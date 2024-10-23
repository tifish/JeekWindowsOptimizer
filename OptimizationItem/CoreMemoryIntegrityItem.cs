using JeekTools;

namespace JeekWindowsOptimizer;

public class CoreMemoryIntegrityItem : OptimizationItem
{
    public CoreMemoryIntegrityItem()
    {
        ShouldReboot = true;

        HasOptimized = Disabled;

        IsInitializing = false;
    }

    public override void HasOptimizedChanged(bool value)
    {
        Disabled = value;
    }

    public override string Name => "禁用内核内存完整性保护";

    public override string Description => """
                                          内核内存完整性保护，普通用户建议禁用。
                                          重启生效。
                                          """;

    private static readonly RegistryValue EnabledValue = new(
        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
        "Enabled");

    public bool Disabled
    {
        get => EnabledValue.GetValue(1) == 0;
        set
        {
            if (value)
                EnabledValue.SetValue(0);
            else
                EnabledValue.DeleteKey();
        }
    }
}
