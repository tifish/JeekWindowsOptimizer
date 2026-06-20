namespace JeekWindowsOptimizer;

public class ServiceItem : OptimizationItem
{
    public override string GroupNameKey { get; }
    public override string NameKey { get; }
    public override string DescriptionKey { get; }

    private readonly string _serviceName;
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

        _serviceName = serviceName;

        // Windows default start mode from the data file, used as the restore value
        // when the service is already disabled at startup (original mode unknown).
        _restoreStartMode = defaultStartMode;
    }

    private WindowsService CreateService() => new(_serviceName);

    public Task<bool> ServiceExists()
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () =>
            {
                using var service = CreateService();
                return service.Exists();
            }
        );
    }

    public override async Task Initialize()
    {
        var startMode = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () =>
            {
                using var service = CreateService();
                return service.GetStartMode();
            }
        );
        IsOptimized = startMode == WindowsService.StartMode.Disabled;

        // Prefer the real current start mode as the restore value. If the service
        // is already disabled we cannot read its original mode, so keep the Windows
        // default supplied by the data file.
        if (startMode != WindowsService.StartMode.Disabled)
            _restoreStartMode = startMode;
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () =>
            {
                using var service = CreateService();
                if (value)
                {
                    service.Stop(); // best-effort: the service may already be stopped
                    return service.SetStartMode(WindowsService.StartMode.Disabled);
                }

                var restored = service.SetStartMode(_restoreStartMode);
                service.Start(); // best-effort
                return restored;
            }
        );
    }
}
