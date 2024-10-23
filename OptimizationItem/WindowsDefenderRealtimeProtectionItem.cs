using JeekTools;

namespace JeekWindowsOptimizer;

public class WindowsDefenderRealtimeProtectionItem : OptimizationItem
{
    public WindowsDefenderRealtimeProtectionItem()
    {
        ShouldTurnOffTamperProtection = true;
        ShouldUpdateGroupPolicy = true;

        HasOptimized = Disabled;

        IsInitializing = false;
    }

    public override void HasOptimizedChanged(bool value)
    {
        Disabled = value;
    }

    public override string Name => "禁用 Windows 实时防病毒";

    public override string Description => """
                                          Windows 实时防病毒会影响所有文件访问的性能，建议禁用，之后定期手动扫描病毒。
                                          立即生效。
                                          """;

    private static readonly RegistryValue DisableRealtimeMonitoringRegistryValue = new(
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection",
        "DisableRealtimeMonitoring");

    public bool Disabled
    {
        get => DisableRealtimeMonitoringRegistryValue.GetValue(0) == 1;
        set
        {
            if (value)
                DisableRealtimeMonitoringRegistryValue.SetValue(1);
            else
                DisableRealtimeMonitoringRegistryValue.DeleteValue();
        }
    }
}
