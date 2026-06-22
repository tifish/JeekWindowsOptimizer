using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Styling;

namespace JeekWindowsOptimizer;

/// <summary>Where roaming settings are stored. Only affects roaming settings, never machine settings.</summary>
internal enum StorageMode
{
    /// <summary><c>%AppData%\&lt;App&gt;\Config</c> (Roaming).</summary>
    Default,

    /// <summary><c>&lt;ProgramDir&gt;\Config</c>.</summary>
    Portable,

    /// <summary><c>&lt;CustomDir&gt;\Config</c>.</summary>
    Custom,
}

internal enum AutoUpdateInterval
{
    Every6Hours,
    Daily,
    Weekly,
    Never,
}

/// <summary>
///     Machine-bound settings that must not roam (e.g. which storage mode this install uses).
///     Always stored in <c>%LocalAppData%\&lt;App&gt;\Config</c>, regardless of storage mode.
/// </summary>
internal sealed class MachineSettings
{
    public StorageMode StorageMode { get; set; } = StorageMode.Default;

    public string? CustomConfigDir { get; set; }
}

/// <summary>Machine-independent user preferences. Stored according to the active <see cref="StorageMode" />.</summary>
internal sealed class RoamingSettings
{
    /// <summary>null or empty means "follow system".</summary>
    public string? Language { get; set; }

    public string? Theme { get; set; }

    public bool AutoUpdate { get; set; } = true;

    public AutoUpdateInterval AutoUpdateInterval { get; set; } = AutoUpdateInterval.Daily;

    public bool DisableMirrorDownload { get; set; }

    public List<string>? UncheckedOptimizationItemNameKeys { get; set; }
}

internal static class AppSettingsStore
{
    private const string AppName = "JeekWindowsOptimizer";
    private const string ConfigDirName = "Config";
    private const string MachineSettingsFileName = "machine.json";
    private const string RoamingSettingsFileName = "settings.json";

    private const string SystemThemeName = "System";
    private const string LightThemeName = "Light";
    private const string DarkThemeName = "Dark";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static MachineSettings Machine { get; private set; } = new();
    public static RoamingSettings Roaming { get; private set; } = new();

    /// <summary>The storage mode actually in effect (portable detection can override the saved mode).</summary>
    public static StorageMode EffectiveStorageMode { get; private set; } = StorageMode.Default;

    // ---------- Paths ----------

    private static string ProgramDir => AppContext.BaseDirectory;

    private static string PortableConfigDir => Path.Combine(ProgramDir, ConfigDirName);

    private static string LocalConfigDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName,
            ConfigDirName
        );

    private static string MachineSettingsPath =>
        Path.Combine(LocalConfigDir, MachineSettingsFileName);

    private static string GetRoamingConfigDir(StorageMode mode, string? customDir)
    {
        return mode switch
        {
            StorageMode.Portable => PortableConfigDir,
            StorageMode.Custom when !string.IsNullOrWhiteSpace(customDir) => Path.Combine(
                customDir,
                ConfigDirName
            ),
            _ => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppName,
                ConfigDirName
            ),
        };
    }

    public static string CurrentRoamingConfigDir =>
        GetRoamingConfigDir(EffectiveStorageMode, Machine.CustomConfigDir);

    private static string RoamingSettingsPath =>
        Path.Combine(CurrentRoamingConfigDir, RoamingSettingsFileName);

    // ---------- Load / Save ----------

    public static void Load()
    {
        Machine = LoadJson<MachineSettings>(MachineSettingsPath) ?? new();

        // A <ProgramDir>\Config directory forces portable mode, overriding the saved mode.
        if (Directory.Exists(PortableConfigDir))
            EffectiveStorageMode = StorageMode.Portable;
        else if (
            Machine.StorageMode == StorageMode.Custom
            && string.IsNullOrWhiteSpace(Machine.CustomConfigDir)
        )
            EffectiveStorageMode = StorageMode.Default;
        else
            EffectiveStorageMode = Machine.StorageMode;

        Roaming = LoadJson<RoamingSettings>(RoamingSettingsPath) ?? new();

        MigrateLegacySettingsIfNeeded();
    }

    /// <summary>Migrate the pre-refactor single file at <c>%LocalAppData%\&lt;App&gt;\settings.json</c>.</summary>
    private static void MigrateLegacySettingsIfNeeded()
    {
        try
        {
            var legacyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppName,
                RoamingSettingsFileName
            );

            if (!File.Exists(legacyPath) || File.Exists(RoamingSettingsPath))
                return;

            var legacy = LoadJson<RoamingSettings>(legacyPath);
            if (legacy is null)
                return;

            Roaming = legacy;
            SaveRoaming();

            try
            {
                File.Delete(legacyPath);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
        catch
        {
            // Migration must never block startup.
        }
    }

    private static T? LoadJson<T>(string path)
        where T : class
    {
        try
        {
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveJson(string path, object value)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
        }
        catch
        {
            // Settings persistence should never block the optimizer from running.
        }
    }

    public static void SaveMachine()
    {
        SaveJson(MachineSettingsPath, Machine);
    }

    public static void SaveRoaming()
    {
        SaveJson(RoamingSettingsPath, Roaming);
    }

    // ---------- Storage mode switching ----------

    /// <summary>
    ///     Switch the storage mode for roaming settings, migrating them to the new location.
    ///     Machine settings are never moved. Leaving portable deletes <c>&lt;ProgramDir&gt;\Config</c>.
    /// </summary>
    public static void SwitchStorageMode(StorageMode newMode, string? customDir = null)
    {
        if (newMode == StorageMode.Custom && string.IsNullOrWhiteSpace(customDir))
            return;

        var oldMode = EffectiveStorageMode;
        var oldDir = CurrentRoamingConfigDir;
        var newDir = GetRoamingConfigDir(newMode, customDir);

        var sameTarget = string.Equals(
            Path.GetFullPath(oldDir),
            Path.GetFullPath(newDir),
            StringComparison.OrdinalIgnoreCase
        );

        // Point at the new location, then write the in-memory roaming settings there.
        EffectiveStorageMode = newMode;
        Machine.StorageMode = newMode;
        Machine.CustomConfigDir = newMode == StorageMode.Custom ? customDir : null;
        SaveRoaming();
        SaveMachine();

        if (sameTarget)
            return;

        // Remove the old roaming settings file.
        try
        {
            var oldSettingsFile = Path.Combine(oldDir, RoamingSettingsFileName);
            if (File.Exists(oldSettingsFile))
                File.Delete(oldSettingsFile);
        }
        catch
        {
            // Best-effort.
        }

        // Leaving portable: delete <ProgramDir>\Config so the next start is not forced portable.
        if (oldMode == StorageMode.Portable)
        {
            try
            {
                if (Directory.Exists(PortableConfigDir))
                    Directory.Delete(PortableConfigDir, recursive: true);
            }
            catch
            {
                // Best-effort.
            }
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
}
