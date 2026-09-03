using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

/// <summary>A drive the user can move something onto.</summary>
public sealed class DriveOption(string root, long freeBytes)
{
    /// <summary>Drive root with trailing separator, e.g. <c>D:\</c>.</summary>
    public string Root { get; } = root;

    public string Letter => Root.TrimEnd('\\', '/');

    public long FreeBytes { get; } = freeBytes;

    public string Label =>
        string.Format(Localizer.Get("DiskSpaceDriveLabel"), Letter, ByteSize.Format(FreeBytes));

    public override string ToString() => Label;
}

/// <summary>
///     Something that lives on the system drive and can be moved to another drive:
///     scan reports where it is and how big it is, move relocates it. Reboot-bound
///     moves (paging file) keep their Done state instead of re-scanning.
/// </summary>
public abstract partial class DiskSpaceRelocationItem : DiskSpaceItem
{
    public override string GroupNameKey => "DiskSpaceRelocation";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentLocationText))]
    [NotifyPropertyChangedFor(nameof(HasCurrentLocation))]
    public partial string CurrentLocation { get; protected set; } = "";

    public bool HasCurrentLocation => CurrentLocation.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMove))]
    [NotifyPropertyChangedFor(nameof(ShowMoveControls))]
    [NotifyPropertyChangedFor(nameof(ShowNotOnSystemDrive))]
    public partial bool IsOnSystemDrive { get; protected set; }

    public ObservableCollection<DriveOption> TargetDrives { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMove))]
    [NotifyPropertyChangedFor(nameof(TargetPreview))]
    public partial DriveOption? SelectedTargetDrive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    public partial string MovedTo { get; private set; } = "";

    /// <summary>Live progress line shown while <see cref="State" /> is Working; empty when unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    public partial string ProgressText { get; protected set; } = "";

    public virtual bool RequiresReboot => false;

    public bool HasTargetDrives => TargetDrives.Count > 0;

    public bool ShowMoveControls =>
        IsOnSystemDrive && HasTargetDrives && State != DiskSpaceItemState.Done;

    public bool ShowNotOnSystemDrive =>
        !IsOnSystemDrive && State is DiskSpaceItemState.Scanned or DiskSpaceItemState.Done;

    public bool ShowNoTargetDrive =>
        IsOnSystemDrive && !HasTargetDrives && State == DiskSpaceItemState.Scanned;

    public bool CanMove =>
        IsOnSystemDrive
        && SelectedTargetDrive is not null
        && !IsBusy
        && State is DiskSpaceItemState.Scanned or DiskSpaceItemState.Failed;

    public string CurrentLocationText =>
        CurrentLocation.Length == 0
            ? ""
            : string.Format(Localizer.Get("DiskSpaceCurrentLocation"), CurrentLocation);

    public string TargetPreview =>
        SelectedTargetDrive is { } drive ? GetTargetPath(drive) : "";

    public void SetTargetDrives(IReadOnlyList<DriveOption> drives)
    {
        var previous = SelectedTargetDrive?.Root;
        TargetDrives.Clear();
        foreach (var drive in drives)
            TargetDrives.Add(drive);

        SelectedTargetDrive =
            TargetDrives.FirstOrDefault(d =>
                string.Equals(d.Root, previous, StringComparison.OrdinalIgnoreCase)
            )
            ?? TargetDrives.OrderByDescending(d => d.FreeBytes).FirstOrDefault();

        OnPropertyChanged(nameof(HasTargetDrives));
        OnPropertyChanged(nameof(ShowMoveControls));
        OnPropertyChanged(nameof(ShowNoTargetDrive));
    }

    /// <summary>Where the item would end up on <paramref name="drive" />.</summary>
    public abstract string GetTargetPath(DriveOption drive);

    /// <summary>Whether the target already holds files the move would merge with.</summary>
    public virtual bool TargetHasContent(DriveOption drive) => false;

    /// <summary>Validates the move without changing anything.</summary>
    public virtual Task<(bool Succeeded, string? Error)> CheckAsync(
        DriveOption drive,
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult<(bool, string?)>((true, null));
    }

    public async Task<bool> MoveAsync(DriveOption drive, CancellationToken cancellationToken = default)
    {
        if (IsBusy)
            return false;

        State = DiskSpaceItemState.Working;
        ErrorMessage = null;
        ProgressText = "";

        (bool Succeeded, string? Error) result;
        try
        {
            result = await MoveCoreAsync(drive, cancellationToken);
        }
        catch (Exception ex)
        {
            result = (false, ex.Message);
        }
        finally
        {
            ProgressText = "";
        }

        if (!result.Succeeded)
        {
            ErrorMessage = result.Error ?? "";
            State = DiskSpaceItemState.Failed;
            return false;
        }

        MovedTo = GetTargetPath(drive);

        if (!RequiresReboot)
        {
            try
            {
                await RefreshCoreAsync(CancellationToken.None);
            }
            catch
            {
                // The move itself succeeded; a failed re-scan only affects the size shown.
            }
        }

        State = DiskSpaceItemState.Done;
        return true;
    }

    /// <summary>Called on the UI thread; dispatch blocking work to the thread pool inside.</summary>
    protected abstract Task<(bool Succeeded, string? Error)> MoveCoreAsync(
        DriveOption drive,
        CancellationToken cancellationToken
    );

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(State))
        {
            OnPropertyChanged(nameof(CanMove));
            OnPropertyChanged(nameof(ShowMoveControls));
            OnPropertyChanged(nameof(ShowNotOnSystemDrive));
            OnPropertyChanged(nameof(ShowNoTargetDrive));
        }
    }

    protected override string BuildStatusText()
    {
        return State switch
        {
            DiskSpaceItemState.Working => ProgressText.Length > 0
                ? ProgressText
                : Localizer.Get("DiskSpaceMoving"),
            DiskSpaceItemState.Done => string.Format(
                Localizer.Get(RequiresReboot ? "DiskSpaceMovedRebootRequired" : "DiskSpaceMoved"),
                MovedTo
            ),
            _ => base.BuildStatusText(),
        };
    }

    public override void NotifyLanguageChanged()
    {
        base.NotifyLanguageChanged();
        OnPropertyChanged(nameof(CurrentLocationText));

        // DriveOption.Label reads the localizer on access; rebuilding the list makes
        // the ComboBox re-read it.
        SetTargetDrives([.. TargetDrives]);
    }
}
