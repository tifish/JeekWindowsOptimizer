using DotNetRun;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

public readonly record struct DefenderTamperProtectionStatus(
    bool? RuntimeIsEnabled,
    int RegistryValue
)
{
    public bool IsOff =>
        RuntimeIsEnabled.HasValue
            ? !RuntimeIsEnabled.Value
            : RegistryValue is 0 or 4;

    public string DetectionSource =>
        RuntimeIsEnabled.HasValue ? "Get-MpComputerStatus" : "RegistryFallback";
}

public static class DefenderProtection
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(DefenderProtection));

    private static readonly RegistryValue TamperProtectionRegistryValue = new(
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Features",
        "TamperProtection"
    );

    public static Task<DefenderTamperProtectionStatus> GetTamperProtectionStatus()
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            async () =>
            {
                var runtimeIsEnabled = await TryGetRuntimeTamperProtectionStatus();
                var registryValue = TamperProtectionRegistryValue.GetValue(0);
                return new DefenderTamperProtectionStatus(runtimeIsEnabled, registryValue);
            }
        );
    }

    private static async Task<bool?> TryGetRuntimeTamperProtectionStatus()
    {
        try
        {
            PowerShellService.Commands.Clear();
            PowerShellService.Streams.ClearStreams();
            PowerShellService
                .AddCommand("Get-MpComputerStatus")
                .AddCommand("Select-Object")
                .AddParameter("ExpandProperty", "IsTamperProtected");

            var result = await PowerShellService.InvokeAsync();
            var value = result.FirstOrDefault()?.BaseObject;
            if (value is bool isEnabled)
                return isEnabled;

            if (value != null && bool.TryParse(value.ToString(), out isEnabled))
                return isEnabled;

            Log.ZLogWarning(
                $"Get-MpComputerStatus did not return IsTamperProtected; using the registry fallback"
            );
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(
                ex,
                $"Failed to query IsTamperProtected from Get-MpComputerStatus; using the registry fallback"
            );
        }

        return null;
    }
}
