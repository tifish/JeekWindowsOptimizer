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
        // Enumerate once at deletion time: re-enumerating per package spawns pnputil again for
        // every candidate, and PnPUtil supplies the final in-use guard for each deletion anyway.
        var candidates = await DriverStoreCleanup.Candidates(cancellationToken);
        var complete = true;
        foreach (var package in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await CleanupCommand.Run("pnputil.exe", DriverStoreCleanup.DeleteArguments(package.Published), cancellationToken);
            }
            catch (IOException)
            {
                // A package that became busy or in use must not discard the packages already removed.
                complete = false;
            }
        }
        return complete && (await DriverStoreCleanup.Candidates(cancellationToken)).Count == 0;
    }
}
