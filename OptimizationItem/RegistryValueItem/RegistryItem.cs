namespace JeekWindowsOptimizer;

public class RegistryItem(string groupName, string name, string description) : OptimizationItem
{
    public override string GroupName { get; } = groupName;
    public override string Name { get; } = name;
    public override string Description { get; } = description;

    public void Initialized()
    {
        IsOptimized = RegistryValues.All(value => value.IsOptimized);

        IsInitializing = false;
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        foreach (var registryValue in RegistryValues)
            registryValue.IsOptimized = value;

        return Task.FromResult(true);
    }

    public List<OptimizationRegistryValue> RegistryValues { get; } = [];
}
