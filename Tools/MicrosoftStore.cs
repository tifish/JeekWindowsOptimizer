using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace JeekWindowsOptimizer;

public static class MicrosoftStore
{
    private static readonly PowerShell _powerShell = PowerShell.Create();

    static MicrosoftStore()
    {
        _powerShell.AddCommand("Set-ExecutionPolicy")
            .AddParameter("Scope", "Process")
            .AddParameter("ExecutionPolicy", "Bypass")
            .Invoke();
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
        _powerShell.Commands.Clear();
        _powerShell.Commands.AddCommand(GetPackageCommand(packageName));
        return _powerShell.Invoke().Count > 0;
    }

    public static async Task UninstallPackage(string packageName)
    {
        _powerShell.Commands.Clear();
        _powerShell.Commands.AddCommand(GetPackageCommand(packageName))
            .AddCommand("Remove-AppxPackage");
        await _powerShell.InvokeAsync();
    }
}
