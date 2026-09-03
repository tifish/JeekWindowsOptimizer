namespace JeekWindowsOptimizer;

/// <summary>
///     WinSxS superseded components via DISM /StartComponentCleanup. Deliberately
///     without /ResetBase: that variant also drops the components kept so recent
///     updates can still be uninstalled, which is not something Windows can rebuild.
///     It lives in the Tools tab instead, for users who want the last gigabytes.
///     Scan runs /AnalyzeComponentStore, which itself takes a while.
/// </summary>
public class ComponentStoreCleanupItem : DiskSpaceCleanupItem
{
    public override string NameKey => "ComponentStoreCleanupName";
    public override string DescriptionKey => "ComponentStoreCleanupDescription";

    public override bool IsSlow => true;

    public ComponentStore.Analysis? LastAnalysis { get; private set; }

    protected override async Task<long> ScanCore(CancellationToken cancellationToken)
    {
        var analysis = await ComponentStore.Analyze(cancellationToken);
        LastAnalysis = analysis;
        if (analysis is null)
            throw new InvalidOperationException("DISM /AnalyzeComponentStore produced no usable output.");

        return analysis.Value.ReclaimableBytes;
    }

    protected override Task<bool> CleanCore(CancellationToken cancellationToken)
    {
        return ComponentStore.Cleanup(resetBase: false, cancellationToken);
    }
}
