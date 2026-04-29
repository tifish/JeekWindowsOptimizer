using System.Diagnostics;
using Avalonia.Controls;
using Jeek.Avalonia.Localization;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;

namespace JeekWindowsOptimizer;

public class DriverItem(string groupNameKey, string nameKey, string descriptionKey) : OptimizationItem
{
    public override string GroupNameKey => groupNameKey;
    public override string NameKey => nameKey;
    public override string DescriptionKey => descriptionKey;

    public List<string> DriverPathPatterns { get; } = [];

    public List<string> GetDriverPaths()
    {
        var result = new List<string>();

        foreach (var pattern in DriverPathPatterns)
        {
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                var folderPath = Path.GetDirectoryName(pattern);
                var namePattern = Path.GetFileName(pattern);

                if (folderPath == null)
                    continue;

                result.AddRange(Directory.GetFileSystemEntries(folderPath, namePattern));
            }
            else if (Directory.Exists(pattern) || File.Exists(pattern))
            {
                result.Add(pattern);
            }
        }

        return result;
    }

    public override Task Initialize()
    {
        IsOptimized = GetDriverPaths().Count == 0;
        return Task.CompletedTask;
    }

    protected override async Task<bool> IsOptimizedChanging(bool value)
    {
        if (!value)
            return false;

        var result = true;
        try
        {
            var driverPaths = GetDriverPaths();

            foreach (var driverPath in driverPaths)
            {
                if (File.Exists(driverPath))
                    File.Delete(driverPath);

                if (Directory.Exists(driverPath))
                    Directory.Delete(driverPath, true);
            }

            result = driverPaths.All(path => !File.Exists(path) && !Directory.Exists(path));
        }
        catch
        {
            result = false;
        }

        if (!result)
        {
            await MessageBoxManager.GetMessageBoxStandard(new MessageBoxStandardParams
            {
                ContentMessage = string.Format(Localizer.Get("PleaseUninstallDriver"), Name),
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
