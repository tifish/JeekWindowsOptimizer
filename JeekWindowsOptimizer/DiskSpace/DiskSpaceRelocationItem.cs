using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

/// <summary>A drive the user can move something onto.</summary>
public sealed class DriveOption(string root, long freeBytes, bool? isSsd)
{
    /// <summary>Drive root with trailing separator, e.g. <c>D:\</c>.</summary>
    public string Root { get; } = root;

    public string Letter => Root.TrimEnd('\\', '/');

    public long FreeBytes { get; } = freeBytes;

    /// <summary>Null when the seek-penalty query failed (USB bridges, some RAID controllers).</summary>
    public bool? IsSsd { get; } = isSsd;

    /// <summary>Shown so the user can weigh speed against space: an HDD makes Desktop or Downloads noticeably slower.</summary>
    public string TypeText =>
        Localizer.Get(IsSsd switch { true => "DriveTypeSsd", false => "DriveTypeHdd", null => "DriveTypeUnknown" });

    public string Label =>
        string.Format(Localizer.Get("DiskSpaceDriveLabel"), Letter, TypeText, ByteSize.Format(FreeBytes));

    public bool IsSameDrive(string? path)
    {
        var root = string.IsNullOrEmpty(path) ? null : Path.GetPathRoot(path);
        return string.Equals(root, Root, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => Label;
}

/// <summary>
///     Something that can live on any drive (user folder, paging file): scan reports
///     where it is and how big it is; move relocates it to another data drive;
///     restore puts it back where Windows keeps it by default (the way back to the
///     system drive). Checked items take part in the batch move. Reboot-bound moves
///     (paging file) keep showing the pre-reboot location.
/// </summary>
public abstract partial class DiskSpaceRelocationItem : DiskSpaceItem
{
    private IReadOnlyList<DriveOption> _allDrives = [];

    public override string GroupNameKey => "DiskSpaceRelocation";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentLocationText))]
    [NotifyPropertyChangedFor(nameof(HasCurrentLocation))]
    [NotifyPropertyChangedFor(nameof(IsAtDefaultLocation))]
    [NotifyPropertyChangedFor(nameof(CanRestoreDefault))]
    public partial string CurrentLocation { get; protected set; } = "";

    public bool HasCurrentLocation => CurrentLocation.Length > 0;

    /// <summary>Informational only; the location line already says where it is.</summary>
    [ObservableProperty]
    public partial bool IsOnSystemDrive { get; protected set; }

    /// <summary>Takes part in "move checked items". Off by default: moving user data is a bigger decision than deleting caches.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

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

    public bool ShowMoveControls => HasTargetDrives && State != DiskSpaceItemState.NotScanned;

    public bool ShowNoTargetDrive => !HasTargetDrives && State == DiskSpaceItemState.Scanned;

    private bool IsSettled =>
        State is DiskSpaceItemState.Scanned or DiskSpaceItemState.Failed or DiskSpaceItemState.Done;

    public bool CanMove => SelectedTargetDrive is not null && !IsBusy && IsSettled;

    /// <summary>Whether the row's checkbox is usable: there must be somewhere to move to.</summary>
    public bool CanCheck => HasTargetDrives && !IsBusy;

    /// <summary>Human-readable default location shown in the restore confirmation.</summary>
    public abstract string DefaultLocationText { get; }

    public abstract bool IsAtDefaultLocation { get; }

    public bool CanRestoreDefault => !IsAtDefaultLocation && !IsBusy && IsSettled && HasCurrentLocation;

    public string CurrentLocationText =>
        CurrentLocation.Length == 0
            ? ""
            : string.Format(Localizer.Get("DiskSpaceCurrentLocation"), CurrentLocation);

    public string TargetPreview =>
        SelectedTargetDrive is { } drive ? GetTargetPath(drive) : "";

    public void ToggleChecked()
    {
        if (CanCheck)
            IsChecked = !IsChecked;
    }

    /// <summary>
    ///     Remembers every NTFS drive and offers the data drives the item is not on now.
    ///     The system drive is never a move target: going back there is "restore
    ///     default", which lands in the user profile instead of a root-level folder.
    /// </summary>
    public void SetTargetDrives(IReadOnlyList<DriveOption> drives)
    {
        _allDrives = drives;
        RefreshTargetDrives();
    }

    private void RefreshTargetDrives()
    {
        var previous = SelectedTargetDrive?.Root;
        var offered = _allDrives
            .Where(d => !d.IsSameDrive(DiskSpaceItemManager.SystemDriveRoot) && !IsCurrentDrive(d))
            .ToList();

        TargetDrives.Clear();
        foreach (var drive in offered)
            TargetDrives.Add(drive);

        SelectedTargetDrive =
            TargetDrives.FirstOrDefault(d =>
                string.Equals(d.Root, previous, StringComparison.OrdinalIgnoreCase)
            )
            ?? TargetDrives.OrderByDescending(d => d.FreeBytes).FirstOrDefault();

        if (!HasTargetDrives)
            IsChecked = false;

        OnPropertyChanged(nameof(HasTargetDrives));
        OnPropertyChanged(nameof(ShowMoveControls));
        OnPropertyChanged(nameof(ShowNoTargetDrive));
        OnPropertyChanged(nameof(CanCheck));
    }

    /// <summary>True when moving to <paramref name="drive" /> would be a no-op.</summary>
    protected virtual bool IsCurrentDrive(DriveOption drive) => drive.IsSameDrive(CurrentLocation);

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

    private bool _lastOperationWasRestore;

    public Task<bool> MoveAsync(DriveOption drive, CancellationToken cancellationToken = default)
    {
        return RunOperationAsync(
            () => MoveCoreAsync(drive, cancellationToken),
            () => GetTargetPath(drive),
            isRestore: false
        );
    }

    /// <summary>Puts the item back where Windows keeps it by default.</summary>
    public Task<bool> RestoreDefaultAsync(CancellationToken cancellationToken = default)
    {
        return RunOperationAsync(
            () => RestoreDefaultCoreAsync(cancellationToken),
            () => DefaultLocationText,
            isRestore: true
        );
    }

    private async Task<bool> RunOperationAsync(
        Func<Task<(bool Succeeded, string? Error)>> operation,
        Func<string> describeDestination,
        bool isRestore
    )
    {
        _lastOperationWasRestore = isRestore;
        if (IsBusy)
            return false;

        State = DiskSpaceItemState.Working;
        ErrorMessage = null;
        ProgressText = "";

        (bool Succeeded, string? Error) result;
        try
        {
            result = await operation();
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

        MovedTo = describeDestination();
        IsChecked = false;

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

        RefreshTargetDrives();
        State = DiskSpaceItemState.Done;
        return true;
    }

    /// <summary>Called on the UI thread; dispatch blocking work to the thread pool inside.</summary>
    protected abstract Task<(bool Succeeded, string? Error)> MoveCoreAsync(
        DriveOption drive,
        CancellationToken cancellationToken
    );

    protected abstract Task<(bool Succeeded, string? Error)> RestoreDefaultCoreAsync(
        CancellationToken cancellationToken
    );

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // The drive list is handed over before the scan knows where the item is; once
        // the location is known, drop the drive it already sits on.
        if (e.PropertyName == nameof(CurrentLocation))
            RefreshTargetDrives();

        if (e.PropertyName == nameof(State))
        {
            OnPropertyChanged(nameof(CanMove));
            OnPropertyChanged(nameof(CanCheck));
            OnPropertyChanged(nameof(CanRestoreDefault));
            OnPropertyChanged(nameof(ShowMoveControls));
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
                Localizer.Get(
                    (_lastOperationWasRestore, RequiresReboot) switch
                    {
                        (true, true) => "DiskSpaceRestoredRebootRequired",
                        (true, false) => "DiskSpaceRestored",
                        (false, true) => "DiskSpaceMovedRebootRequired",
                        (false, false) => "DiskSpaceMoved",
                    }
                ),
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
        RefreshTargetDrives();
    }
}
