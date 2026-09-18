using System.Management;
using JeekTools;

namespace JeekWindowsOptimizer;

public class WindowsActivatorItem : OptimizationItem
{
    public override string GroupNameKey => "System";
    public override string NameKey => "WindowsActivatorName";

    public override string DescriptionKey => "WindowsActivatorDescription";

    private static readonly string ActivatorDirectory = Path.Join(
        AppContext.BaseDirectory,
        @"Tools\Activator"
    );

    /// <summary>The activator is not shipped; the item is offered only when its files are present.</summary>
    public static bool IsAvailable =>
        File.Exists(Path.Join(ActivatorDirectory, "Activate.cmd"))
        && File.Exists(Path.Join(ActivatorDirectory, "Activator.rar"))
        && File.Exists(Path.Join(ActivatorDirectory, "UnRAR.exe"));

    public override async Task Initialize()
    {
        ShouldTurnOffOnAccessProtection = true;
        IsOptimized = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            IsWindowsActivated
        );
    }

    private static bool IsWindowsActivated()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT LicenseStatus FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL"
            );
            foreach (var o in searcher.Get())
            {
                var obj = (ManagementObject)o;
                var licenseStatus = Convert.ToInt32(obj["LicenseStatus"]);
                // LicenseStatus: 1 = Licensed
                if (licenseStatus == 1)
                    return true;
            }
        }
        catch
        {
            // Handle exceptions if needed
        }

        return false;
    }

    protected override async Task<bool> IsOptimizedChanging(bool value)
    {
        if (!value)
            return false;

        await Executor.RunAndWait(Path.Join(ActivatorDirectory, "Activate.cmd"));

        return await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            IsWindowsActivated
        );
    }
}
