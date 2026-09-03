namespace JeekWindowsOptimizer;

/// <summary>
///     WinSxS superseded components via DISM /StartComponentCleanup /ResetBase.
///     Scan runs /AnalyzeComponentStore, which itself takes a while.
/// </summary>
public class ComponentStoreCleanupItem : DiskSpaceCleanupItem
{
    public override string NameKey => "ComponentStoreCleanupName";
    public override string DescriptionKey => "ComponentStoreCleanupDescription";

    protected override bool DefaultChecked => false;
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
        return ComponentStore.Cleanup(resetBase: true, cancellationToken);
    }
}
