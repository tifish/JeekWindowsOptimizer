using CommunityToolkit.Mvvm.ComponentModel;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

public enum DiskSpaceItemState
{
    NotScanned,
    Scanning,
    Scanned,
    Working,
    Done,
    Failed,
}

/// <summary>
///     One row on the Disk Space tab. Unlike <see cref="OptimizationItem" /> there is
///     no persistent on/off state: an item measures how much system-drive space it
///     concerns and performs a one-shot action (clean or move). All members must be
///     used from the UI thread; heavy work is dispatched to the thread pool inside.
/// </summary>
public abstract partial class DiskSpaceItem : ObservableObject
{
    public abstract string GroupNameKey { get; }
    public abstract string NameKey { get; }
    public abstract string DescriptionKey { get; }

    public string GroupName => Localizer.Get(GroupNameKey);
    public string Name => Localizer.Get(NameKey);
    public string Description => Localizer.Get(DescriptionKey);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    public partial DiskSpaceItemState State { get; protected set; }

    /// <summary>Bytes on the system drive this item accounts for; null until scanned.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial long? SizeBytes { get; protected set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    public partial string? ErrorMessage { get; protected set; }

    public bool IsBusy => State is DiskSpaceItemState.Scanning or DiskSpaceItemState.Working;

    public string SizeText =>
        State == DiskSpaceItemState.Scanning ? "…"
        : SizeBytes is { } bytes ? ByteSize.Format(bytes)
        : "—";

    public string StatusText => BuildStatusText();

    public bool HasStatusText => StatusText.Length > 0;

    protected virtual string BuildStatusText()
    {
        return State switch
        {
            DiskSpaceItemState.Scanning => Localizer.Get("DiskSpaceScanning"),
            DiskSpaceItemState.Failed => string.Format(
                Localizer.Get("DiskSpaceFailed"),
                ErrorMessage ?? ""
            ),
            _ => "",
        };
    }

    /// <summary>Re-measures the item. Safe to call repeatedly; no-op while busy.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
            return;

        State = DiskSpaceItemState.Scanning;
        ErrorMessage = null;
        try
        {
            await RefreshCoreAsync(cancellationToken);
            State = DiskSpaceItemState.Scanned;
        }
        catch (OperationCanceledException)
        {
            State = SizeBytes is null ? DiskSpaceItemState.NotScanned : DiskSpaceItemState.Scanned;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            State = DiskSpaceItemState.Failed;
        }
    }

    /// <summary>Measures and stores <see cref="SizeBytes" /> (and any subclass state). Called on the UI thread.</summary>
    protected abstract Task RefreshCoreAsync(CancellationToken cancellationToken);

    public virtual void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(GroupName));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatusText));
    }
}
