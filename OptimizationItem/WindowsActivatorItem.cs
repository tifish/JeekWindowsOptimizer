using System.Management;
using JeekTools;

namespace JeekWindowsOptimizer;

public class WindowsActivatorItem : OptimizationItem
{
    public override string GroupName => "系统";
    public override string Name => "激活 Windows";

    public override string Description => """
                                          未激活的 Windows 缺少部分功能，性能也不是最佳，建议激活。
                                          立即生效。
                                          """;

    public WindowsActivatorItem()
    {
        ShouldTurnOffRealTimeProtection = true;

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

        await Executor.RunAndWait(Path.Join(AppContext.BaseDirectory, @"Activate\Activate.cmd"));

        return IsWindowsActivated();
    }
}
