using DotNetRun;
using Json.Easy;

namespace JeekWindowsOptimizer;

public class WindowsTerminalUseNewWindow : OptimizationItem
{
    public override string GroupNameKey => "Software";
    public override string NameKey => "WindowsTerminalUseNewWindowName";

    public override string DescriptionKey => "WindowsTerminalUseNewWindowDescription";

    private static readonly string SettingsJsonPath = Cmd.ExpandEnvVar(
        @"%USERPROFILE%\AppData\Local\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json"
    );

    private readonly JsonFile SettingsJson = new(SettingsJsonPath);

    private const string DefaultValue = "useNew";

    public override async Task Initialize()
    {
        var json = await SettingsJson.Load();
        if (json == null)
        {
            IsOptimized = true;
            return;
        }

        IsOptimized = json.Get("windowingBehavior", DefaultValue) == DefaultValue;
    }

    protected override async Task<bool> IsOptimizedChanging(bool value)
    {
        // The previous tab/window preference is unknown. Keep this a one-way action.
        if (!value)
            return false;

        var json = await SettingsJson.Load();
        if (json == null)
            return false;

        json.Set("windowingBehavior", DefaultValue);
        await SettingsJson.Save(json);

        return true;
    }
}
