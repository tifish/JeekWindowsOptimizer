namespace JeekWindowsOptimizer;

public class ServiceItem : OptimizationItem
{
    public override string GroupName { get; }
    public override string Name { get; }
    public override string Description { get; }

    private readonly WindowsService _service;

    public ServiceItem(string groupName, string name, string description, string serviceName)
    {
        GroupName = groupName;
        Name = name;
        Description = description;

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
