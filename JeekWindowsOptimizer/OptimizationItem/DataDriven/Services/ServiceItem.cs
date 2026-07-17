namespace JeekWindowsOptimizer;

public class ServiceItem : OptimizationItem
{
    public override string GroupNameKey { get; }
    public override string NameKey { get; }
    public override string DescriptionKey { get; }

    private readonly string _servicePattern;
    private readonly bool _isPrefix;
    private readonly WindowsService.StartMode _defaultStartMode;
    private readonly Dictionary<string, WindowsService.StartMode> _restoreStartModes = new(
        StringComparer.OrdinalIgnoreCase
    );

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

        _isPrefix = serviceName.EndsWith('*');
        _servicePattern = _isPrefix ? serviceName[..^1] : serviceName;
        _defaultStartMode = defaultStartMode;
    }

    private List<string> ResolveServiceNames()
    {
        if (_isPrefix)
            return WindowsService.FindNamesByPrefix(_servicePattern);

        return [_servicePattern];
    }

    public Task<bool> ServiceExists()
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () =>
            {
                if (_isPrefix)
                    return WindowsService.FindNamesByPrefix(_servicePattern).Count > 0;

                using var service = new WindowsService(_servicePattern);
                return service.Exists();
            }
        );
    }

    public override async Task Initialize()
    {
        await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () =>
            {
                var names = ResolveServiceNames();
                if (names.Count == 0)
                {
                    IsOptimized = false;
                    return;
                }

                var allDisabled = true;
                foreach (var name in names)
                {
                    using var service = new WindowsService(name);
                    if (!service.Exists())
                        continue;

                    var startMode = service.GetStartMode();
                    if (startMode == WindowsService.StartMode.Disabled)
                    {
                        if (!_restoreStartModes.ContainsKey(name))
                            _restoreStartModes[name] = _defaultStartMode;
                    }
                    else
                    {
                        allDisabled = false;
                        _restoreStartModes[name] = startMode;
                    }
                }

                IsOptimized = allDisabled;
            }
        );
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () =>
            {
                var names = ResolveServiceNames();
                if (names.Count == 0)
                    return false;

                var ok = true;
                foreach (var name in names)
                {
                    using var service = new WindowsService(name);
                    if (!service.Exists())
                        continue;

                    if (value)
                    {
                        var current = service.GetStartMode();
                        if (current != WindowsService.StartMode.Disabled)
                            _restoreStartModes[name] = current;

                        service.Stop(); // best-effort: the service may already be stopped
                        if (!service.SetStartMode(WindowsService.StartMode.Disabled))
                            ok = false;
                    }
                    else
                    {
                        if (!_restoreStartModes.TryGetValue(name, out var restoreMode))
                            restoreMode = _defaultStartMode;

                        if (!service.SetStartMode(restoreMode))
                            ok = false;
                        service.Start(); // best-effort
                    }
                }

                return ok;
            }
        );
    }
}
