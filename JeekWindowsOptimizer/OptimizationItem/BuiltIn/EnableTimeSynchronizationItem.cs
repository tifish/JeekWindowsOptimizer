namespace JeekWindowsOptimizer;

public class EnableTimeSynchronizationItem : OptimizationItem
{
    public override string GroupNameKey => "System";
    public override string NameKey => "EnableTimeSynchronizationName";
    public override string DescriptionKey => "EnableTimeSynchronizationDescription";

    public override async Task Initialize()
    {
        IsOptimized = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () => WindowsTimeSynchronization.GetStatus().IsEnabled
        );
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () => WindowsTimeSynchronization.SetEnabled(value)
        );
    }
}
