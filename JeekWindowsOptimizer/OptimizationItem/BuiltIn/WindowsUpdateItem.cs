using DotNetRun;

namespace JeekWindowsOptimizer;

public class WindowsUpdateItem : OptimizationItem
{
    public override string GroupNameKey => "Security";
    public override string NameKey => "WindowsUpdateName";

    public override string DescriptionKey => "WindowsUpdateDescription";

    private const string KeyPath =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

    private readonly List<RegistryValue> _registryValues =
    [
        new(KeyPath, "PauseFeatureUpdatesStartTime"),
        new(KeyPath, "PauseFeatureUpdatesEndTime"),
        new(KeyPath, "PauseQualityUpdatesStartTime"),
        new(KeyPath, "PauseQualityUpdatesEndTime"),
        new(KeyPath, "PauseUpdatesExpiryTime"),
        new(KeyPath, "PauseUpdatesStartTime"),
    ];

    private static WindowsService CreateWindowsUpdateService() => new("wuauserv");

    public override async Task Initialize()
    {
        IsOptimized = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () =>
            {
                if (!_registryValues.All(value => !value.HasValue()))
                    return false;

                using var service = CreateWindowsUpdateService();
                return service.GetStartMode() != WindowsService.StartMode.Disabled;
            }
        );
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () =>
            {
                if (value)
                {
                    _registryValues.ForEach(regValue => regValue.DeleteValue());

                    using var service = CreateWindowsUpdateService();
                    if (service.GetStartMode() == WindowsService.StartMode.Disabled)
                        service.SetStartMode(WindowsService.StartMode.Manual);
                }

                return value;
            }
        );
    }
}
