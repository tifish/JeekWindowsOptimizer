using System.Text.Json;
using Avalonia.Styling;

namespace JeekWindowsOptimizer;

internal sealed class AppSettings
{
    public string? Language { get; set; }

    public string? Theme { get; set; }

    public List<string>? UncheckedOptimizationItemNameKeys { get; set; }
}

internal static class AppSettingsStore
{
    private const string SystemThemeName = "System";
    private const string LightThemeName = "Light";
    private const string DarkThemeName = "Dark";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JeekWindowsOptimizer",
        "settings.json"
    );

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;

            Current =
                JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch
        {
            Current = new();
        }
    }

    public static void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch
        {
            // Settings persistence should never block the optimizer from running.
        }
    }

    public static bool TryGetLanguage(out string language)
    {
        var configuredLanguage = Current.Language;
        if (
            configuredLanguage is not null
            && (
                string.Equals(configuredLanguage, "en", StringComparison.OrdinalIgnoreCase)
                || string.Equals(configuredLanguage, "zh", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            language = configuredLanguage.ToLowerInvariant();
            return true;
        }

        language = "";
        return false;
    }

    public static bool TryGetThemeVariant(out ThemeVariant themeVariant)
    {
        if (string.Equals(Current.Theme, LightThemeName, StringComparison.OrdinalIgnoreCase))
        {
            themeVariant = ThemeVariant.Light;
            return true;
        }

        if (string.Equals(Current.Theme, DarkThemeName, StringComparison.OrdinalIgnoreCase))
        {
            themeVariant = ThemeVariant.Dark;
            return true;
        }

        if (string.Equals(Current.Theme, SystemThemeName, StringComparison.OrdinalIgnoreCase))
        {
            themeVariant = ThemeVariant.Default;
            return true;
        }

        themeVariant = ThemeVariant.Default;
        return false;
    }

    public static HashSet<string> GetUncheckedOptimizationItemNameKeys()
    {
        return new HashSet<string>(
            Current.UncheckedOptimizationItemNameKeys ?? [],
            StringComparer.Ordinal
        );
    }

    public static void SetUncheckedOptimizationItemNameKeys(IEnumerable<string> nameKeys)
    {
        Current.UncheckedOptimizationItemNameKeys = nameKeys
            .Where(nameKey => !string.IsNullOrWhiteSpace(nameKey))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        Save();
    }

    public static void SetLanguage(string language)
    {
        Current.Language = language;
        Save();
    }

    public static void SetThemeVariant(ThemeVariant themeVariant)
    {
        Current.Theme =
            themeVariant == ThemeVariant.Light ? LightThemeName
            : themeVariant == ThemeVariant.Dark ? DarkThemeName
            : SystemThemeName;
        Save();
    }
}
