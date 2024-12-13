using JeekTools;

namespace JeekWindowsOptimizer;

public class BestPerformancePowerModeItem : OptimizationItem
{
    public override string GroupNameKey => "System";
    public override string NameKey => "BestPerformancePowerModeName";

    public override string DescriptionKey => "BestPerformancePowerModeDescription";

    public BestPerformancePowerModeItem()
    {
        IsOptimized = Power.GetPowerMode() == PowerModeType.BestPerformance;
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        var result = Power.SetPowerMode(value ? PowerModeType.BestPerformance : PowerModeType.Balanced);
        return Task.FromResult(result);
    }
}
