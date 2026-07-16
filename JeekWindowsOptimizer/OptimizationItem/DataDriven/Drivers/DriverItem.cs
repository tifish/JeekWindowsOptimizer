using System.Diagnostics;
using Avalonia.Controls;
using Jeek.Avalonia.Localization;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;

namespace JeekWindowsOptimizer;

public class DriverItem(string groupNameKey, string nameKey, string descriptionKey)
    : OptimizationItem
{
    public override string GroupNameKey => groupNameKey;
    public override string NameKey => nameKey;
    public override string DescriptionKey => descriptionKey;

    public List<string> DriverPathPatterns { get; } = [];
    public List<string> ServiceNames { get; } = [];

    public List<string> GetDriverPaths()
    {
        var result = new List<string>();

        foreach (var pattern in DriverPathPatterns)
        {
            try
            {
                if (pattern.Contains('*') || pattern.Contains('?'))
                {
                    var folderPath = Path.GetDirectoryName(pattern);
                    var namePattern = Path.GetFileName(pattern);

                    if (string.IsNullOrEmpty(folderPath) || string.IsNullOrEmpty(namePattern))
                        continue;
                    if (!Directory.Exists(folderPath))
                        continue;

                    result.AddRange(Directory.GetFileSystemEntries(folderPath, namePattern));
                }
                else if (Directory.Exists(pattern) || File.Exists(pattern))
                {
                    result.Add(pattern);
                }
            }
            catch
            {
                // Ignore inaccessible or invalid patterns.
            }
        }

        return result;
    }

    public List<string> GetExistingServiceNames()
    {
        var result = new List<string>();

        foreach (var serviceName in ServiceNames)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
                continue;

            try
            {
                using var service = new WindowsService(serviceName);
                if (service.Exists())
                    result.Add(serviceName);
            }
            catch
            {
                // Ignore individual service probe failures.
            }
        }

        return result;
    }

    public bool IsProductPresent()
    {
        return GetDriverPaths().Count > 0 || GetExistingServiceNames().Count > 0;
    }

    public override async Task Initialize()
    {
        IsOptimized = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () => !IsProductPresent()
        );
    }

    protected override async Task<bool> IsOptimizedChanging(bool value)
    {
        if (!value)
            return false;

        var result = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () =>
            {
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

                    // Drivers may be locked; services usually remain until the product is uninstalled.
                    return !IsProductPresent();
                }
                catch
                {
                    return false;
                }
            }
        );

        if (!result)
        {
            await OptimizationExecutionScheduler.RunAsync(
                OptimizationExecutionAffinity.Ui,
                async () =>
                {
                    await MessageBoxManager
                        .GetMessageBoxStandard(
                            new MessageBoxStandardParams
                            {
                                ContentMessage = string.Format(
                                    Localizer.Get("PleaseUninstallDriver"),
                                    Name
                                ),
                                ButtonDefinitions = ButtonEnum.Ok,
                                Icon = Icon.Info,
                                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                                Topmost = true,
                                FontFamily = "Microsoft YaHei",
                            }
                        )
                        .ShowAsync();
                }
            );

            // Show Windows' uninstall app panel
            await OptimizationExecutionScheduler.RunAsync(
                OptimizationExecutionAffinity.Background,
                () =>
                    Process.Start(
                        new ProcessStartInfo("ms-settings:appsfeatures-app")
                        {
                            UseShellExecute = true,
                        }
                    )
            );
        }

        return result;
    }
}
