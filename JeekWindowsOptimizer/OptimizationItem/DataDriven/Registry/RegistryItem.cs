namespace JeekWindowsOptimizer;

public class RegistryItem(string groupNameKey, string nameKey, string descriptionKey)
    : OptimizationItem
{
    public override string GroupNameKey => groupNameKey;
    public override string NameKey => nameKey;
    public override string DescriptionKey => descriptionKey;

    public override async Task Initialize()
    {
        IsOptimized = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () => RegistryValues.All(value => value.IsOptimized)
        );
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () =>
            {
                foreach (var registryValue in RegistryValues)
                    registryValue.IsOptimized = value;

                return true;
            }
        );
    }

    public List<OptimizationRegistryValue> RegistryValues { get; } = [];
}
