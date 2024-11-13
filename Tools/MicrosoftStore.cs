using System.Management.Automation;
using System.Management.Automation.Runspaces;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

public static class MicrosoftStore
{
    private static readonly ILogger Log = LogManager.CreateLogger<MainViewModel>();

    private static readonly PowerShell _powerShell = PowerShell.Create();

    static MicrosoftStore()
    {
        try
        {
            _powerShell.AddCommand("Set-ExecutionPolicy")
                .AddParameter("Scope", "Process")
                .AddParameter("ExecutionPolicy", "Bypass")
                .Invoke();

            _powerShell.Commands.Clear();
            _powerShell.AddCommand("Import-Module")
                .AddParameter("Name", "AppX")
                .AddParameter("UseWindowsPowerShell")
                .Invoke();
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

    public static bool HasPackage(string packageName)
    {
        try
        {
            _powerShell.Commands.Clear();
            _powerShell.Commands.AddCommand(GetPackageCommand(packageName));
            return _powerShell.Invoke().Count > 0;
        }
        catch (Exception e)
        {
            Log.ZLogError(e, $"Failed to check if package {packageName} exists");
            return false;
        }
    }

    public static async Task UninstallPackage(string packageName)
    {
        _powerShell.Commands.Clear();
        _powerShell.Commands.AddCommand(GetPackageCommand(packageName))
            .AddCommand("Remove-AppxPackage");
        await _powerShell.InvokeAsync();
    }
}
