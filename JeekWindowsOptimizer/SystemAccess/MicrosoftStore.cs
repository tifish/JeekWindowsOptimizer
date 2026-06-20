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
            }
        );
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
