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

    public long ReclaimableBytes => SizeBytes ?? 0;

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
        var before = SizeBytes ?? 0;

        bool succeeded;
        try
        {
            succeeded = await Task.Run(() => CleanCore(cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            State = DiskSpaceItemState.Scanned;
            return 0;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            State = DiskSpaceItemState.Failed;
            return 0;
        }

        long after;
        try
        {
            after = await Task.Run(() => ScanCore(CancellationToken.None), CancellationToken.None);
        }
        catch
        {
            after = 0;
        }

        SizeBytes = after;
        FreedBytes = Math.Max(0, before - after);

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

    /// <summary>Runs on a thread-pool thread. Returns reclaimable bytes.</summary>
    protected abstract Task<long> ScanCore(CancellationToken cancellationToken);

    /// <summary>Runs on a thread-pool thread. Returns false when the cleanup did not complete.</summary>
    protected abstract Task<bool> CleanCore(CancellationToken cancellationToken);
}
