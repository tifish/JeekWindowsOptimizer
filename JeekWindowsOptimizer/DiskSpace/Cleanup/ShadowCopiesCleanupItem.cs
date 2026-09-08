namespace JeekWindowsOptimizer;

public sealed class ShadowCopiesCleanupItem : DiskSpaceCleanupItem
{
    public override string NameKey => "ShadowCopiesCleanupName";
    public override string DescriptionKey => "ShadowCopiesCleanupDescription";
    protected override bool DefaultChecked => false;
    public override bool CanClean => SnapshotCount > 1;
    public int SnapshotCount { get; private set; }

    protected override async Task<long> ScanCore(CancellationToken cancellationToken)
    {
        SnapshotCount = ShadowCopyStorage.List().Count;
        var used = await ShadowCopyStorage.UsedBytes(cancellationToken);
        return used;
    }

    protected override async Task<bool> CleanCore(CancellationToken cancellationToken)
    {
        var candidates = ShadowCopyStorage.KeepNewest(ShadowCopyStorage.List());
        foreach (var snapshot in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Revalidate each ID: another process may have removed the latest snapshot meanwhile.
            if (!ShadowCopyStorage.KeepNewest(ShadowCopyStorage.List()).Any(s => s.Id == snapshot.Id))
                continue;
            await CleanupCommand.Run("vssadmin.exe", "delete shadows /shadow=" + snapshot.Id + " /quiet", cancellationToken);
        }
        return ShadowCopyStorage.List().Count <= 1;
    }
}
