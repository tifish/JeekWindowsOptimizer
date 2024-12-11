namespace JeekWindowsOptimizer;

public class RegistryItem(string groupNameKey, string nameKey, string descriptionKey) : OptimizationItem
{
    public override string GroupNameKey => groupNameKey;
    public override string NameKey => nameKey;
    public override string DescriptionKey => descriptionKey;

    public void Initialized()
    {
        IsOptimized = RegistryValues.All(value => value.IsOptimized);
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        foreach (var registryValue in RegistryValues)
            registryValue.IsOptimized = value;

        return Task.FromResult(true);
    }

    public List<OptimizationRegistryValue> RegistryValues { get; } = [];
}
