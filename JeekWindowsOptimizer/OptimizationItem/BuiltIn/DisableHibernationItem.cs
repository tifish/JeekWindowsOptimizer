using System.Diagnostics;
using DotNetRun;

namespace JeekWindowsOptimizer;

public class DisableHibernationItem : OptimizationItem
{
    public override string GroupNameKey => "System";
    public override string NameKey => "DisableHibernationName";
    public override string DescriptionKey => "DisableHibernationDescription";

    private readonly RegistryValue _hibernateEnabledValue = new(
        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power",
        "HibernateEnabled"
    );

    public DisableHibernationItem()
    {
        Category = OptimizationItemCategory.Personal;
    }

    public override Task Initialize()
    {
        IsOptimized = _hibernateEnabledValue.GetValue(1) == 0;
        return Task.CompletedTask;
    }

    protected override async Task<bool> IsOptimizedChanging(bool value)
    {
        // powercfg toggles HibernateEnabled and adds/removes hiberfil.sys.
        using var proc = Process.Start(
            new ProcessStartInfo("powercfg.exe", value ? "/hibernate off" : "/hibernate on")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        );

        if (proc is null)
            return false;

        await proc.WaitForExitAsync();
        return true;
    }
}
