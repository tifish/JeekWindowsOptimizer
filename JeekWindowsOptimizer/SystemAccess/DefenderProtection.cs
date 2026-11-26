using System.Diagnostics;
using System.Management;
using DotNetRun;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

public readonly record struct DefenderTamperProtectionStatus(
    bool? RuntimeIsEnabled,
    int RegistryValue,
    bool? RealTimeProtectionEnabled = null,
    string? AmRunningMode = null,
    bool HasThirdPartyAntivirus = false
)
{
    public bool IsOff =>
        RuntimeIsEnabled.HasValue
            ? !RuntimeIsEnabled.Value
            : RegistryValue is 0 or 4;

    public bool IsDefenderInactiveMode =>
        !string.IsNullOrWhiteSpace(AmRunningMode)
        && (
            AmRunningMode.Contains("Passive", StringComparison.OrdinalIgnoreCase)
            || AmRunningMode.Equals("Not running", StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>
    /// True when Windows Defender Tamper Protection is actively preventing Defender policy
    /// overrides (such as real-time scanning or sample submission) from taking effect.
    /// </summary>
    public bool IsBlockingDefenderPolicies =>
        !IsOff
        && !IsDefenderInactiveMode
        && !(RuntimeIsEnabled is null && HasThirdPartyAntivirus);

    public string DetectionSource =>
        RuntimeIsEnabled.HasValue ? "WmiMpComputerStatus" : "RegistryFallback";
}

public static class DefenderProtection
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(DefenderProtection));

    public const string TamperProtectionSettingsUri = "windowsdefender://threatsettings";
    public const string FallbackDefenderSettingsUri = "ms-settings:windowsdefender";

    private static readonly RegistryValue TamperProtectionRegistryValue = new(
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Features",
        "TamperProtection"
    );

    internal static Func<Task<DefenderTamperProtectionStatus>>? StatusOverrideForTesting { get; set; }
    internal static Action? OpenSettingsOverrideForTesting { get; set; }

    public static Task<DefenderTamperProtectionStatus> GetTamperProtectionStatus()
    {
        if (StatusOverrideForTesting is { } statusOverride)
            return statusOverride();

        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            ReadLiveTamperProtectionStatus
        );
    }

    public static void OpenTamperProtectionSettings()
    {
        if (OpenSettingsOverrideForTesting is { } openOverride)
        {
            openOverride();
            return;
        }

        try
        {
            Process.Start(
                new ProcessStartInfo(TamperProtectionSettingsUri) { UseShellExecute = true }
            );
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(
                ex,
                $"Failed to open '{TamperProtectionSettingsUri}'; falling back to '{FallbackDefenderSettingsUri}'"
            );
            Process.Start(
                new ProcessStartInfo(FallbackDefenderSettingsUri) { UseShellExecute = true }
            );
        }
    }

    private static DefenderTamperProtectionStatus ReadLiveTamperProtectionStatus()
    {
        var (runtimeIsEnabled, realTimeEnabled, runningMode) = TryReadWmiMpComputerStatus();
        var registryValue = TamperProtectionRegistryValue.GetValue(0);

        // Only query SecurityCenter2 when WMI couldn't confirm runtime state and registry says ON,
        // so normal startups pay zero extra WMI cost while third-party AV replacements don't block.
        var hasThirdPartyAv =
            runtimeIsEnabled is null
            && registryValue is not (0 or 4)
            && AntiVirus.HasThirdPartyAntivirusInstalled();

        return new DefenderTamperProtectionStatus(
            runtimeIsEnabled,
            registryValue,
            realTimeEnabled,
            runningMode,
            hasThirdPartyAv
        );
    }

    private static (bool? IsTamperProtected, bool? RealTimeProtectionEnabled, string? AmRunningMode)
        TryReadWmiMpComputerStatus()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Defender",
                "SELECT IsTamperProtected, RealTimeProtectionEnabled, AMRunningMode FROM MSFT_MpComputerStatus"
            );

            foreach (ManagementObject status in searcher.Get())
            {
                using (status)
                {
                    var isTamperProtected = TryReadBoolProperty(status, "IsTamperProtected");
                    var realTimeEnabled = TryReadBoolProperty(status, "RealTimeProtectionEnabled");
                    var runningMode = TryReadStringProperty(status, "AMRunningMode");
                    if (isTamperProtected.HasValue || realTimeEnabled.HasValue || runningMode is not null)
                        return (isTamperProtected, realTimeEnabled, runningMode);
                }
            }

            Log.ZLogWarning(
                $"MSFT_MpComputerStatus returned no rows; using the registry fallback"
            );
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(
                ex,
                $"Failed to query MSFT_MpComputerStatus via WMI; using the registry fallback"
            );
        }

        return (null, null, null);
    }

    private static bool? TryReadBoolProperty(ManagementObject obj, string propertyName)
    {
        try
        {
            var raw = obj[propertyName];
            if (raw is bool b)
                return b;
            if (raw is not null && bool.TryParse(raw.ToString(), out b))
                return b;
        }
        catch
        {
            // Property may be missing on older Windows builds.
        }

        return null;
    }

    private static string? TryReadStringProperty(ManagementObject obj, string propertyName)
    {
        try
        {
            return obj[propertyName]?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
