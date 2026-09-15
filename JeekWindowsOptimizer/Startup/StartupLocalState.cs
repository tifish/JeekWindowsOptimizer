using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer.Startup;

/// <summary>The machine-local half of the startup feature's state. Never roams.</summary>
internal sealed class StartupLocalSettings
{
    /// <summary>
    ///     When this machine's first sweep was accepted. Until it is set, everything already present
    ///     is treated as the baseline rather than as hundreds of items awaiting confirmation.
    /// </summary>
    public DateTime? BaselineTakenUtc { get; set; }

    /// <summary>First time each key was seen here, so the UI can sort and mark what is new.</summary>
    public Dictionary<string, DateTime>? FirstSeenUtc { get; set; }

    /// <summary>
    ///     Start type a service or driver had before it was denied, so allowing it again restores
    ///     Automatic or Manual exactly as it was instead of guessing.
    /// </summary>
    public Dictionary<string, int>? OriginalServiceStart { get; set; }
}

/// <summary>
///     Machine-local companion to <see cref="StartupDecisionStore" />.
///     <para>
///         The split matters. What roams is the user's intent: allow this, deny that. What stays
///         here is how to put <em>this</em> machine back the way it was, plus when things were first
///         seen here. Syncing a restore value would be actively wrong, since the same service can
///         legitimately be Automatic on one machine and Manual on another.
///     </para>
/// </summary>
public static class StartupLocalState
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(StartupLocalState));

    private const string FileName = "StartupLocal.json";

    private static readonly Lock Gate = new();
    private static StartupLocalSettings _settings = new();
    private static bool _loaded;

    private static string FilePath => Path.Combine(AppSettingsStore.LocalConfigDir, FileName);

    public static void Load()
    {
        lock (Gate)
        {
            JsonSettingsFile.TryLoad(FilePath, out StartupLocalSettings settings);
            settings.FirstSeenUtc ??= new Dictionary<string, DateTime>(StringComparer.Ordinal);
            settings.OriginalServiceStart ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _settings = settings;
            _loaded = true;
        }
    }

    private static void EnsureLoaded()
    {
        if (!_loaded)
            Load();
    }

    private static void Save()
    {
        try
        {
            SharedDataFile.WriteAllTextAtomic(FilePath, JsonSettingsFile.Serialize(_settings));
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to save machine-local startup state");
        }
    }

    /// <summary>False until the user has accepted this machine's first sweep.</summary>
    public static bool HasBaseline
    {
        get
        {
            lock (Gate)
            {
                EnsureLoaded();
                return _settings.BaselineTakenUtc is not null;
            }
        }
    }

    public static void MarkBaselineTaken()
    {
        lock (Gate)
        {
            EnsureLoaded();
            _settings.BaselineTakenUtc = DateTime.UtcNow;
            Save();
        }
    }

    /// <summary>Records the first time each key was seen here and returns the stored timestamps.</summary>
    public static Dictionary<string, DateTime> RecordSeen(IEnumerable<string> keys)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var seen = _settings.FirstSeenUtc!;
            var now = DateTime.UtcNow;
            var changed = false;

            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key) || seen.ContainsKey(key))
                    continue;
                seen[key] = now;
                changed = true;
            }

            if (changed)
                Save();

            return new Dictionary<string, DateTime>(seen, StringComparer.Ordinal);
        }
    }

    public static void RememberServiceStart(string serviceName, int start)
    {
        lock (Gate)
        {
            EnsureLoaded();
            // Only the pre-denial value is interesting. Overwriting it with the disabled value we
            // just wrote would destroy the only record of what to restore.
            if (start == 4 || _settings.OriginalServiceStart!.ContainsKey(serviceName))
                return;

            _settings.OriginalServiceStart[serviceName] = start;
            Save();
        }
    }

    /// <summary>The remembered pre-denial start type, or Manual when nothing was recorded.</summary>
    public static int RecallServiceStart(string serviceName)
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _settings.OriginalServiceStart!.TryGetValue(serviceName, out var start)
                ? start
                : 3;
        }
    }

    public static void ForgetServiceStart(string serviceName)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (_settings.OriginalServiceStart!.Remove(serviceName))
                Save();
        }
    }
}
