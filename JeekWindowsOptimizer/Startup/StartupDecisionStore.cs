using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer.Startup;

/// <summary>Human-readable fields stored with a decision so an absent item can still be listed.</summary>
public readonly record struct StartupDecisionSnapshot(
    string? Kind,
    string? Name,
    string? Publisher,
    string? Command,
    string? EntryName = null,
    string? ImagePath = null
);

/// <summary>
///     Persistence for <see cref="StartupDecisionLedger" />: its own file in the roaming Config
///     folder, so a user who points that folder at a synced drive gets their decisions on every
///     machine.
///     <para>
///         It deliberately does not use <see cref="JsonSettingsFile.TryMergeAndWrite" />. That merge
///         is per JSON property, and the whole ledger is one property, so two machines each adding
///         entries would overwrite one another. Every read and write here goes through
///         <see cref="StartupDecisionLedger.Merge" /> instead, which resolves per entry.
///     </para>
/// </summary>
public static partial class StartupDecisionStore
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(StartupDecisionStore));

    public const string FileName = "StartupDecisions.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Lock Gate = new();
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PoisonRetryDelay = TimeSpan.FromSeconds(3);
    private const int PoisonRetryLimit = 5;

    private static StartupDecisionLedger _ledger = new();
    private static Timer? _saveTimer;
    private static Timer? _poisonRetryTimer;
    private static int _poisonRetries;
    private static bool _loaded;

    /// <summary>
    ///     True when the file exists but could not be read. While poisoned the store refuses to
    ///     write: saving an empty or stale in-memory ledger over a good file would destroy
    ///     decisions, and a sync tool replacing the file makes a failed read completely normal.
    /// </summary>
    public static bool IsPoisoned { get; private set; }

    public static string? PoisonReason { get; private set; }

    /// <summary>Raised on a worker thread after the ledger changed because of an external edit or sync.</summary>
    public static event Action? ExternallyChanged;

    public static string FilePath =>
        Path.Combine(AppSettingsStore.CurrentRoamingConfigDir, FileName);

    public static void Load()
    {
        lock (Gate)
        {
            if (!_loaded)
            {
                _loaded = true;
                AppSettingsStore.RegisterConfigWatcher(IsInteresting, OnConfigChanged);
                AppSettingsStore.RoamingConfigLocationChanged += OnConfigLocationChanged;
            }

            LoadLocked();
        }
    }

    private static void OnConfigLocationChanged() => ReloadFromDisk(notify: true);

    private static void LoadLocked()
    {
        var path = FilePath;
        if (!TryReadLedgerFile(path, out var disk, out var error))
        {
            if (error is not null)
            {
                EnterPoisoned(error);
                return;
            }

            // No file yet: a fresh machine, not a failure.
            _ledger = new StartupDecisionLedger();
            LeavePoisoned();
            return;
        }

        _ledger = disk!;
        LeavePoisoned();
        AbsorbConflictCopiesLocked();
    }

    // ---------- Reads ----------

    /// <summary>The decision for a key, or null when the user never decided it.</summary>
    public static StartupDecisionEntry? Find(string key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        lock (Gate)
            return _ledger.Entries.TryGetValue(key, out var entry) ? entry.Clone() : null;
    }

    public static Dictionary<string, StartupDecisionEntry> Snapshot()
    {
        lock (Gate)
            return _ledger.Clone().Entries;
    }

    public static int Count
    {
        get
        {
            lock (Gate)
                return _ledger.Entries.Count;
        }
    }

    public static long Epoch
    {
        get
        {
            lock (Gate)
                return _ledger.Epoch;
        }
    }

    // ---------- Writes ----------

    /// <summary>
    ///     Records a decision, stamping it with the next Lamport counter so it wins over anything
    ///     this ledger has seen. Returns false when the store is poisoned and refuses to write.
    /// </summary>
    public static bool Decide(string key, StartupDecision decision, StartupDecisionSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        lock (Gate)
        {
            if (IsPoisoned)
                return false;

            _ledger.Entries.TryGetValue(key, out var previous);
            _ledger.Entries[key] = new StartupDecisionEntry
            {
                Decision = decision,
                Clock = _ledger.NextClock(),
                DecidedAtUtc = DateTime.UtcNow,
                Machine = Environment.MachineName,
                Kind = snapshot.Kind,
                Name = snapshot.Name,
                Publisher = snapshot.Publisher,
                Command = snapshot.Command,
                EntryName = snapshot.EntryName,
                ImagePath = snapshot.ImagePath,
                // Preserve anything a newer build wrote for this key.
                Extra = previous?.Extra,
            };

            ScheduleSaveLocked();
            return true;
        }
    }

    public static bool DecideMany(
        IEnumerable<(string Key, StartupDecision Decision, StartupDecisionSnapshot Snapshot)> decisions
    )
    {
        var any = false;
        foreach (var (key, decision, snapshot) in decisions)
            any |= Decide(key, decision, snapshot);
        return any;
    }

    /// <summary>
    ///     Forgets every decision everywhere. Implemented as an epoch bump rather than per-entry
    ///     tombstones: on merge the older epoch is discarded wholesale, so the reset survives sync
    ///     instead of being undone by a machine that still holds the old entries.
    /// </summary>
    public static bool ForgetAll()
    {
        lock (Gate)
        {
            if (IsPoisoned)
                return false;

            _ledger.Epoch++;
            _ledger.Entries.Clear();
            ScheduleSaveLocked();
            return true;
        }
    }

    private static void ScheduleSaveLocked()
    {
        _saveTimer ??= new Timer(
            _ => Flush(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
        _saveTimer.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Writes any pending changes immediately. Called on shutdown.</summary>
    public static void Flush()
    {
        lock (Gate)
        {
            _saveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (IsPoisoned)
                return;

            var path = FilePath;
            try
            {
                using var lease = SharedDataFile.Acquire(path);

                // The cross-process mutex only covers this machine. Another machine writing the
                // same synced file cannot be locked out, so correctness has to come from the merge.
                if (TryReadLedgerFile(path, out var disk, out var error))
                {
                    if (disk!.Version > StartupDecisionLedger.CurrentVersion)
                    {
                        // Written by a newer build. Reading is fine, rewriting would drop what we
                        // do not understand, so leave it alone.
                        _ledger = disk;
                        EnterPoisoned(
                            $"File version {disk.Version} is newer than {StartupDecisionLedger.CurrentVersion}."
                        );
                        return;
                    }

                    _ledger = StartupDecisionLedger.Merge(_ledger, disk);
                }
                else if (error is not null)
                {
                    EnterPoisoned(error);
                    return;
                }

                _ledger.Normalize();
                SharedDataFile.WriteAllTextAtomic(
                    path,
                    JsonSerializer.Serialize(_ledger, JsonOptions)
                );
            }
            catch (Exception ex)
            {
                Log.ZLogWarning(ex, $"Failed to save startup decisions to {path}");
            }
        }
    }

    // ---------- External change ----------

    /// <summary>
    ///     Matches the ledger itself plus the conflict copies the common sync tools leave behind:
    ///     Syncthing's <c>.sync-conflict-</c>, OneDrive's machine-name suffix and Dropbox's
    ///     "conflicted copy". Because entries merge, those copies can be absorbed automatically
    ///     instead of asking the user to reconcile them by hand.
    /// </summary>
    private static bool IsInteresting(string changedName)
    {
        var name = Path.GetFileName(changedName);
        if (string.Equals(name, FileName, StringComparison.OrdinalIgnoreCase))
            return true;
        return ConflictCopyPattern().IsMatch(name);
    }

    [GeneratedRegex(@"^StartupDecisions[-. (][^\\/]*\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex ConflictCopyPattern();

    private static void OnConfigChanged() => ReloadFromDisk(notify: true);

    private static void ReloadFromDisk(bool notify)
    {
        lock (Gate)
        {
            var before = JsonSerializer.Serialize(_ledger, JsonOptions);
            var path = FilePath;

            if (!TryReadLedgerFile(path, out var disk, out var error))
            {
                if (error is not null)
                {
                    EnterPoisoned(error);
                    return;
                }
            }
            else
            {
                // Reloading merges rather than replaces, which makes it idempotent: seeing our own
                // write come back from the watcher is harmless and needs no echo suppression.
                _ledger = StartupDecisionLedger.Merge(_ledger, disk!);
                LeavePoisoned();
            }

            if (AbsorbConflictCopiesLocked())
                ScheduleSaveLocked();

            var after = JsonSerializer.Serialize(_ledger, JsonOptions);
            if (!notify || string.Equals(before, after, StringComparison.Ordinal))
                return;
        }

        try
        {
            ExternallyChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Startup decision change handler failed");
        }
    }

    private static bool AbsorbConflictCopiesLocked()
    {
        var absorbed = AbsorbFrom(
            AppSettingsStore.CurrentRoamingConfigDir,
            _ledger,
            deleteAbsorbed: true,
            out var merged
        );
        if (absorbed)
            _ledger = merged;
        return absorbed;
    }

    /// <summary>
    ///     Merges every conflict copy in a folder into <paramref name="ledger" />. Split out from the
    ///     store's own state so a probe can exercise it against a temporary folder.
    /// </summary>
    internal static bool AbsorbFrom(
        string dir,
        StartupDecisionLedger ledger,
        bool deleteAbsorbed,
        out StartupDecisionLedger merged
    )
    {
        merged = ledger;
        var absorbed = false;

        try
        {
            if (!Directory.Exists(dir))
                return false;

            foreach (var file in Directory.EnumerateFiles(dir, "StartupDecisions*.json").ToList())
            {
                var name = Path.GetFileName(file);
                if (string.Equals(name, FileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!ConflictCopyPattern().IsMatch(name))
                    continue;

                if (!TryReadLedgerFile(file, out var copy, out _) || copy is null)
                    continue;

                merged = StartupDecisionLedger.Merge(merged, copy);
                absorbed = true;

                if (!deleteAbsorbed)
                    continue;

                try
                {
                    File.Delete(file);
                    Log.ZLogInformation($"Absorbed startup decision conflict copy {name}");
                }
                catch (Exception ex)
                {
                    Log.ZLogWarning(ex, $"Absorbed but could not delete conflict copy {name}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to scan for startup decision conflict copies in {dir}");
        }

        return absorbed;
    }

    /// <summary>
    ///     Reads the file. Returns false with a null error when the file simply does not exist, and
    ///     false with an error when it exists but could not be read or parsed.
    /// </summary>
    internal static bool TryReadLedgerFile(
        string path,
        out StartupDecisionLedger? ledger,
        out string? error
    )
    {
        ledger = null;
        error = null;

        try
        {
            if (!File.Exists(path))
                return false;

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                // A sync tool can briefly expose a zero-length file mid-replacement.
                error = "File is empty.";
                return false;
            }

            var parsed = JsonSerializer.Deserialize<StartupDecisionLedger>(text, JsonOptions);
            if (parsed is null)
            {
                error = "File did not parse into a ledger.";
                return false;
            }

            parsed.Normalize();
            ledger = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void EnterPoisoned(string reason)
    {
        IsPoisoned = true;
        PoisonReason = reason;
        Log.ZLogWarning(
            $"Startup decisions unreadable ({reason}); writes are blocked until a good read."
        );

        if (_poisonRetries >= PoisonRetryLimit)
            return;

        _poisonRetries++;
        _poisonRetryTimer ??= new Timer(
            _ => ReloadFromDisk(notify: true),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
        _poisonRetryTimer.Change(PoisonRetryDelay, Timeout.InfiniteTimeSpan);
    }

    private static void LeavePoisoned()
    {
        if (IsPoisoned)
            Log.ZLogInformation($"Startup decisions readable again; writes re-enabled.");
        IsPoisoned = false;
        PoisonReason = null;
        _poisonRetries = 0;
    }

    // ---------- Test seam ----------

    /// <summary>Replaces the in-memory ledger without touching disk. Debug probes only.</summary>
    internal static void ReplaceForTesting(StartupDecisionLedger ledger)
    {
        lock (Gate)
        {
            ledger.Normalize();
            _ledger = ledger;
        }
    }
}
