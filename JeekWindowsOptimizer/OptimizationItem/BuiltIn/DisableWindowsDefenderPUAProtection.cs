using JeekTools;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ZLogger;

namespace JeekWindowsOptimizer;

public class DisableWindowsDefenderPUAProtectionItem : OptimizationItem
{
    private static readonly ILogger Log =
        LogManager.CreateLogger<DisableWindowsDefenderPUAProtectionItem>();

    public override string GroupNameKey => "System";
    public override string NameKey => "DisableWindowsDefenderPUAProtectionName";

    public override string DescriptionKey => "DisableWindowsDefenderPUAProtectionDescription";

    public DisableWindowsDefenderPUAProtectionItem()
    {
        Category = OptimizationItemCategory.Antivirus;
    }

    public override async Task Initialize()
    {
        var fromRegistry = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            ReadPuaProtectionFromRegistry
        );
        if (fromRegistry is { } value)
        {
            IsOptimized = value == 0;
            return;
        }

        // Get-MpPreference costs several seconds; only needed when no registry value is present.
        // Let a failure propagate so the caller reports the item as not checked.
        var isOptimized = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            async () =>
            {
                PowerShellService.Commands.Clear();
                PowerShellService
                    .AddCommand("Get-MpPreference")
                    .AddCommand("Select-Object")
                    .AddParameter("ExpandProperty", "PUAProtection");
                var result = await PowerShellService.InvokeAsync();
                return (byte)result.First().BaseObject == 0;
            }
        );
        IsOptimized = isOptimized;
    }

    /// <summary>
    /// Group policy wins over the local preference; the legacy MpEngine policy covers older
    /// Windows 10 builds. The local value is what Set-MpPreference writes.
    /// </summary>
    private static int? ReadPuaProtectionFromRegistry()
    {
        (string KeyPath, string ValueName)[] sources =
        [
            (@"SOFTWARE\Policies\Microsoft\Windows Defender", "PUAProtection"),
            (@"SOFTWARE\Policies\Microsoft\Windows Defender\MpEngine", "MpEnablePus"),
            (@"SOFTWARE\Microsoft\Windows Defender", "PUAProtection"),
        ];

        foreach (var (keyPath, valueName) in sources)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                if (key?.GetValue(valueName) is int value)
                    return value;
            }
            catch (Exception ex)
            {
                Log.ZLogWarning(ex, $"Failed to read {keyPath}\\{valueName}");
            }
        }

        return null;
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
