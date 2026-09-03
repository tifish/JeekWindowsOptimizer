using System.Diagnostics;
using Avalonia.Controls;
using Jeek.Avalonia.Localization;
using JeekTools;
using Microsoft.Extensions.Logging;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using ZLogger;

namespace JeekWindowsOptimizer;

public class DriverItem(string groupNameKey, string nameKey, string descriptionKey)
    : OptimizationItem
{
    private static readonly ILogger Log = LogManager.CreateLogger<DriverItem>();

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

        var failures = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            RemoveProduct
        );

        var result = failures.RemainingServices.Count == 0 && failures.RemainingPaths.Count == 0;

        if (!result)
        {
            var details = BuildFailureDetails(failures);

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
                                    )
                                    + details,
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

    /// <summary>
    /// The services and driver paths that survived a removal attempt.
    /// </summary>
    public sealed class RemovalFailures
    {
        public List<string> RemainingServices { get; } = [];
        public List<string> RemainingPaths { get; } = [];
    }

    /// <summary>
    /// Stops and deletes the product's services, then deletes its driver files. Individual
    /// failures are caught and logged; the remaining (still-present) services and paths are
    /// returned so the caller can report exactly what could not be removed.
    /// </summary>
    public RemovalFailures RemoveProduct()
    {
        // Stop and remove services first: driver files are often locked while
        // their service is running.
        foreach (var serviceName in GetExistingServiceNames())
        {
            try
            {
                using var service = new WindowsService(serviceName);
                if (!service.Delete())
                    Log.ZLogWarning($"Could not delete service '{serviceName}' for '{NameKey}'.");
            }
            catch (Exception ex)
            {
                Log.ZLogWarning(ex, $"Error deleting service '{serviceName}' for '{NameKey}'.");
            }
        }

        foreach (var driverPath in GetDriverPaths())
        {
            try
            {
                if (File.Exists(driverPath))
                    File.Delete(driverPath);

                if (Directory.Exists(driverPath))
                    Directory.Delete(driverPath, true);
            }
            catch (Exception ex)
            {
                Log.ZLogWarning(ex, $"Error deleting driver path '{driverPath}' for '{NameKey}'.");
            }
        }

        // Re-probe: whatever is still present is what could not be removed (services may be
        // locked until reboot, driver files may be held open by the kernel).
        var failures = new RemovalFailures();
        failures.RemainingServices.AddRange(GetExistingServiceNames());
        failures.RemainingPaths.AddRange(GetDriverPaths());

        if (failures.RemainingServices.Count > 0 || failures.RemainingPaths.Count > 0)
        {
            Log.ZLogWarning(
                $"Could not fully remove '{NameKey}'. Remaining services: [{string.Join(", ", failures.RemainingServices)}]. Remaining paths: [{string.Join(", ", failures.RemainingPaths)}]."
            );
        }

        return failures;
    }

    private static string BuildFailureDetails(RemovalFailures failures)
    {
        var remaining = failures.RemainingServices
            .Select(name => $"  • {name}")
            .Concat(failures.RemainingPaths.Select(path => $"  • {path}"))
            .ToList();

        if (remaining.Count == 0)
            return string.Empty;

        return "\n\n"
            + Localizer.Get("DriverRemovalFailedDetails")
            + "\n"
            + string.Join("\n", remaining);
    }
}
