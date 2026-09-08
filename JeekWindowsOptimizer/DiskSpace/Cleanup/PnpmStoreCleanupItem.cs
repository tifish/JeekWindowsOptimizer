using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

public sealed class PnpmStoreCleanupItem : DiskSpaceCleanupItem
{
    public override string GroupNameKey => DeveloperCacheCleanupItem.DeveloperGroup;
    public override string NameKey => "PnpmStoreCleanupName";
    public override string DescriptionKey => "PnpmStoreCleanupDescription";
    protected override bool DefaultChecked => false;
    public string? StorePath { get; private set; }
    private readonly IPnpmStore _store;
    public PnpmStoreCleanupItem() : this(new PnpmStore()) { }
    internal PnpmStoreCleanupItem(IPnpmStore store) { _store = store; }

    protected override async Task<long> ScanCore(CancellationToken cancellationToken)
    {
        StorePath = await _store.GetPathAsync(cancellationToken);
        if (StorePath is null || !DiskSpaceItemManager.IsOnSystemDrive(StorePath)) return 0;
        PnpmStore.ValidateStore(StorePath, cancellationToken);
        return FixedDirectoryCleanupItem.Measure(StorePath, cancellationToken);
    }

    protected override async Task<bool> CleanCore(CancellationToken cancellationToken)
    {
        if (StorePath is null || !DiskSpaceItemManager.IsOnSystemDrive(StorePath)) return true;
        PnpmStore.ValidateStore(StorePath, cancellationToken);
        await _store.PruneAsync(StorePath, cancellationToken);
        // Referenced packages remaining after a successful prune are expected.
        return true;
    }

    protected override string BuildStatusText()
    {
        var status = base.BuildStatusText();
        if (status.Length > 0) return status;
        return State == DiskSpaceItemState.Scanned ? StorePath ?? Localizer.Get("PnpmNotInstalled") : "";
    }
}
