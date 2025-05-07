using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

public class DisableWindowsDefenderPUAProtectionItem : OptimizationItem
{
    private static readonly ILogger Log = LogManager.CreateLogger<DisableWindowsDefenderPUAProtectionItem>();

    public override string GroupNameKey => "Kernel";
    public override string NameKey => "DisableWindowsDefenderPUAProtectionName";

    public override string DescriptionKey => "DisableWindowsDefenderPUAProtectionDescription";

    public override async Task Initialize()
    {
        try
        {
            PowerShellService.Commands.Clear();
            PowerShellService.AddCommand("Get-MpPreference").AddCommand("Select-Object").AddParameter("ExpandProperty", "PUAProtection");
            var result = await PowerShellService.InvokeAsync();
            IsOptimized = (byte)result.First().BaseObject == 0;
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Failed to call Get-MpPreference");
        }
    }

    protected override async Task<bool> IsOptimizedChanging(bool value)
    {
        try
        {
            PowerShellService.Commands.Clear();
            PowerShellService.AddCommand("Set-MpPreference").AddParameter("PUAProtection", value ? 0 : 1);
            await PowerShellService.InvokeAsync();
            return true;
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Failed to call Set-MpPreference");
            return false;
        }
    }
}
