namespace JeekWindowsOptimizer;

public class WindowsUpdateCacheCleanupItem : DiskSpaceCleanupItem
{
    public override string NameKey => "WindowsUpdateCacheCleanupName";
    public override string DescriptionKey => "WindowsUpdateCacheCleanupDescription";

    protected override Task<long> ScanCore(CancellationToken cancellationToken)
    {
        return Task.FromResult(WindowsUpdateCache.GetSize(cancellationToken));
    }

    protected override async Task<bool> CleanCore(CancellationToken cancellationToken)
    {
        return await WindowsUpdateCache.Clean(cancellationToken) >= 0;
    }
}
