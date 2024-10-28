namespace JeekWindowsOptimizer;

public class RegistryItem : OptimizationItem
{
    public override string Name { get; }
    public override string Description { get; }

    public RegistryItem(string name, string description)
    {
        Name = name;
        Description = description;

        HasOptimized = RegistryValues.All(value => value.HasOptimized);

        IsInitializing = false;
    }

    public override void HasOptimizedChanged(bool value)
    {
        foreach (var registryValue in RegistryValues)
            registryValue.HasOptimized = value;
    }

    public List<OptimizationRegistryValue> RegistryValues { get; } = [];
}
