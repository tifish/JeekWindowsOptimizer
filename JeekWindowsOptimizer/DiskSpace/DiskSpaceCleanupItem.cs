using CommunityToolkit.Mvvm.ComponentModel;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

/// <summary>
///     A reclaimable-space item: scan reports how many bytes can be freed, clean
///     frees them and re-scans so the row shows what is left.
/// </summary>
public abstract partial class DiskSpaceCleanupItem : DiskSpaceItem
{
    public override string GroupNameKey => "DiskSpaceCleanup";

    protected DiskSpaceCleanupItem()
    {
        IsChecked = DefaultChecked;
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(State) or nameof(QueuePosition))
                OnPropertyChanged(nameof(CleanButtonText));
        };
    }

    /// <summary>Items that give up something Windows cannot rebuild start unchecked.</summary>
    protected virtual bool DefaultChecked => true;

    private bool _autoCheckApplied;

    /// <summary>
    ///     Lets an item revise its checked state once the first scan knows enough — the
    ///     previous installation only keeps a rollback for a few days, and after that
    ///     window there is nothing left to lose. Applied once so a later rescan never
    ///     overrides what the user picked.
    /// </summary>
    protected virtual bool? AutoCheckAfterScan => null;

    /// <summary>True for operations that run for minutes (DISM, cleanmgr).</summary>
    public virtual bool IsSlow => false;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    public partial long FreedBytes { get; private set; }

    [ObservableProperty]
    public partial bool IsFreedBytesKnown { get; private set; }

    public string CleanButtonText => Localizer.Get(QueuePosition > 0 ? "DiskSpaceQueuedButton"
        : State == DiskSpaceItemState.Working ? "DiskSpaceCleaning" : "CleanDiskSpaceItem");

    public virtual bool CanClean => true;

    public virtual long ReclaimableBytes => CanClean ? SizeBytes ?? 0 : 0;

    public void ToggleChecked()
    {
        IsChecked = !IsChecked;
    }

    protected override async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        SizeBytes = await Task.Run(() => ScanCore(cancellationToken), cancellationToken);

        if (!_autoCheckApplied && AutoCheckAfterScan is { } shouldCheck)
        {
            _autoCheckApplied = true;
            IsChecked = shouldCheck;
        }
    }

    /// <summary>Frees the space and returns the bytes actually reclaimed (measured by re-scan).</summary>
    public async Task<long> CleanAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
            return 0;

        State = DiskSpaceItemState.Working;
        ErrorMessage = null;
        FreedBytes = 0;
        IsFreedBytesKnown = false;

        bool succeeded;
        try
        {
            // Measure immediately before deleting; the last UI scan may be stale.
            SizeBytes = await Task.Run(() => ScanCore(cancellationToken), cancellationToken);
            succeeded = await Task.Run(() => CleanCore(cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            SizeBytes = null;
            ErrorMessage = Localizer.Get("DiskSpaceCleanCancelled");
            State = DiskSpaceItemState.Failed;
            return 0;
        }
        catch (Exception ex)
        {
            SizeBytes = null;
            ErrorMessage = ex.Message;
            State = DiskSpaceItemState.Failed;
            return 0;
        }

        long after;
        try
        {
            after = await Task.Run(() => ScanCore(CancellationToken.None), CancellationToken.None);
        }
        catch (Exception ex)
        {
            SizeBytes = null;
            ErrorMessage = Localizer.Get("DiskSpaceCleanMeasurementFailed") + " " + ex.Message;
            State = DiskSpaceItemState.Failed;
            return 0;
        }

        var before = SizeBytes.Value;
        SizeBytes = after;
        FreedBytes = Math.Max(0, before - after);
        IsFreedBytesKnown = true;

        if (succeeded)
        {
            State = DiskSpaceItemState.Done;
        }
        else
        {
            ErrorMessage ??= Localizer.Get("DiskSpaceCleanIncomplete");
            State = DiskSpaceItemState.Failed;
        }

        return FreedBytes;
    }

    protected override string BuildStatusText()
    {
        if (QueuePosition > 0)
            return string.Format(Localizer.Get("DiskSpaceQueuePosition"), QueuePosition);
        return State switch
        {
            DiskSpaceItemState.Working => Localizer.Get("DiskSpaceCleaning"),
            DiskSpaceItemState.Done => string.Format(
                Localizer.Get("DiskSpaceCleaned"),
                ByteSize.Format(FreedBytes)
            ),
            _ => base.BuildStatusText(),
        };
    }

    public override void NotifyLanguageChanged()
    {
        base.NotifyLanguageChanged();
        OnPropertyChanged(nameof(CleanButtonText));
    }

    /// <summary>Runs on a thread-pool thread. Returns reclaimable bytes.</summary>
    protected abstract Task<long> ScanCore(CancellationToken cancellationToken);

    /// <summary>Runs on a thread-pool thread. Returns false when the cleanup did not complete.</summary>
    protected abstract Task<bool> CleanCore(CancellationToken cancellationToken);
}
