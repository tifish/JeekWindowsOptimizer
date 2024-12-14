using PowerManagerAPI;

namespace JeekWindowsOptimizer;

public class BestPerformancePowerModeItem : OptimizationItem
{
    public override string GroupNameKey => "System";
    public override string NameKey => "BestPerformancePowerModeName";
    public override string DescriptionKey => "BestPerformancePowerModeDescription";

    public BestPerformancePowerModeItem()
    {
        IsOptimized = PowerManager.ActivePowerPlan == PowerPlan.Balanced
                      && PowerManager.PowerMode == PowerMode.BestPerformance;
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        PowerManager.ActivePowerPlan = PowerPlan.Balanced;
        PowerManager.PowerMode = value ? PowerMode.BestPerformance : PowerMode.Balanced;
        return Task.FromResult(true);
    }
}
