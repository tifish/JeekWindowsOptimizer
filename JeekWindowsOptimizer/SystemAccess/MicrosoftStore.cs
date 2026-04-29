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
        try
        {
            PowerShellService.Commands.Clear();
            await PowerShellService.AddCommand("Set-ExecutionPolicy")
                .AddParameter("Scope", "Process")
                .AddParameter("ExecutionPolicy", "Bypass")
                .InvokeAsync();

            PowerShellService.Commands.Clear();
            await PowerShellService.AddCommand("Import-Module")
                .AddParameter("Name", "AppX")
                .AddParameter("UseWindowsPowerShell")
                .InvokeAsync();
        }
        catch (Exception e)
        {
            Log.ZLogError(e, $"Failed to set execution policy");
        }
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

    public static async Task UninstallPackage(string packageName)
    {
        PowerShellService.Commands.Clear();
        PowerShellService.Commands.AddCommand(GetPackageCommand(packageName))
            .AddCommand("Remove-AppxPackage");
        await PowerShellService.InvokeAsync();
    }
}
