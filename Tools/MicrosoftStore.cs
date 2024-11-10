using JeekTools;

namespace JeekWindowsOptimizer;

public static class MicrosoftStore
{
    private const string PowerShell = "PowerShell";
    private const string PowerShellArguments = "-ExecutionPolicy Bypass -WindowStyle Hidden -NonInteractive -Command";

    public static async Task<bool> HasPackage(string packageName)
    {
        return await Executor.RunAndWait(PowerShell,
            $$"""{{PowerShellArguments}} "if ((Get-AppxPackage -AllUsers {{packageName}}).Count > 0) { Exit 0 } else { Exit 1 }" """,
            false, true);
    }

    public static async Task UninstallPackage(string packageName)
    {
        await Executor.RunAndWait(PowerShell,
            $"""{PowerShellArguments} "Get-AppxPackage -AllUsers {packageName} | Remove-AppxPackage" """,
            false, true);
    }
}
