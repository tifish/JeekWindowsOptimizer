using JeekTools;

namespace JeekWindowsOptimizer;

public class MeltdownAndSpectreItem : OptimizationItem
{
    public MeltdownAndSpectreItem()
    {
        ShouldReboot = true;

        HasOptimized = Disabled;

        IsInitializing = false;
    }

    public override void HasOptimizedChanged(bool value)
    {
        Disabled = value;
    }

    public override string Name => "禁用 Meltdown 和 Spectre 补丁";

    public override string Description => """
                                          Meltdown 和 Spectre 是 CPU 硬件漏洞，可以获取内核数据。补丁影响性能，请根据实际情况选择是否禁用。
                                          重启生效。
                                          """;

    private const string MemoryManagementKeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private static readonly RegistryValue FeatureSettingsValue = new(MemoryManagementKeyPath, "FeatureSettings");
    private static readonly RegistryValue FeatureSettingsOverrideValue = new(MemoryManagementKeyPath, "FeatureSettingsOverride");
    private static readonly RegistryValue FeatureSettingsOverrideMaskValue = new(MemoryManagementKeyPath, "FeatureSettingsOverrideMask");

    public bool Disabled
    {
        get => FeatureSettingsValue.GetValue(0) == 1
               && FeatureSettingsOverrideValue.GetValue(0) == 3
               && FeatureSettingsOverrideMaskValue.GetValue(0) == 3;
        set
        {
            if (value)
            {
                FeatureSettingsValue.SetValue(1);
                FeatureSettingsOverrideValue.SetValue(3);
                FeatureSettingsOverrideMaskValue.SetValue(3);
            }
            else
            {
                FeatureSettingsValue.SetValue(0);
                FeatureSettingsOverrideValue.DeleteValue();
                FeatureSettingsOverrideMaskValue.DeleteValue();
            }
        }
    }
}
