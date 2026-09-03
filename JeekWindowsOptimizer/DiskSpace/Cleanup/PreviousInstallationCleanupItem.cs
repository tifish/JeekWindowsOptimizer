namespace JeekWindowsOptimizer;

/// <summary>
///     Windows.old and the hidden upgrade staging folders. Removed through Disk
///     Cleanup's own handlers so ownership and pending-rollback bookkeeping are
///     handled by Windows. Deleting it forfeits rolling back to the previous build.
/// </summary>
public class PreviousInstallationCleanupItem : DiskSpaceCleanupItem
{
    public override string NameKey => "PreviousInstallationCleanupName";
    public override string DescriptionKey => "PreviousInstallationCleanupDescription";

    protected override bool DefaultChecked => false;
    public override bool IsSlow => true;

    public static IReadOnlyList<string> Paths
    {
        get
        {
            var root = DiskSpaceItemManager.SystemDriveRoot;
            return
            [
                Path.Join(root, "Windows.old"),
                Path.Join(root, "$Windows.~BT"),
                Path.Join(root, "$Windows.~WS"),
                Path.Join(root, "$GetCurrent"),
            ];
        }
    }

    protected override Task<long> ScanCore(CancellationToken cancellationToken)
    {
        return Task.FromResult(
            Paths.Sum(path => FileSystemCleaner.GetDirectorySize(path, cancellationToken))
        );
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
