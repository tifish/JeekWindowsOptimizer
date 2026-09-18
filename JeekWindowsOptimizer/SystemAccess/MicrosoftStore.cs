using System.Management.Automation.Runspaces;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

public static class MicrosoftStore
{
    private static readonly ILogger Log = LogManager.CreateLogger<MainViewModel>();

    public static async Task Initialize()
    {
        await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            async () =>
            {
                try
                {
                    PowerShellService.Commands.Clear();
                    await PowerShellService
                        .AddCommand("Set-ExecutionPolicy")
                        .AddParameter("Scope", "Process")
                        .AddParameter("ExecutionPolicy", "Bypass")
                        .InvokeAsync();

                    PowerShellService.Commands.Clear();
                    await PowerShellService
                        .AddCommand("Import-Module")
                        .AddParameter("Name", "AppX")
                        .AddParameter("UseWindowsPowerShell")
                        .InvokeAsync();
                }
                catch (Exception e)
                {
                    Log.ZLogError(e, $"Failed to set execution policy");
                }

                try
                {
                    // One snapshot for all items: each Get-AppxPackage call costs ~0.2 s.
                    PowerShellService.Commands.Clear();
                    PowerShellService.Streams.ClearStreams();
                    // Project names inside the Windows PowerShell compatibility session:
                    // shipping full package objects back takes ~2 s instead of ~0.2 s.
                    PowerShellService.AddScript(
                        "Invoke-Command -Session (Get-PSSession -Name WinPSCompatSession) "
                            + "{ Get-AppxPackage -AllUsers | ForEach-Object Name }"
                    );
                    var results = await PowerShellService.InvokeAsync();
                    // A partial list would report missing packages as uninstalled.
                    if (PowerShellService.HadErrors || results.Count == 0)
                        Log.ZLogWarning($"Package list unavailable, falling back to per-package queries");
                    else
                        _installedPackageNames = new HashSet<string>(
                            results
                                .Select(result => result?.BaseObject as string)
                                .OfType<string>(),
                            StringComparer.OrdinalIgnoreCase
                        );
                }
                catch (Exception e)
                {
                    Log.ZLogError(e, $"Failed to list installed packages");
                }
            }
        );
    }

    private static HashSet<string>? _installedPackageNames;

    /// <summary>
    /// Checks the snapshot taken by <see cref="Initialize"/>; falls back to a live query
    /// when the snapshot is unavailable.
    /// </summary>
    public static async Task<bool> IsPackageInstalled(string packageName)
    {
        var snapshot = _installedPackageNames;
        return snapshot is not null
            ? snapshot.Contains(packageName)
            : await HasPackage(packageName);
    }

    private static Command GetPackageCommand(string packageName) =>
        new("Get-AppxPackage")
        {
            Parameters =
            {
                new CommandParameter("AllUsers"),
                new CommandParameter("Name", packageName),
            },
        };

    public static async Task<bool> HasPackage(string packageName)
    {
        return await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            async () =>
            {
                try
                {
                    PowerShellService.Commands.Clear();
                    PowerShellService.Commands.AddCommand(GetPackageCommand(packageName));
                    return (await PowerShellService.InvokeAsync()).Count > 0;
                }
                catch (Exception e)
                {
                    Log.ZLogError(e, $"Failed to check if package {packageName} exists");
                    return false;
                }
            }
        );
    }

    public static async Task<string?> GetPackageFullName(string packageName)
    {
        return await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            async () =>
            {
                try
                {
                    PowerShellService.Commands.Clear();
                    PowerShellService.Streams.ClearStreams();
                    PowerShellService
                        .Commands.AddCommand(GetPackageCommand(packageName))
                        .AddCommand("Select-Object")
                        .AddParameter("First", 1)
                        .AddParameter("ExpandProperty", "PackageFullName");
                    return (await PowerShellService.InvokeAsync()).FirstOrDefault()?.BaseObject
                        as string;
                }
                catch (Exception e)
                {
                    Log.ZLogError(e, $"Failed to get full name for package {packageName}");
                    return null;
                }
            }
        );
    }

    public static async Task UninstallPackage(string packageName)
    {
        await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            async () =>
            {
                PowerShellService.Commands.Clear();
                PowerShellService
                    .Commands.AddCommand(GetPackageCommand(packageName))
                    .AddCommand("Remove-AppxPackage");
                await PowerShellService.InvokeAsync();
            }
        );
    }
}
