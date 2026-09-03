namespace JeekWindowsOptimizer;

public class DeliveryOptimizationCacheCleanupItem : DiskSpaceCleanupItem
{
    public override string NameKey => "DeliveryOptimizationCacheCleanupName";
    public override string DescriptionKey => "DeliveryOptimizationCacheCleanupDescription";

    protected override Task<long> ScanCore(CancellationToken cancellationToken)
    {
        return Task.FromResult(DeliveryOptimizationCache.GetSize(cancellationToken));
    }

    protected override async Task<bool> CleanCore(CancellationToken cancellationToken)
    {
        await DeliveryOptimizationCache.Clean(cancellationToken);
        return true;
    }
}
