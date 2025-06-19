using System.Diagnostics;
using Avalonia.Controls;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;

namespace JeekWindowsOptimizer;

public class DriverItem(string groupNameKey, string nameKey, string descriptionKey, string driverPath) : OptimizationItem
{
    public override string GroupNameKey => groupNameKey;
    public override string NameKey => nameKey;
    public override string DescriptionKey => descriptionKey;
    public string DriverPath => driverPath;

    public List<string> DriverPaths { get; } = [];

    public override Task Initialize()
    {
        IsOptimized = !DriverPaths.Any(File.Exists);
        return Task.CompletedTask;
    }

    protected override async Task<bool> IsOptimizedChanging(bool value)
    {
        if (!value)
            return false;

        var result = true;
        try
        {
            foreach (var driverPath in DriverPaths)
            {
                if (!File.Exists(driverPath))
                    continue;

                File.Delete(driverPath);

                if (File.Exists(driverPath))
                {
                    result = false;
                    break;
                }
            }
        }
        catch
        {
            result = false;
        }

        if (!result)
        {
            await MessageBoxManager.GetMessageBoxStandard(new MessageBoxStandardParams
            {
                ContentMessage = $"请手工卸载 {Name} 并重启后重试。",
                ButtonDefinitions = ButtonEnum.Ok,
                Icon = Icon.Info,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true,
                FontFamily = "Microsoft YaHei",
            }).ShowAsync();

            // Show Windows' uninstall app panel
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures-app") { UseShellExecute = true });
        }

        return result;
    }
}
