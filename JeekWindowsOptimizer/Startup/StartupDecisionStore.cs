using System.Diagnostics;
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
///     Per-machine sharded persistence for <see cref="StartupDecisionLedger" />.
///     <para>
///         To avoid sync conflicts across devices, each machine writes ONLY its own shard under
///         <c>Config/StartupDecisions/&lt;MachineName&gt;.json</c>, while reads merge every shard in
///         that folder plus any legacy <c>Config/StartupDecisions.json</c> (and any legacy conflict
///         copies in <c>Config/</c>) using entry-level LWW with a minimal Hybrid Logical Clock and
///         file-level reset epochs.
///     </para>
/// </summary>
internal sealed class StartupDecisionShardStore
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(StartupDecisionStore));

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SelfWriteSuppressionWindow = TimeSpan.FromSeconds(2);
    private const int RetryLimit = 5;

    private readonly Lock _gate = new();
    private readonly Func<string> _configDirProvider;
    private readonly string _rawMachineName;
    private readonly string _sanitizedMachineName;
    private readonly bool _enableBackgroundTimers;

    private StartupDecisionLedger _ledger = new();
    private readonly Dictionary<string, StartupDecisionEntry> _pendingLocalEdits = new(
        StringComparer.Ordinal
    );
    private bool _pendingForgetAll;
    private long _pendingForgetAllMinEpoch;
    private List<string> _loadedFiles = [];
    private Timer? _saveTimer;
    private Timer? _retryTimer;
    private int _retries;
    private long _lastSelfWriteTimestamp;

    public StartupDecisionShardStore(
        Func<string> configDirProvider,
        string? machineName = null,
        bool enableBackgroundTimers = true
    )
    {
        _configDirProvider = configDirProvider;
        _rawMachineName = string.IsNullOrWhiteSpace(machineName)
            ? Environment.MachineName
            : machineName.Trim();
        _sanitizedMachineName = SanitizeMachineName(_rawMachineName);
        _enableBackgroundTimers = enableBackgroundTimers;
    }

    public string MachineName => _sanitizedMachineName;

    public string ConfigDir => _configDirProvider();

    public string ShardsDir => Path.Combine(ConfigDir, StartupDecisionStore.ShardsDirName);

    /// <summary>This machine's own shard file (<c>Config/StartupDecisions/&lt;MachineName&gt;.json</c>).</summary>
    public string OwnShardPath => Path.Combine(ShardsDir, $"{_sanitizedMachineName}.json");

    /// <summary>Legacy single-file path (<c>Config/StartupDecisions.json</c>), read as a fallback layer when present.</summary>
    public string LegacyFilePath => Path.Combine(ConfigDir, StartupDecisionStore.LegacyFileName);

    /// <summary>
    ///     True when this machine's own shard (or a sole legacy file, or a future-version shard)
    ///     could not be read safely. While poisoned the store refuses to write.
    /// </summary>
    public bool IsPoisoned { get; private set; }

    public string? PoisonReason { get; private set; }

    /// <summary>Paths of shard and legacy files that contributed to the current merged ledger.</summary>
    public IReadOnlyList<string> LoadedFiles
    {
        get
        {
            lock (_gate)
                return [.. _loadedFiles];
        }
    }

    public event Action? ExternallyChanged;

    // ---------- Reads ----------

    public StartupDecisionEntry? Find(string key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        lock (_gate)
            return _ledger.Entries.TryGetValue(key, out var entry) ? entry.Clone() : null;
    }

    public Dictionary<string, StartupDecisionEntry> Snapshot()
    {
        lock (_gate)
            return _ledger.Clone().Entries;
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _ledger.Entries.Count;
        }
    }

    public long Epoch
    {
        get
        {
            lock (_gate)
                return _ledger.Epoch;
        }
    }

    public long Clock
    {
        get
        {
            lock (_gate)
                return _ledger.Clock;
        }
    }

    // ---------- Load / Reload / Sync ----------

    public void Load()
    {
        lock (_gate)
        {
            _pendingLocalEdits.Clear();
            _pendingForgetAll = false;
            _pendingForgetAllMinEpoch = 0;

            var disk = LoadMergedFromDisk(ConfigDir, OwnShardPath);
            ApplyDiskReadLocked(disk, replaceInMemory: true);
        }
    }

    /// <summary>
    ///     Re-reads and merges all shards from disk in place without writing back (avoids sync echo).
    ///     Preserves any unsaved local edits or a pending ForgetAll.
    /// </summary>
    public bool ReloadFromDisk(bool notify)
    {
        bool changed;
        lock (_gate)
        {
            var before = SerializeCanonical(_ledger);
            var disk = LoadMergedFromDisk(ConfigDir, OwnShardPath);
            ApplyDiskReadLocked(disk, replaceInMemory: true);

            var after = SerializeCanonical(_ledger);
            changed = !string.Equals(before, after, StringComparison.Ordinal);
        }

        if (notify && changed)
            RaiseExternallyChanged();

        return changed;
    }

    /// <summary>
    ///     Synchronizes in-memory decisions after switching to a different roamable config directory
    ///     without moving files: merges with any existing shards at the destination, writing this
    ///     machine's shard only when there were local decisions or an epoch to carry over.
    /// </summary>
    public void SyncWithCurrentLocation(bool notify)
    {
        bool changed;
        lock (_gate)
        {
            var before = SerializeCanonical(_ledger);
            var hasCarryOver =
                _ledger.Entries.Count > 0
                || _ledger.Epoch > 0
                || _pendingLocalEdits.Count > 0
                || _pendingForgetAll;

            if (!hasCarryOver)
            {
                var disk = LoadMergedFromDisk(ConfigDir, OwnShardPath);
                ApplyDiskReadLocked(disk, replaceInMemory: true);
            }
            else
            {
                var carryOver = _ledger.Clone();
                FlushLocked(forceWrite: true, carryOver: carryOver);
            }

            var after = SerializeCanonical(_ledger);
            changed = !string.Equals(before, after, StringComparison.Ordinal);
        }

        if (notify && changed)
            RaiseExternallyChanged();
    }

    // ---------- Writes ----------

    public bool Decide(
        string key,
        StartupDecision decision,
        StartupDecisionSnapshot snapshot,
        long? nowUtcMsOverride = null
    )
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        lock (_gate)
        {
            if (IsPoisoned)
                return false;

            RecordLocalDecisionLocked(key, decision, snapshot, nowUtcMsOverride);
            ScheduleSaveLocked();
            return true;
        }
    }

    public bool DecideMany(
        IEnumerable<(string Key, StartupDecision Decision, StartupDecisionSnapshot Snapshot)> decisions,
        long? nowUtcMsOverride = null
    )
    {
        lock (_gate)
        {
            if (IsPoisoned)
                return false;

            var any = false;
            foreach (var (key, decision, snapshot) in decisions)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;
                RecordLocalDecisionLocked(key, decision, snapshot, nowUtcMsOverride);
                any = true;
            }

            if (any)
                ScheduleSaveLocked();

            return any;
        }
    }

    private void RecordLocalDecisionLocked(
        string key,
        StartupDecision decision,
        StartupDecisionSnapshot snapshot,
        long? nowUtcMsOverride
    )
    {
        _ledger.Entries.TryGetValue(key, out var previous);
        var entry = new StartupDecisionEntry
        {
            Decision = decision,
            Clock = _ledger.NextClock(nowUtcMsOverride),
            DecidedAtUtc = nowUtcMsOverride is { } ms
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                : DateTime.UtcNow,
            Machine = _rawMachineName,
            Kind = snapshot.Kind,
            Name = snapshot.Name,
            Publisher = snapshot.Publisher,
            Command = snapshot.Command,
            EntryName = snapshot.EntryName,
            ImagePath = snapshot.ImagePath,
            // Preserve anything a newer build wrote for this key.
            Extra = previous?.Extra is null
                ? null
                : new Dictionary<string, JsonElement>(previous.Extra),
        };

        _ledger.Entries[key] = entry;
        _pendingLocalEdits[key] = entry.Clone();
    }

    /// <summary>
    ///     Forgets every decision everywhere. Implemented as an epoch bump rather than per-entry
    ///     tombstones: on merge the older epoch is discarded wholesale, so the reset survives sync
    ///     instead of being undone by another machine's shard or the legacy file.
    /// </summary>
    public bool ForgetAll()
    {
        lock (_gate)
        {
            if (IsPoisoned)
                return false;

            _ledger.Epoch++;
            _ledger.Entries.Clear();
            _pendingLocalEdits.Clear();
            _pendingForgetAll = true;
            _pendingForgetAllMinEpoch = _ledger.Epoch;
            ScheduleSaveLocked();
            return true;
        }
    }

    /// <summary>Writes any pending changes immediately to this machine's own shard.</summary>
    public bool Flush(bool forceWrite = false, long? nowUtcMsOverride = null)
    {
        lock (_gate)
        {
            _saveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return FlushLocked(forceWrite, carryOver: null, nowUtcMsOverride);
        }
    }

    private bool FlushLocked(
        bool forceWrite,
        StartupDecisionLedger? carryOver,
        long? nowUtcMsOverride = null
    )
    {
        if (IsPoisoned)
            return false;

        if (!forceWrite && !_pendingForgetAll && _pendingLocalEdits.Count == 0 && carryOver is null)
            return true;

        var configDir = ConfigDir;
        var shardsDir = Path.Combine(configDir, StartupDecisionStore.ShardsDirName);
        var shardPath = Path.Combine(shardsDir, $"{_sanitizedMachineName}.json");

        try
        {
            MarkSelfWrite();
            Directory.CreateDirectory(shardsDir);
            using var lease = SharedDataFile.Acquire(shardPath);

            var disk = LoadMergedFromDisk(configDir, shardPath);
            if (TryCheckPoison(disk, out var poisonReason))
            {
                if (disk.HasAnyReadableFile && disk.OwnShardError is null)
                    _ledger = disk.Merged;
                EnterPoisonedLocked(poisonReason);
                return false;
            }

            var merged = disk.HasAnyReadableFile
                ? disk.Merged.Clone()
                : new StartupDecisionLedger();

            if (carryOver is not null)
                merged = StartupDecisionLedger.Merge(merged, carryOver);

            var diskMaxClock = merged.GetHighestClock();

            if (_pendingForgetAll)
            {
                var targetEpoch =
                    merged.Epoch >= _pendingForgetAllMinEpoch
                        ? merged.Epoch + 1
                        : _pendingForgetAllMinEpoch;
                merged.Epoch = targetEpoch;
                merged.Entries.Clear();
                merged.Clock = Math.Max(merged.Clock, Math.Max(diskMaxClock, _ledger.GetHighestClock()));
            }

            // Local edits performed right now must strictly beat any remote shard that
            // arrived on disk before the watcher debounced a reload (handles clock skew too).
            foreach (var (key, edit) in _pendingLocalEdits)
            {
                var toApply = edit.Clone();
                var nowMs = nowUtcMsOverride ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (toApply.Clock <= diskMaxClock)
                {
                    toApply.Clock = nowMs > diskMaxClock ? nowMs : diskMaxClock + 1;
                    diskMaxClock = toApply.Clock;
                }
                else
                {
                    diskMaxClock = toApply.Clock;
                }

                if (merged.Entries.TryGetValue(key, out var existing) && existing.Extra is not null)
                {
                    toApply.Extra ??= new Dictionary<string, JsonElement>();
                    foreach (var (extraKey, extraVal) in existing.Extra)
                        toApply.Extra.TryAdd(extraKey, extraVal);
                }

                merged.Entries[key] = toApply;
            }

            merged.Clock = Math.Max(merged.Clock, diskMaxClock);
            merged.Normalize();

            var toWrite = merged.CloneSorted(ownerMachine: _sanitizedMachineName);
            var json = JsonSerializer.Serialize(toWrite, JsonOptions);

            MarkSelfWrite();
            WriteAllTextAtomicWithRetry(shardPath, json);
            MarkSelfWrite();

            _ledger = toWrite;
            _pendingLocalEdits.Clear();
            _pendingForgetAll = false;
            _pendingForgetAllMinEpoch = 0;

            var loaded = new List<string>(disk.LoadedFiles);
            var fullShardPath = Path.GetFullPath(shardPath);
            if (!loaded.Contains(fullShardPath, StringComparer.OrdinalIgnoreCase))
                loaded.Add(fullShardPath);
            loaded.Sort(StringComparer.OrdinalIgnoreCase);
            _loadedFiles = loaded;

            if (disk.UnreadableOtherShards.Count > 0)
                ScheduleRetryLocked();
            else
                LeavePoisonedLocked();
            return true;
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to save startup decision shard to {shardPath}");
            return false;
        }
    }

    // ---------- Watcher matching & self-write suppression ----------

    internal bool IsInterestingChange(string relativePath, WatcherChangeTypes changeType)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var normalized = relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        if (normalized.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            return false;

        var sepIndex = normalized.IndexOf(Path.DirectorySeparatorChar);
        if (sepIndex < 0)
        {
            // Direct child of Config/: legacy StartupDecisions.json, legacy conflict copies,
            // or the StartupDecisions directory itself.
            if (
                string.Equals(
                    normalized,
                    StartupDecisionStore.ShardsDirName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                // Ignore directory LastWrite noise caused by unrelated files (like .tmp) inside
                // StartupDecisions/. Only react when the folder is created, renamed, or deleted.
                if (changeType == WatcherChangeTypes.Changed)
                    return false;
                return !IsRecentSelfWrite();
            }

            return string.Equals(
                    normalized,
                    StartupDecisionStore.LegacyFileName,
                    StringComparison.OrdinalIgnoreCase
                )
                || StartupDecisionStore.LegacyConflictCopyPattern().IsMatch(normalized);
        }

        // Direct child inside StartupDecisions\: StartupDecisions/<MachineName>.json
        var dirName = normalized[..sepIndex];
        var remainder = normalized[(sepIndex + 1)..];
        if (
            !string.Equals(
                dirName,
                StartupDecisionStore.ShardsDirName,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return false;

        if (remainder.Contains(Path.DirectorySeparatorChar))
            return false;

        if (!remainder.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;

        // Only suppress self-write for this machine's own shard; never suppress another
        // machine's shard arriving in the same window.
        var ownFileName = $"{_sanitizedMachineName}.json";
        if (
            string.Equals(remainder, ownFileName, StringComparison.OrdinalIgnoreCase)
            && IsRecentSelfWrite()
        )
            return false;

        return true;
    }

    private void MarkSelfWrite() =>
        Volatile.Write(ref _lastSelfWriteTimestamp, Stopwatch.GetTimestamp());

    internal bool IsRecentSelfWrite()
    {
        var stamp = Volatile.Read(ref _lastSelfWriteTimestamp);
        return stamp != 0 && Stopwatch.GetElapsedTime(stamp) <= SelfWriteSuppressionWindow;
    }

    // ---------- Disk loading & merge ----------

    private void ApplyDiskReadLocked(DiskLoadResult disk, bool replaceInMemory)
    {
        if (TryCheckPoison(disk, out var poisonReason))
        {
            if (disk.HasAnyReadableFile)
                _ledger = disk.Merged;
            _loadedFiles = disk.LoadedFiles;
            EnterPoisonedLocked(poisonReason);
            return;
        }

        var next = disk.HasAnyReadableFile ? disk.Merged.Clone() : new StartupDecisionLedger();
        if (!replaceInMemory)
            next = StartupDecisionLedger.Merge(_ledger, next);

        // Preserve any unsaved local reset or edits if a reload raced with a pending save.
        if (_pendingForgetAll)
        {
            if (next.Epoch < _pendingForgetAllMinEpoch)
            {
                next.Epoch = _pendingForgetAllMinEpoch;
                next.Entries.Clear();
            }
            else
            {
                _pendingForgetAllMinEpoch = next.Epoch + 1;
                next.Epoch = _pendingForgetAllMinEpoch;
                next.Entries.Clear();
            }
        }

        var maxClock = Math.Max(next.GetHighestClock(), _ledger.GetHighestClock());
        foreach (var (key, edit) in _pendingLocalEdits)
        {
            var candidate = edit.Clone();
            if (candidate.Clock > maxClock)
                maxClock = candidate.Clock;
            if (!next.Entries.TryGetValue(key, out var existing) || StartupDecisionLedger.Wins(candidate, existing))
                next.Entries[key] = candidate;
        }

        next.Clock = Math.Max(next.Clock, maxClock);
        next.Normalize();

        _ledger = next;
        _loadedFiles = disk.LoadedFiles;
        LeavePoisonedLocked();

        if (disk.UnreadableOtherShards.Count > 0)
        {
            foreach (var (file, reason) in disk.UnreadableOtherShards)
                Log.ZLogWarning($"Could not read remote startup decision shard {file}: {reason}");
            ScheduleRetryLocked();
        }
    }

    private static bool TryCheckPoison(DiskLoadResult disk, out string reason)
    {
        if (disk.FutureVersionError is not null)
        {
            reason = disk.FutureVersionError;
            return true;
        }

        if (disk.OwnShardError is not null)
        {
            reason = disk.OwnShardError;
            return true;
        }

        if (disk.LegacyFileError is not null && !disk.OwnShardExisted)
        {
            reason = disk.LegacyFileError;
            return true;
        }

        reason = "";
        return false;
    }

    internal static DiskLoadResult LoadMergedFromDisk(string configDir, string ownShardPath)
    {
        var loadedFiles = new List<string>();
        var unreadableOtherShards = new List<(string Path, string Error)>();
        StartupDecisionLedger? merged = null;
        string? futureVersionError = null;
        string? ownShardError = null;
        string? legacyFileError = null;
        var ownShardExisted = false;

        var fullOwnShardPath = Path.GetFullPath(ownShardPath);

        void MergeFile(string rawPath, bool isOwnShard, bool isLegacyPrimary)
        {
            var fullPath = Path.GetFullPath(rawPath);
            if (!TryReadLedgerFile(fullPath, out var ledger, out var error))
            {
                if (error is null)
                    return;

                if (isOwnShard)
                {
                    ownShardExisted = true;
                    ownShardError = error;
                }
                else if (isLegacyPrimary)
                {
                    legacyFileError = error;
                }
                else
                {
                    unreadableOtherShards.Add((fullPath, error));
                }
                return;
            }

            if (isOwnShard)
                ownShardExisted = true;

            loadedFiles.Add(fullPath);

            if (ledger!.Version > StartupDecisionLedger.CurrentVersion)
            {
                futureVersionError ??=
                    $"File version {ledger.Version} in {Path.GetFileName(fullPath)} is newer than {StartupDecisionLedger.CurrentVersion}.";
            }

            merged = merged is null ? ledger : StartupDecisionLedger.Merge(merged, ledger);
        }

        // 1. Legacy Config/StartupDecisions.json (read-only fallback layer for seamless upgrades).
        var legacyPath = Path.Combine(configDir, StartupDecisionStore.LegacyFileName);
        if (File.Exists(legacyPath))
            MergeFile(legacyPath, isOwnShard: false, isLegacyPrimary: true);

        // 2. Any pre-migration conflict copies of StartupDecisions.json in Config/ (read-only).
        if (Directory.Exists(configDir))
        {
            string[] legacyCandidates;
            try
            {
                legacyCandidates = Directory.GetFiles(
                    configDir,
                    "StartupDecisions*.json",
                    SearchOption.TopDirectoryOnly
                );
                Array.Sort(legacyCandidates, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.ZLogWarning(ex, $"Failed to scan {configDir} for legacy startup decision files");
                legacyCandidates = [];
            }

            foreach (var candidate in legacyCandidates)
            {
                var name = Path.GetFileName(candidate);
                if (string.Equals(name, StartupDecisionStore.LegacyFileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!StartupDecisionStore.LegacyConflictCopyPattern().IsMatch(name))
                    continue;
                MergeFile(candidate, isOwnShard: false, isLegacyPrimary: false);
            }
        }

        // 3. All per-machine shards under Config/StartupDecisions/*.json.
        var shardsDir = Path.Combine(configDir, StartupDecisionStore.ShardsDirName);
        if (Directory.Exists(shardsDir))
        {
            string[] shardFiles;
            try
            {
                shardFiles = Directory.GetFiles(shardsDir, "*.json", SearchOption.TopDirectoryOnly);
                Array.Sort(shardFiles, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.ZLogWarning(ex, $"Failed to enumerate startup decision shards in {shardsDir}");
                shardFiles = [];
            }

            foreach (var file in shardFiles)
            {
                var fullPath = Path.GetFullPath(file);
                var isOwn = string.Equals(fullPath, fullOwnShardPath, StringComparison.OrdinalIgnoreCase);
                MergeFile(fullPath, isOwnShard: isOwn, isLegacyPrimary: false);
            }
        }

        loadedFiles.Sort(StringComparer.OrdinalIgnoreCase);
        return new DiskLoadResult(
            merged ?? new StartupDecisionLedger(),
            merged is not null,
            loadedFiles,
            ownShardExisted,
            ownShardError,
            legacyFileError,
            futureVersionError,
            unreadableOtherShards
        );
    }

    /// <summary>
    ///     Reads one ledger file with transient-lock retries. Returns false with a null error when
    ///     the file simply does not exist, and false with an error when it exists but could not be
    ///     read or parsed.
    /// </summary>
    internal static bool TryReadLedgerFile(
        string path,
        out StartupDecisionLedger? ledger,
        out string? error
    )
    {
        ledger = null;
        error = null;

        if (!TryReadAllTextWithRetry(path, out var text, out var readError))
        {
            error = readError;
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // A sync tool can briefly expose a zero-length file mid-replacement.
            error = "File is empty.";
            return false;
        }

        try
        {
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

    private static bool TryReadAllTextWithRetry(
        string path,
        out string? content,
        out string? error
    )
    {
        const int maxAttempts = 4;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                );
                using var reader = new StreamReader(stream);
                content = reader.ReadToEnd();
                error = null;
                return true;
            }
            catch (FileNotFoundException)
            {
                content = null;
                error = null;
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                content = null;
                error = null;
                return false;
            }
            catch (IOException ex) when (attempt + 1 < maxAttempts)
            {
                Thread.Sleep(50 * (attempt + 1));
                error = ex.Message;
            }
            catch (UnauthorizedAccessException ex) when (attempt + 1 < maxAttempts)
            {
                Thread.Sleep(50 * (attempt + 1));
                error = ex.Message;
            }
            catch (Exception ex)
            {
                content = null;
                error = ex.Message;
                return false;
            }
        }

        content = null;
        error = $"Could not read {Path.GetFileName(path)} after {maxAttempts} attempts.";
        return false;
    }

    private static void WriteAllTextAtomicWithRetry(string path, string contents)
    {
        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                SharedDataFile.WriteAllTextAtomic(path, contents);
                return;
            }
            catch (IOException) when (attempt + 1 < maxAttempts)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt + 1 < maxAttempts)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    // ---------- Timers & poison state ----------

    private void ScheduleSaveLocked()
    {
        if (!_enableBackgroundTimers)
            return;

        _saveTimer ??= new Timer(
            _ => Flush(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
        _saveTimer.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
    }

    private void EnterPoisonedLocked(string reason)
    {
        IsPoisoned = true;
        PoisonReason = reason;
        Log.ZLogWarning(
            $"Startup decisions unreadable ({reason}); writes are blocked until a good read."
        );
        ScheduleRetryLocked();
    }

    private void ScheduleRetryLocked()
    {
        if (!_enableBackgroundTimers || _retries >= RetryLimit)
            return;

        _retries++;
        _retryTimer ??= new Timer(
            _ => ReloadFromDisk(notify: true),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
        _retryTimer.Change(RetryDelay, Timeout.InfiniteTimeSpan);
    }

    private void LeavePoisonedLocked()
    {
        if (IsPoisoned)
            Log.ZLogInformation($"Startup decisions readable again; writes re-enabled.");
        IsPoisoned = false;
        PoisonReason = null;
        _retries = 0;
    }

    private void RaiseExternallyChanged()
    {
        try
        {
            ExternallyChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Startup decision change handler failed");
        }
    }

    private static string SerializeCanonical(StartupDecisionLedger ledger) =>
        JsonSerializer.Serialize(ledger.CloneSorted(ownerMachine: null), JsonOptions);

    // ---------- Machine name sanitization ----------

    internal static string SanitizeMachineName(string? rawName)
    {
        var trimmed = (rawName ?? "").Trim();
        if (trimmed.Length == 0)
            return "default";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = trimmed.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (Array.IndexOf(invalid, c) >= 0 || c is '/' or '\\' or ':')
                chars[i] = '_';
        }

        var cleaned = new string(chars).Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0)
            return "default";

        if (IsReservedWindowsDeviceName(cleaned))
            cleaned = "_" + cleaned;

        return cleaned;
    }

    private static bool IsReservedWindowsDeviceName(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper
            is "CON"
                or "PRN"
                or "AUX"
                or "NUL"
                or "COM1"
                or "COM2"
                or "COM3"
                or "COM4"
                or "COM5"
                or "COM6"
                or "COM7"
                or "COM8"
                or "COM9"
                or "LPT1"
                or "LPT2"
                or "LPT3"
                or "LPT4"
                or "LPT5"
                or "LPT6"
                or "LPT7"
                or "LPT8"
                or "LPT9";
    }

    // ---------- Test seam ----------

    internal void ReplaceForTesting(StartupDecisionLedger ledger)
    {
        lock (_gate)
        {
            ledger.Normalize();
            _ledger = ledger;
        }
    }

    internal sealed record DiskLoadResult(
        StartupDecisionLedger Merged,
        bool HasAnyReadableFile,
        List<string> LoadedFiles,
        bool OwnShardExisted,
        string? OwnShardError,
        string? LegacyFileError,
        string? FutureVersionError,
        List<(string Path, string Error)> UnreadableOtherShards
    );
}

/// <summary>
///     Static facade for the live application's startup decision store.
///     Each machine writes only its own shard at <c>Config/StartupDecisions/&lt;MachineName&gt;.json</c>
///     and reads/merges every shard in <c>Config/StartupDecisions/</c> plus any legacy
///     <c>Config/StartupDecisions.json</c>.
/// </summary>
public static partial class StartupDecisionStore
{
    public const string ShardsDirName = "StartupDecisions";
    public const string LegacyFileName = "StartupDecisions.json";

    private static readonly StartupDecisionShardStore Live = new(
        () => AppSettingsStore.CurrentRoamingConfigDir
    );
    private static bool _wired;
    private static readonly Lock WireGate = new();

    public static string MachineName => Live.MachineName;

    public static string ShardsDir => Live.ShardsDir;

    /// <summary>This machine's own shard file (<c>Config/StartupDecisions/&lt;MachineName&gt;.json</c>).</summary>
    public static string FilePath => Live.OwnShardPath;

    /// <summary>Legacy single-file path (<c>Config/StartupDecisions.json</c>), read as a fallback layer when present.</summary>
    public static string LegacyFilePath => Live.LegacyFilePath;

    /// <summary>Paths of shard and legacy files that contributed to the current merged ledger.</summary>
    public static IReadOnlyList<string> LoadedFiles => Live.LoadedFiles;

    public static bool IsPoisoned => Live.IsPoisoned;

    public static string? PoisonReason => Live.PoisonReason;

    /// <summary>Raised on a worker thread after the merged ledger changed because of an external edit or sync.</summary>
    public static event Action? ExternallyChanged
    {
        add => Live.ExternallyChanged += value;
        remove => Live.ExternallyChanged -= value;
    }

    public static void Load()
    {
        lock (WireGate)
        {
            if (!_wired)
            {
                _wired = true;
                AppSettingsStore.RegisterConfigWatcher(
                    (relativePath, changeType) => Live.IsInterestingChange(relativePath, changeType),
                    () => Live.ReloadFromDisk(notify: true)
                );
                AppSettingsStore.BeforeRoamingConfigLocationChange += OnBeforeConfigLocationChange;
                AppSettingsStore.RoamingConfigLocationChanged += OnConfigLocationChanged;
            }
        }

        Live.Load();
    }

    private static void OnBeforeConfigLocationChange() => Live.Flush();

    private static void OnConfigLocationChanged(bool movedFiles)
    {
        if (movedFiles)
            Live.ReloadFromDisk(notify: true);
        else
            Live.SyncWithCurrentLocation(notify: true);
    }

    public static StartupDecisionEntry? Find(string key) => Live.Find(key);

    public static Dictionary<string, StartupDecisionEntry> Snapshot() => Live.Snapshot();

    public static int Count => Live.Count;

    public static long Epoch => Live.Epoch;

    public static long Clock => Live.Clock;

    public static bool Decide(
        string key,
        StartupDecision decision,
        StartupDecisionSnapshot snapshot
    ) => Live.Decide(key, decision, snapshot);

    public static bool DecideMany(
        IEnumerable<(string Key, StartupDecision Decision, StartupDecisionSnapshot Snapshot)> decisions
    ) => Live.DecideMany(decisions);

    public static bool ForgetAll() => Live.ForgetAll();

    /// <summary>Writes any pending changes immediately to this machine's own shard. Called on shutdown.</summary>
    public static bool Flush() => Live.Flush();

    /// <summary>Re-reads and merges all shards from disk in place without writing back.</summary>
    public static bool Reload(bool notify = true) => Live.ReloadFromDisk(notify);

    [GeneratedRegex(@"^StartupDecisions[-. (][^\\/]*\.json$", RegexOptions.IgnoreCase)]
    internal static partial Regex LegacyConflictCopyPattern();

    internal static bool TryReadLedgerFile(
        string path,
        out StartupDecisionLedger? ledger,
        out string? error
    ) => StartupDecisionShardStore.TryReadLedgerFile(path, out ledger, out error);

    internal static string SanitizeMachineName(string? rawName) =>
        StartupDecisionShardStore.SanitizeMachineName(rawName);

    /// <summary>Replaces the in-memory ledger without touching disk. Debug probes only.</summary>
    internal static void ReplaceForTesting(StartupDecisionLedger ledger) =>
        Live.ReplaceForTesting(ledger);
}
