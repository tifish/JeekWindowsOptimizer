namespace JeekWindowsOptimizer;

public class ServiceItem : OptimizationItem
{
    public override string GroupNameKey { get; }
    public override string NameKey { get; }
    public override string DescriptionKey { get; }

    private readonly WindowsService _service;

    public ServiceItem(string groupNameKey, string nameKey, string descriptionKey, string serviceName)
    {
        GroupNameKey = groupNameKey;
        NameKey = nameKey;
        DescriptionKey = descriptionKey;

        _service = new WindowsService(serviceName);

        IsOptimized = _service.GetStartMode() == WindowsService.StartMode.Disabled;
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        if (value)
        {
            _service.Stop();
            _service.SetStartMode(WindowsService.StartMode.Disabled);
        }
        else
        {
            _service.SetStartMode(WindowsService.StartMode.Automatic);
            _service.Start();
        }

        return Task.FromResult(true);
    }
}
