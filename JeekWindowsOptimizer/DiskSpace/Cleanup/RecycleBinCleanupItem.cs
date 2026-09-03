namespace JeekWindowsOptimizer;

public class RecycleBinCleanupItem : DiskSpaceCleanupItem
{
    public override string NameKey => "RecycleBinCleanupName";
    public override string DescriptionKey => "RecycleBinCleanupDescription";

    protected override Task<long> ScanCore(CancellationToken cancellationToken)
    {
        return Task.FromResult(RecycleBin.GetSize());
    }

    protected override Task<bool> CleanCore(CancellationToken cancellationToken)
    {
        return Task.FromResult(RecycleBin.Empty());
    }
}
