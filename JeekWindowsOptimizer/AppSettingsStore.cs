using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Styling;
using JeekTools;

namespace JeekWindowsOptimizer;

internal enum AutoUpdateInterval
{
    Every6Hours,
    Daily,
    Weekly,
    Never,
}

/// <summary>
///     Machine-bound settings that must not roam (e.g. which storage location this install uses).
///     Always stored in <c>%LocalAppData%\&lt;App&gt;\Config</c>, regardless of storage location.
/// </summary>
internal sealed class MachineSettings
{
    public StorageLocation StorageLocation { get; set; } = StorageLocation.UserDirectory;

    public string? CustomStoragePath { get; set; }
}

/// <summary>Machine-independent user preferences. Stored according to the active <see cref="StorageLocation" />.</summary>
internal sealed class RoamingSettings
{
    /// <summary>null or empty means "follow system".</summary>
    public string? Language { get; set; }

    public string? Theme { get; set; }

    public bool AutoUpdate { get; set; } = true;

    public AutoUpdateInterval AutoUpdateInterval { get; set; } = AutoUpdateInterval.Daily;

    public bool DisableMirrorDownload { get; set; }

    public List<string>? UncheckedOptimizationItemNameKeys { get; set; }
    public Dictionary<string, bool>? DiskSpaceCleanupSelections { get; set; }

    public bool ShowOnlyNotOptimized { get; set; }

    /// <summary>Show entries signed as Windows system components on the Startup tab.</summary>
    public bool ShowWindowsStartupEntries { get; set; }

    /// <summary>Also hide Microsoft entries that are not Windows components (Office, OneDrive).</summary>
    public bool HideMicrosoftStartupEntries { get; set; }

    /// <summary>Restrict the Startup tab to entries that have never been decided.</summary>
    public bool ShowOnlyPendingStartupItems { get; set; }

    /// <summary>
    ///     Put a remembered Deny back into force automatically when a scan finds it running again.
    ///     Off by default: a decision that arrived from another machine should be confirmed here
    ///     before it silently switches off something the user just installed.
    /// </summary>
    public bool AutoEnforceStartupDecisions { get; set; }
}

/// <summary>
///     App settings split by roaming behavior, on top of the JeekTools
///     <see cref="SettingsStorage" /> path scheme and <see cref="JsonSettingsFile" />
///     merge/write machinery. Watches the roaming Config folder so edits made
///     outside the app (or by another instance) are picked up at runtime.
/// </summary>
internal static class AppSettingsStore
{
    private const string AppName = "JeekWindowsOptimizer";
    private const string RoamingSettingsFileName = "settings.json";
    private const string LegacyMachineSettingsFileName = "machine.json";

    private const string SystemThemeName = "System";
    private const string LightThemeName = "Light";
    private const string DarkThemeName = "Dark";

    private static readonly SettingsStorage Storage = new(AppName);

    public static MachineSettings Machine { get; private set; } = new();
    public static RoamingSettings Roaming { get; private set; } = new();

    /// <summary>The storage location actually in effect (portable detection can override the saved location).</summary>
    public static StorageLocation EffectiveStorageLocation { get; private set; } =
        StorageLocation.UserDirectory;

    /// <summary>Raised (on a worker thread) after the roaming settings were reloaded from disk.</summary>
    public static event Action? RoamingSettingsReloaded;

    /// <summary>Raised right before the roaming Config folder moves, so other stores can flush pending edits.</summary>
    public static event Action? BeforeRoamingConfigLocationChange;

    /// <summary>
    ///     Raised after the roaming Config folder moved, passing whether existing files were moved
    ///     (<c>true</c>) or left behind (<c>false</c>), so other stores can reload or sync their shards.
    /// </summary>
    public static event Action<bool>? RoamingConfigLocationChanged;

    private static MachineSettings _baseMachine = new();
    private static RoamingSettings _baseRoaming = new();
    private static string _lastSavedRoamingJson = "";

    // ---------- Paths ----------

    public static string CurrentRoamingConfigDir =>
        Storage.ResolveConfigRoot(EffectiveStorageLocation, Machine.CustomStoragePath);

    /// <summary>Machine-local Config folder. Never roams, whatever the storage location is.</summary>
    public static string LocalConfigDir => Storage.LocalConfigDir;

    private static string RoamingSettingsPath =>
        Storage.ResolveSettingsPath(EffectiveStorageLocation, Machine.CustomStoragePath);

    // ---------- Load / Save ----------

    public static void Load()
    {
        MigrateLegacyMachineSettingsIfNeeded();

        JsonSettingsFile.TryLoad(Storage.MachineSettingsPath, out MachineSettings machine);
        NormalizeMachine(machine);
        Machine = machine;
        EffectiveStorageLocation = Storage.ResolveEffectiveLocation(machine.StorageLocation);

        MigrateLegacyRoamingSettingsIfNeeded();

        JsonSettingsFile.TryLoad(RoamingSettingsPath, out RoamingSettings roaming);
        NormalizeRoaming(roaming);
        Roaming = roaming;

        _baseMachine = JsonSettingsFile.Clone(Machine);
        _baseRoaming = JsonSettingsFile.Clone(Roaming);
        _lastSavedRoamingJson = JsonSettingsFile.Serialize(Roaming);

        StartWatcher();
    }

    /// <summary>Migrate the pre-refactor machine file at <c>%LocalAppData%\&lt;App&gt;\Config\machine.json</c>.</summary>
    private static void MigrateLegacyMachineSettingsIfNeeded()
    {
        try
        {
            var legacyPath = Path.Combine(Storage.LocalConfigDir, LegacyMachineSettingsFileName);
            if (!File.Exists(legacyPath) || File.Exists(Storage.MachineSettingsPath))
                return;

            if (JsonNode.Parse(File.ReadAllText(legacyPath)) is not JsonObject legacy)
                return;

            var machine = new MachineSettings
            {
                StorageLocation = legacy["StorageMode"]?.GetValue<string>() switch
                {
                    "Portable" => StorageLocation.ProgramDirectory,
                    "Custom" => StorageLocation.CustomDirectory,
                    _ => StorageLocation.UserDirectory,
                },
                CustomStoragePath = legacy["CustomConfigDir"]?.GetValue<string>(),
            };
            NormalizeMachine(machine);
            SharedDataFile.WriteAllTextAtomic(
                Storage.MachineSettingsPath,
                JsonSettingsFile.Serialize(machine)
            );
            File.Delete(legacyPath);
        }
        catch
        {
            // Migration must never block startup.
        }
    }

    /// <summary>Migrate the pre-refactor single file at <c>%LocalAppData%\&lt;App&gt;\settings.json</c>.</summary>
    private static void MigrateLegacyRoamingSettingsIfNeeded()
    {
        try
        {
            var legacyPath = Path.Combine(Storage.LocalDir, RoamingSettingsFileName);
            if (!File.Exists(legacyPath) || File.Exists(RoamingSettingsPath))
                return;

            if (!JsonSettingsFile.TryLoad(legacyPath, out RoamingSettings legacy))
                return;

            NormalizeRoaming(legacy);
            SharedDataFile.WriteAllTextAtomic(
                RoamingSettingsPath,
                JsonSettingsFile.Serialize(legacy)
            );
            File.Delete(legacyPath);
        }
        catch
        {
            // Migration must never block startup.
        }
    }

    private static void NormalizeMachine(MachineSettings settings)
    {
        settings.StorageLocation = Storage.NormalizeLocation(settings.StorageLocation);
        if (string.IsNullOrWhiteSpace(settings.CustomStoragePath))
            settings.CustomStoragePath = null;
        if (
            settings.StorageLocation == StorageLocation.CustomDirectory
            && settings.CustomStoragePath is null
        )
            settings.StorageLocation = StorageLocation.UserDirectory;
    }

    private static void NormalizeRoaming(RoamingSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Language))
            settings.Language = null;
        if (string.IsNullOrWhiteSpace(settings.Theme))
            settings.Theme = null;
    }

    public static void SaveMachine()
    {
        if (
            JsonSettingsFile.TryMergeAndWrite(
                Storage.MachineSettingsPath,
                _baseMachine,
                Machine,
                NormalizeMachine,
                forceAllLocal: false,
                out var merged
            )
        )
        {
            Machine = merged;
            _baseMachine = JsonSettingsFile.Clone(merged);
        }
    }

    public static void SaveRoaming(bool forceAllLocal = false)
    {
        if (
            JsonSettingsFile.TryMergeAndWrite(
                RoamingSettingsPath,
                _baseRoaming,
                Roaming,
                NormalizeRoaming,
                forceAllLocal,
                out var merged
            )
        )
        {
            Roaming = merged;
            _baseRoaming = JsonSettingsFile.Clone(merged);
            _lastSavedRoamingJson = JsonSettingsFile.Serialize(merged);
        }
    }

    // ---------- Storage location switching ----------

    /// <summary>
    ///     Switch the storage location for roaming settings. With
    ///     <paramref name="moveFiles" /> the whole Config folder is moved to the new
    ///     root; otherwise the old files stay and the new location starts from the
    ///     in-memory settings. Machine settings are never moved.
    /// </summary>
    public static void SwitchStorageLocation(
        StorageLocation newLocation,
        string? customDir,
        bool moveFiles
    )
    {
        if (
            newLocation == StorageLocation.CustomDirectory && string.IsNullOrWhiteSpace(customDir)
        )
            return;

        var oldRoot = CurrentRoamingConfigDir;
        var newRoot = Storage.ResolveConfigRoot(newLocation, customDir);
        var sameTarget = string.Equals(
            Path.GetFullPath(oldRoot),
            Path.GetFullPath(newRoot),
            StringComparison.OrdinalIgnoreCase
        );

        StopWatcher();
        try
        {
            if (!sameTarget)
                BeforeRoamingConfigLocationChange?.Invoke();

            if (!sameTarget && moveFiles)
                SettingsStorage.MoveConfigRoot(oldRoot, newRoot);

            Machine.StorageLocation = newLocation;
            Machine.CustomStoragePath =
                newLocation == StorageLocation.CustomDirectory ? customDir : null;
            EffectiveStorageLocation = newLocation;
            SaveMachine();
            SaveRoaming(forceAllLocal: !File.Exists(RoamingSettingsPath));
        }
        finally
        {
            StartWatcher();
        }

        RoamingConfigLocationChanged?.Invoke(moveFiles);
    }

    // ---------- Roaming config watcher ----------

    private static readonly System.Threading.Lock WatchLock = new();
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromSeconds(10);
    private static RoamingConfigWatcher? _watcher;

    /// <summary>
    ///     Registers a handler for external changes under the roaming Config folder (including
    ///     subdirectories such as <c>StartupDecisions/</c>). <paramref name="matches" /> is
    ///     evaluated immediately on file system events with the Config-relative path and change type
    ///     so self-writes and noisy directory updates can be filtered before debouncing.
    /// </summary>
    public static void RegisterConfigWatcher(
        Func<string, WatcherChangeTypes, bool> matches,
        Action onChanged
    )
    {
        lock (WatchLock)
            ExtraWatchers.Add((matches, onChanged));
    }

    private static readonly List<(
        Func<string, WatcherChangeTypes, bool> Matches,
        Action OnChanged
    )> ExtraWatchers = [];

    private static void StartWatcher()
    {
        StopWatcher();

        try
        {
            var dir = CurrentRoamingConfigDir;
            Directory.CreateDirectory(dir);

            var watcher = new RoamingConfigWatcher(
                ReloadDebounce,
                MatchWatchTarget,
                OnDebouncedChanges
            );
            watcher.Watch(dir);
            _watcher = watcher;
        }
        catch
        {
            // A missing watcher only disables live reload; the app still works.
        }
    }

    private static void StopWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private static List<object> MatchWatchTarget(string relativePath, WatcherChangeTypes changeType)
    {
        var targets = new List<object>();

        if (string.Equals(relativePath, RoamingSettingsFileName, StringComparison.OrdinalIgnoreCase))
            targets.Add(RoamingSettingsFileName);

        (Func<string, WatcherChangeTypes, bool> Matches, Action OnChanged)[] watchers;
        lock (WatchLock)
            watchers = [.. ExtraWatchers];

        foreach (var entry in watchers)
        {
            try
            {
                if (entry.Matches(relativePath, changeType))
                    targets.Add(entry.OnChanged);
            }
            catch
            {
                // One store's matcher failure must not stop the others.
            }
        }

        return targets;
    }

    private static void OnDebouncedChanges(IReadOnlySet<object> targets, bool overflow)
    {
        (Func<string, WatcherChangeTypes, bool> Matches, Action OnChanged)[] watchers;
        lock (WatchLock)
            watchers = [.. ExtraWatchers];

        foreach (var (_, onChanged) in watchers)
        {
            if (!overflow && !targets.Contains(onChanged))
                continue;

            try
            {
                onChanged();
            }
            catch
            {
                // One store's reload failure must not stop the others.
            }
        }

        if (!overflow && !targets.Contains(RoamingSettingsFileName))
            return;

        try
        {
            if (!JsonSettingsFile.TryLoad(RoamingSettingsPath, out RoamingSettings disk))
                return;
            NormalizeRoaming(disk);
            var diskJson = JsonSettingsFile.Serialize(disk);
            if (string.Equals(diskJson, _lastSavedRoamingJson, StringComparison.Ordinal))
                return; // Our own save; nothing new.

            Roaming = disk;
            _baseRoaming = JsonSettingsFile.Clone(disk);
            _lastSavedRoamingJson = diskJson;
            RoamingSettingsReloaded?.Invoke();
        }
        catch
        {
            // Keep the current in-memory settings on any reload failure.
        }
    }

    // ---------- Language ----------

    public static bool IsFollowSystemLanguage => string.IsNullOrWhiteSpace(Roaming.Language);

    /// <summary>Resolve the concrete language to use, honoring an explicit choice or following the system.</summary>
    public static string ResolveEffectiveLanguage(IReadOnlyList<string> availableLanguages)
    {
        var configured = Roaming.Language;
        if (!string.IsNullOrWhiteSpace(configured))
            foreach (var language in availableLanguages)
                if (string.Equals(language, configured, StringComparison.OrdinalIgnoreCase))
                    return language;

        var systemTwoLetter = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        foreach (var language in availableLanguages)
            if (string.Equals(language, systemTwoLetter, StringComparison.OrdinalIgnoreCase))
                return language;

        foreach (var language in availableLanguages)
            if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
                return language;

        return availableLanguages.Count > 0 ? availableLanguages[0] : "en";
    }

    /// <summary>Set the language. Pass null or empty to follow the system language.</summary>
    public static void SetLanguage(string? language)
    {
        Roaming.Language = string.IsNullOrWhiteSpace(language) ? null : language;
        SaveRoaming();
    }

    // ---------- Theme ----------

    public static bool TryGetThemeVariant(out ThemeVariant themeVariant)
    {
        if (string.Equals(Roaming.Theme, LightThemeName, StringComparison.OrdinalIgnoreCase))
        {
            themeVariant = ThemeVariant.Light;
            return true;
        }

        if (string.Equals(Roaming.Theme, DarkThemeName, StringComparison.OrdinalIgnoreCase))
        {
            themeVariant = ThemeVariant.Dark;
            return true;
        }

        if (string.Equals(Roaming.Theme, SystemThemeName, StringComparison.OrdinalIgnoreCase))
        {
            themeVariant = ThemeVariant.Default;
            return true;
        }

        themeVariant = ThemeVariant.Default;
        return false;
    }

    public static void SetThemeVariant(ThemeVariant themeVariant)
    {
        Roaming.Theme =
            themeVariant == ThemeVariant.Light ? LightThemeName
            : themeVariant == ThemeVariant.Dark ? DarkThemeName
            : SystemThemeName;
        SaveRoaming();
    }

    // ---------- Auto update ----------

    public static void SetAutoUpdate(bool enabled)
    {
        Roaming.AutoUpdate = enabled;
        SaveRoaming();
    }

    public static void SetAutoUpdateInterval(AutoUpdateInterval interval)
    {
        Roaming.AutoUpdateInterval = interval;
        SaveRoaming();
    }

    // ---------- Optimization item selection ----------

    public static HashSet<string> GetUncheckedOptimizationItemNameKeys()
    {
        return new HashSet<string>(
            Roaming.UncheckedOptimizationItemNameKeys ?? [],
            StringComparer.Ordinal
        );
    }

    public static void SetUncheckedOptimizationItemNameKeys(IEnumerable<string> nameKeys)
    {
        Roaming.UncheckedOptimizationItemNameKeys = nameKeys
            .Where(nameKey => !string.IsNullOrWhiteSpace(nameKey))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        SaveRoaming();
    }

    public static void SetShowOnlyNotOptimized(bool value)
    {
        Roaming.ShowOnlyNotOptimized = value;
        SaveRoaming();
    }

    // ---------- Startup tab ----------

    public static void SetShowWindowsStartupEntries(bool value)
    {
        Roaming.ShowWindowsStartupEntries = value;
        SaveRoaming();
    }

    public static void SetHideMicrosoftStartupEntries(bool value)
    {
        Roaming.HideMicrosoftStartupEntries = value;
        SaveRoaming();
    }

    public static void SetShowOnlyPendingStartupItems(bool value)
    {
        Roaming.ShowOnlyPendingStartupItems = value;
        SaveRoaming();
    }

    public static void SetAutoEnforceStartupDecisions(bool value)
    {
        Roaming.AutoEnforceStartupDecisions = value;
        SaveRoaming();
    }
}

/// <summary>
///     Watches the roaming Config directory (including subdirectories such as
///     <c>StartupDecisions/</c>) and invokes a debounced callback once changes settle.
///     Matchers run immediately when events arrive so self-writes and noisy directory
///     timestamp updates are ignored before starting the quiet-period timer.
/// </summary>
internal sealed class RoamingConfigWatcher : IDisposable
{
    private readonly TimeSpan _quietPeriod;
    private readonly Func<string, WatcherChangeTypes, List<object>> _matchTargets;
    private readonly Action<IReadOnlySet<object>, bool> _onFlushed;
    private readonly Lock _gate = new();
    private readonly HashSet<object> _pendingTargets = [];
    private readonly Timer _debounce;
    private FileSystemWatcher? _watcher;
    private bool _pendingOverflow;
    private bool _disposed;

    public RoamingConfigWatcher(
        TimeSpan quietPeriod,
        Func<string, WatcherChangeTypes, List<object>> matchTargets,
        Action<IReadOnlySet<object>, bool> onFlushed
    )
    {
        _quietPeriod = quietPeriod < TimeSpan.FromMilliseconds(50)
            ? TimeSpan.FromMilliseconds(50)
            : quietPeriod;
        _matchTargets = matchTargets;
        _onFlushed = onFlushed;
        _debounce = new Timer(
            _ => Flush(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
    }

    public void Watch(string dir)
    {
        Stop();
        if (_disposed || !Directory.Exists(dir))
            return;

        try
        {
            var watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter =
                    NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size,
            };
            watcher.Changed += OnEvent;
            watcher.Created += OnEvent;
            watcher.Deleted += OnEvent;
            watcher.Renamed += OnEvent;
            watcher.Error += (_, _) => ScheduleOverflow();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch
        {
            // Inaccessible directory: skip watching.
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        var matched = new List<object>();

        if (!string.IsNullOrEmpty(e.Name) && !e.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            matched.AddRange(_matchTargets(e.Name, e.ChangeType));

        if (
            e is RenamedEventArgs renamed
            && !string.IsNullOrEmpty(renamed.OldName)
            && !renamed.OldName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        )
            matched.AddRange(_matchTargets(renamed.OldName, e.ChangeType));

        if (matched.Count == 0)
            return;

        lock (_gate)
        {
            if (_disposed)
                return;

            foreach (var target in matched)
                _pendingTargets.Add(target);
            _debounce.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    private void ScheduleOverflow()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _pendingOverflow = true;
            _debounce.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        HashSet<object> targets;
        bool overflow;
        lock (_gate)
        {
            if (_disposed || (!_pendingOverflow && _pendingTargets.Count == 0))
                return;

            targets = new HashSet<object>(_pendingTargets);
            overflow = _pendingOverflow;
            _pendingTargets.Clear();
            _pendingOverflow = false;
        }

        _onFlushed(targets, overflow);
    }

    private void Stop()
    {
        lock (_gate)
        {
            _pendingTargets.Clear();
            _pendingOverflow = false;
            if (!_disposed)
                _debounce.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        if (_watcher is null)
            return;

        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        catch
        {
            // Ignore disposal errors.
        }
        _watcher = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _pendingTargets.Clear();
            _pendingOverflow = false;
            _debounce.Dispose();
        }

        Stop();
    }
}
