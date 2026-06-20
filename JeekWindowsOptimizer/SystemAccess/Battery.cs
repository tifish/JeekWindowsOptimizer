using System.Management;

namespace JeekWindowsOptimizer;

public static class Battery
{
    public static Task<bool> HasBatteryAsync()
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            HasBattery
        );
    }

    public static bool HasBattery()
    {
        var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Battery");
        var queryCollection = searcher.Get();
        return queryCollection.Count > 0;
    }
}
