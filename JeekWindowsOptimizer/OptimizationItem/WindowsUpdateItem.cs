using JeekTools;

namespace JeekWindowsOptimizer;

public class WindowsUpdateItem : OptimizationItem
{
    public override string GroupNameKey => "Security";
    public override string NameKey => "WindowsUpdateName";

    public override string DescriptionKey => "WindowsUpdateDescription";

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

    private readonly WindowsService _service = new("wuauserv");

    public override Task Initialize()
    {
        IsOptimized = _registryValues.All(value => !value.HasValue())
                      && _service.GetStartMode() != WindowsService.StartMode.Disabled;
        return Task.CompletedTask;
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        if (value)
        {
            _registryValues.ForEach(regValue => regValue.DeleteValue());

            if (_service.GetStartMode() == WindowsService.StartMode.Disabled)
                _service.SetStartMode(WindowsService.StartMode.Manual);
        }

        return Task.FromResult(value);
    }
}
