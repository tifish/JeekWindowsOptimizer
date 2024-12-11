using System.Management;
using JeekTools;

namespace JeekWindowsOptimizer;

public class WindowsActivatorItem : OptimizationItem
{
    public override string GroupNameKey => "System";
    public override string NameKey => "WindowsActivatorName";

    public override string DescriptionKey => "WindowsActivatorDescription";

    public WindowsActivatorItem()
    {
        ShouldTurnOffOnAccessProtection = true;

        IsOptimized = IsWindowsActivated();
    }

    private static bool IsWindowsActivated()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT LicenseStatus FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL");
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

        await Executor.RunAndWait(Path.Join(AppContext.BaseDirectory, @"Activator\Activate.cmd"));

        return IsWindowsActivated();
    }
}
