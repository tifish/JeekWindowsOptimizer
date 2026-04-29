namespace JeekWindowsOptimizer;

public class TestItem : OptimizationItem
{
    public override string GroupNameKey => "System";
    public override string NameKey => "DisableWindowsRealTimeAntivirusName";

    public override string DescriptionKey => "DisableWindowsRealTimeAntivirusDescription";

    public override Task Initialize()
    {
        return Task.CompletedTask;
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        return Task.FromResult(true);
    }
}
