global using static JeekWindowsOptimizer.PowerShellServiceContainer;
using System.Management.Automation;

namespace JeekWindowsOptimizer;

public static class PowerShellServiceContainer
{
    public static readonly PowerShell PowerShellService = PowerShell.Create();
}
