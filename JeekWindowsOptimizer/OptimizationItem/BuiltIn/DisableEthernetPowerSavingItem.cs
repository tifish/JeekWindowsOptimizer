namespace JeekWindowsOptimizer;

public sealed class DisableEthernetPowerSavingItem : OptimizationItem
{
    public override string GroupNameKey => "System";
    public override string NameKey => "DisableEthernetPowerSavingName";
    public override string DescriptionKey => "DisableEthernetPowerSavingDescription";

    public DisableEthernetPowerSavingItem()
    {
        ShouldReboot = true;
    }

    public override async Task Initialize()
    {
        IsOptimized = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () => EthernetPowerManagement.AreAllOptimized()
        );
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () => EthernetPowerManagement.Apply(value)
        );
    }
}
