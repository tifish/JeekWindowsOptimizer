using DotNetRun;

namespace JeekWindowsOptimizer;

/// <summary>
/// Disables NTFS LastAccessTime updates.
/// On Windows 10 1803+, <c>NtfsDisableLastAccessUpdate</c> is a bitfield:
/// bit0 = disable updates, bit1 = system managed, bit31 = flags initialized.
/// Writing plain <c>1</c> is rewritten at boot to system-managed
/// <c>0x80000003</c>, so status checks that require exact <c>1</c> fail after reboot.
/// We write <c>0x80000001</c> (user managed, disabled, flags initialized) so the
/// setting persists, and treat any value with the disable bit set as optimized.
/// </summary>
public class DisableNtfsLastAccessUpdateItem : OptimizationItem
{
    private const int DisableBit = 0x1;
    private const int SystemManagedBit = 0x2;
    private const int FlagsInitializedBit = unchecked((int)0x80000000);

    /// <summary>User managed, Last Access updates disabled, flags initialized.</summary>
    private const int OptimizedValue = FlagsInitializedBit | DisableBit; // 0x80000001

    /// <summary>System managed default style (Windows may still decide based on volume size).</summary>
    private const int UnoptimizedValue = FlagsInitializedBit | SystemManagedBit; // 0x80000002

    private readonly RegistryValue _value = new(
        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem",
        "NtfsDisableLastAccessUpdate"
    );

    public override string GroupNameKey => "Kernel";
    public override string NameKey => "DisableNtfsLastAccessUpdateName";
    public override string DescriptionKey => "DisableNtfsLastAccessUpdateDescription";

    public override async Task Initialize()
    {
        IsOptimized = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            IsLastAccessDisabled
        );
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () =>
            {
                _value.SetValue(value ? OptimizedValue : UnoptimizedValue);
                return IsLastAccessDisabled() == value;
            }
        );
    }

    private bool IsLastAccessDisabled()
    {
        // Missing value → default treated as enabled (not optimized).
        var current = _value.GetValue(0);
        return (current & DisableBit) != 0;
    }
}
