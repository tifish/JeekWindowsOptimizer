using JeekTools;

namespace JeekWindowsOptimizer;

public class BestPerformancePowerModeItem : OptimizationItem
{
    public override string GroupNameKey => "System";
    public override string NameKey => "BestPerformancePowerModeName";

    public override string DescriptionKey => "BestPerformancePowerModeDescription";

    public BestPerformancePowerModeItem()
    {
        IsOptimized = Power.GetPowerPlan() == PowerPlanType.Balanced
                      && Power.GetPowerMode() == PowerModeType.BestPerformance;
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        var result1 = Power.SetPowerPlan(value ? PowerPlanType.HighPerformance : PowerPlanType.Balanced);
        var result2 = Power.SetPowerMode(value ? PowerModeType.BestPerformance : PowerModeType.Balanced);
        return Task.FromResult(result1 && result2);
    }
}
