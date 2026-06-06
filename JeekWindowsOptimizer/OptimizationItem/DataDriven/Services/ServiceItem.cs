namespace JeekWindowsOptimizer;

public class ServiceItem : OptimizationItem
{
    public override string GroupNameKey { get; }
    public override string NameKey { get; }
    public override string DescriptionKey { get; }

    private readonly WindowsService _service;
    private WindowsService.StartMode _restoreStartMode;

    public ServiceItem(
        string groupNameKey,
        string nameKey,
        string descriptionKey,
        OptimizationItemCategory category,
        string serviceName,
        WindowsService.StartMode defaultStartMode
    )
    {
        GroupNameKey = groupNameKey;
        NameKey = nameKey;
        DescriptionKey = descriptionKey;
        Category = category;

        _service = new WindowsService(serviceName);

        // Windows default start mode from the data file, used as the restore value
        // when the service is already disabled at startup (original mode unknown).
        _restoreStartMode = defaultStartMode;
    }

    public bool ServiceExists => _service.Exists();

    public override Task Initialize()
    {
        var startMode = _service.GetStartMode();
        IsOptimized = startMode == WindowsService.StartMode.Disabled;

        // Prefer the real current start mode as the restore value. If the service
        // is already disabled we cannot read its original mode, so keep the Windows
        // default supplied by the data file.
        if (startMode != WindowsService.StartMode.Disabled)
            _restoreStartMode = startMode;

        return Task.CompletedTask;
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        if (value)
        {
            _service.Stop(); // best-effort: the service may already be stopped
            return Task.FromResult(_service.SetStartMode(WindowsService.StartMode.Disabled));
        }

        var restored = _service.SetStartMode(_restoreStartMode);
        _service.Start(); // best-effort
        return Task.FromResult(restored);
    }
}
