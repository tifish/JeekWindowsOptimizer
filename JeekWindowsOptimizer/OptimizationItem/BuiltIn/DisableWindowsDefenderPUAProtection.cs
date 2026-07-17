using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

public class DisableWindowsDefenderPUAProtectionItem : OptimizationItem
{
    private static readonly ILogger Log =
        LogManager.CreateLogger<DisableWindowsDefenderPUAProtectionItem>();

    public override string GroupNameKey => "Kernel";
    public override string NameKey => "DisableWindowsDefenderPUAProtectionName";

    public override string DescriptionKey => "DisableWindowsDefenderPUAProtectionDescription";

    public DisableWindowsDefenderPUAProtectionItem()
    {
        Category = OptimizationItemCategory.Antivirus;
    }

    public override async Task Initialize()
    {
        var currentValue = IsOptimized;
        var isOptimized = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            async () =>
            {
                try
                {
                    PowerShellService.Commands.Clear();
                    PowerShellService
                        .AddCommand("Get-MpPreference")
                        .AddCommand("Select-Object")
                        .AddParameter("ExpandProperty", "PUAProtection");
                    var result = await PowerShellService.InvokeAsync();
                    return (byte)result.First().BaseObject == 0;
                }
                catch (Exception ex)
                {
                    Log.ZLogError(ex, $"Failed to call Get-MpPreference");
                    return currentValue;
                }
            }
        );
        IsOptimized = isOptimized;
    }

    protected override async Task<bool> IsOptimizedChanging(bool value)
    {
        return await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            async () =>
            {
                try
                {
                    PowerShellService.Commands.Clear();
                    PowerShellService
                        .AddCommand("Set-MpPreference")
                        .AddParameter("PUAProtection", value ? 0 : 1);
                    await PowerShellService.InvokeAsync();
                    return true;
                }
                catch (Exception ex)
                {
                    Log.ZLogError(ex, $"Failed to call Set-MpPreference");
                    return false;
                }
            }
        );
    }
}
