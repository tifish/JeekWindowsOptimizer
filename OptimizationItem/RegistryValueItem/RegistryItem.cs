namespace JeekWindowsOptimizer;

public class RegistryItem(string groupName, string name, string description) : OptimizationItem
{
    public override string GroupName { get; } = groupName;
    public override string Name { get; } = name;
    public override string Description { get; } = description;

    public void Initialized()
    {
        HasOptimized = RegistryValues.All(value => value.HasOptimized);

        IsInitializing = false;
    }

    public override Task<bool> OnHasOptimizedChanging(bool value)
    {
        foreach (var registryValue in RegistryValues)
            registryValue.HasOptimized = value;

        return Task.FromResult(true);
    }

    public List<OptimizationRegistryValue> RegistryValues { get; } = [];
}
