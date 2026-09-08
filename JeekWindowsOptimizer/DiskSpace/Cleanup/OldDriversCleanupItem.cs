namespace JeekWindowsOptimizer;

public sealed class OldDriversCleanupItem : DiskSpaceCleanupItem
{
    public override string NameKey => "OldDriversCleanupName";
    public override string DescriptionKey => "OldDriversCleanupDescription";
    protected override bool DefaultChecked => false;
    public int PackageCount { get; private set; }

    protected override async Task<long> ScanCore(CancellationToken cancellationToken)
    {
        var packages = await DriverStoreCleanup.Candidates(cancellationToken);
        PackageCount = packages.Count;
        return packages.Select(p => p.Directory).Distinct(StringComparer.OrdinalIgnoreCase)
            .Sum(path => FileSystemCleaner.GetDirectorySize(path, cancellationToken));
    }

    protected override async Task<bool> CleanCore(CancellationToken cancellationToken)
    {
        var candidates = await DriverStoreCleanup.Candidates(cancellationToken);
        foreach (var package in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Re-enumerate before deletion, since installations may run while a request is queued.
            if (!(await DriverStoreCleanup.Candidates(cancellationToken)).Any(p => p == package)) continue;
            await CleanupCommand.Run("pnputil.exe", DriverStoreCleanup.DeleteArguments(package.Published), cancellationToken);
        }
        return (await DriverStoreCleanup.Candidates(cancellationToken)).Count == 0;
    }
}
