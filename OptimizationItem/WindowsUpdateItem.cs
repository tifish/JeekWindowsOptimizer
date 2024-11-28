using JeekTools;

namespace JeekWindowsOptimizer;

public class WindowsUpdateItem : OptimizationItem
{
    public override string GroupName => "安全";
    public override string Name => "启用 Windows 更新";

    public override string Description => """
                                          Windows 更新可以修复系统漏洞，提高系统安全性，建议启用。
                                          立即生效。
                                          """;

    private const string KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

    private readonly List<RegistryValue> _registryValues =
    [
        new(KeyPath, "PauseFeatureUpdatesStartTime"),
        new(KeyPath, "PauseFeatureUpdatesEndTime"),
        new(KeyPath, "PauseQualityUpdatesStartTime"),
        new(KeyPath, "PauseQualityUpdatesEndTime"),
        new(KeyPath, "PauseUpdatesExpiryTime"),
        new(KeyPath, "PauseUpdatesStartTime"),
    ];

    public WindowsUpdateItem()
    {
        IsOptimized = _registryValues.All(value => !value.HasValue());
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        if (value)
            _registryValues.ForEach(regValue => regValue.DeleteValue());

        return Task.FromResult(value);
    }
}
