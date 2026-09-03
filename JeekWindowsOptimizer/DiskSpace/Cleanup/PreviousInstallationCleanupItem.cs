using Jeek.Avalonia.Localization;
using Microsoft.Win32;

namespace JeekWindowsOptimizer;

/// <summary>
///     Windows.old and the hidden upgrade staging folders. Removed through Disk
///     Cleanup's own handlers so ownership and pending-rollback bookkeeping are
///     handled by Windows.
///     Windows only offers "go back to the previous version" for a limited number of
///     days and then deletes the folder itself, so the row starts checked once that
///     window has passed (or when only staging leftovers remain) and stays unchecked
///     while a rollback is still possible.
/// </summary>
public class PreviousInstallationCleanupItem : DiskSpaceCleanupItem
{
    /// <summary>Windows' own default; DISM /Set-OSUninstallWindow writes the override read below.</summary>
    private const int DefaultUninstallWindowDays = 10;

    public override string NameKey => "PreviousInstallationCleanupName";
    public override string DescriptionKey => "PreviousInstallationCleanupDescription";

    protected override bool DefaultChecked => false;
    public override bool IsSlow => true;

    protected override bool? AutoCheckAfterScan => !IsRollbackAvailable;

    public bool IsRollbackAvailable { get; private set; }
    public int UninstallWindowDays { get; private set; } = DefaultUninstallWindowDays;
    public int RollbackDaysLeft { get; private set; }

    public static string WindowsOldPath => Path.Join(DiskSpaceItemManager.SystemDriveRoot, "Windows.old");

    public static IReadOnlyList<string> Paths
    {
        get
        {
            var root = DiskSpaceItemManager.SystemDriveRoot;
            return
            [
                WindowsOldPath,
                Path.Join(root, "$Windows.~BT"),
                Path.Join(root, "$Windows.~WS"),
                Path.Join(root, "$GetCurrent"),
            ];
        }
    }

    protected override Task<long> ScanCore(CancellationToken cancellationToken)
    {
        UpdateRollbackState();
        return Task.FromResult(
            Paths.Sum(path => FileSystemCleaner.GetDirectorySize(path, cancellationToken))
        );
    }

    private void UpdateRollbackState()
    {
        UninstallWindowDays = ReadUninstallWindowDays();
        RollbackDaysLeft = 0;
        IsRollbackAvailable = false;

        if (!Directory.Exists(WindowsOldPath))
            return;

        try
        {
            var age = DateTime.Now - Directory.GetCreationTime(WindowsOldPath);
            var daysLeft = UninstallWindowDays - (int)age.TotalDays;
            if (daysLeft <= 0)
                return;

            RollbackDaysLeft = daysLeft;
            IsRollbackAvailable = true;
        }
        catch
        {
            // Unreadable timestamp: assume the rollback still works and stay unchecked.
            IsRollbackAvailable = true;
        }
    }

    private static int ReadUninstallWindowDays()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\Setup\Uninstall");
            if (key?.GetValue("UninstallWindow") is int days && days > 0)
                return days;
        }
        catch
        {
            // Key is absent on a machine that never upgraded; the default applies.
        }

        return DefaultUninstallWindowDays;
    }

    protected override string BuildStatusText()
    {
        if (State == DiskSpaceItemState.Scanned && ReclaimableBytes > 0)
        {
            return IsRollbackAvailable
                ? string.Format(Localizer.Get("PreviousInstallationRollbackAvailable"), RollbackDaysLeft)
                : Localizer.Get("PreviousInstallationRollbackExpired");
        }

        return base.BuildStatusText();
    }

    protected override async Task<bool> CleanCore(CancellationToken cancellationToken)
    {
        if (Paths.All(path => !Directory.Exists(path)))
            return true;

        return await DiskCleanupTool.Run(
            DiskCleanupTool.PreviousInstallationHandlers,
            cancellationToken
        );
    }
}
