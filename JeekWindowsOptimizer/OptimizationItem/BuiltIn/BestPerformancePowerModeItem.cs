using Microsoft.Win32;
using PowerManagerAPI;

namespace JeekWindowsOptimizer;

public class BestPerformancePowerModeItem : OptimizationItem
{
    public const string DefaultPowerSchemesPath =
        @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    public const string ActiveOverlayAcValueName = "ActiveOverlayAcPowerScheme";
    public const string ActiveOverlayDcValueName = "ActiveOverlayDcPowerScheme";

    public override string GroupNameKey => "System";
    public override string NameKey => "BestPerformancePowerModeName";
    public override string DescriptionKey => "BestPerformancePowerModeDescription";

    public override async Task Initialize()
    {
        IsOptimized = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            IsAcBestPerformanceActive
        );
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () =>
            {
                PowerManager.ActivePowerPlan = PowerPlan.Balanced;
                var targetMode = value ? PowerMode.BestPerformance : PowerMode.Balanced;

                if (PowerPlanSettings.IsOnAcPower())
                {
                    // When on AC, PowerSetActiveOverlayScheme updates ActiveOverlayAcPowerScheme
                    // immediately and notifies Windows without touching the battery (DC) mode.
                    PowerManager.PowerMode = targetMode;
                }
                else
                {
                    // On battery, write only the AC overlay value so DC battery power mode stays untouched.
                    WriteOverlayAcPowerScheme(
                        value ? PowerModeGuids.BestPerformance : PowerModeGuids.Balanced
                    );
                }

                return true;
            }
        );
    }

    public static bool IsAcBestPerformanceActive()
    {
        if (PowerManager.ActivePowerPlan != PowerPlan.Balanced)
            return false;

        if (TryReadOverlayAcPowerScheme(out var acOverlay))
            return acOverlay == PowerModeGuids.BestPerformance;

        return PowerPlanSettings.IsOnAcPower() && PowerManager.PowerMode == PowerMode.BestPerformance;
    }

    public static bool TryReadOverlayAcPowerScheme(
        out Guid overlayGuid,
        RegistryHive hive = RegistryHive.LocalMachine,
        string schemesPath = DefaultPowerSchemesPath
    )
    {
        overlayGuid = Guid.Empty;
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(schemesPath, writable: false);
        var raw = key?.GetValue(ActiveOverlayAcValueName)?.ToString();
        return Guid.TryParse(raw, out overlayGuid);
    }

    public static string? ReadOverlayPowerSchemeRaw(
        string valueName,
        RegistryHive hive = RegistryHive.LocalMachine,
        string schemesPath = DefaultPowerSchemesPath
    )
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(schemesPath, writable: false);
        return key?.GetValue(valueName)?.ToString();
    }

    public static void WriteOverlayAcPowerScheme(
        Guid overlayGuid,
        RegistryHive hive = RegistryHive.LocalMachine,
        string schemesPath = DefaultPowerSchemesPath
    )
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(schemesPath, writable: true);
        key.SetValue(ActiveOverlayAcValueName, overlayGuid.ToString("D"), RegistryValueKind.String);
    }
}
