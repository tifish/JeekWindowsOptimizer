using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

public class PagingFileRelocationItem : DiskSpaceRelocationItem
{
    public override string NameKey => "PagingFileRelocationName";
    public override string DescriptionKey => "PagingFileRelocationDescription";

    public override bool RequiresReboot => true;

    public PagingFile.State? LastState { get; private set; }

    public override string DefaultLocationText => Localizer.Get("PagingFileDefaultLocation");

    /// <summary>Windows' default is "automatically manage paging file size for all drives".</summary>
    public override bool IsAtDefaultLocation => LastState?.AutomaticallyManaged ?? true;

    protected override async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var state = await Task.Run(PagingFile.GetState, cancellationToken);
        LastState = state;

        var systemRoot = DiskSpaceItemManager.SystemDriveRoot;
        var onSystemDrive = state.Usages.Where(u => DiskSpaceItemManager.IsOnSystemDrive(u.Path)).ToList();
        var configuredOnSystemDrive = state.Settings.Any(s =>
            DiskSpaceItemManager.IsOnSystemDrive(s.Path)
        );

        SizeBytes = onSystemDrive.Sum(u => u.AllocatedBytes);

        if (state.AutomaticallyManaged)
        {
            var paths = state.Usages.Count > 0
                ? string.Join(", ", state.Usages.Select(u => u.Path))
                : Path.Join(systemRoot, "pagefile.sys");
            CurrentLocation = $"{paths} ({Localizer.Get("PagingFileAutomatic")})";
            IsOnSystemDrive = true;
        }
        else if (state.Settings.Count == 0 && state.Usages.Count == 0)
        {
            CurrentLocation = Localizer.Get("PagingFileNone");
            IsOnSystemDrive = false;
        }
        else
        {
            var sources = state.Settings.Count > 0
                ? state.Settings.Select(s =>
                    s.IsSystemManaged
                        ? $"{s.Path} ({Localizer.Get("PagingFileSystemManaged")})"
                        : $"{s.Path} ({s.InitialSizeMb}-{s.MaximumSizeMb} MB)"
                )
                : state.Usages.Select(u => u.Path);
            CurrentLocation = string.Join(", ", sources);
            IsOnSystemDrive = configuredOnSystemDrive || onSystemDrive.Count > 0;
        }

        OnPropertyChanged(nameof(IsAtDefaultLocation));
        OnPropertyChanged(nameof(CanRestoreDefault));
    }

    /// <summary>
    ///     Only a drive that already holds the one and only system-managed paging file
    ///     is a no-op target; automatic management is never "current" because moving
    ///     turns it off.
    /// </summary>
    protected override bool IsCurrentDrive(DriveOption drive)
    {
        if (LastState is not { AutomaticallyManaged: false, Settings.Count: 1 } state)
            return false;

        var only = state.Settings[0];
        return only.IsSystemManaged && drive.IsSameDrive(only.Path);
    }

    public override string GetTargetPath(DriveOption drive)
    {
        return Path.Join(drive.Root, "pagefile.sys");
    }

    protected override async Task<(bool Succeeded, string? Error)> MoveCoreAsync(
        DriveOption drive,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await Task.Run(() => PagingFile.MoveTo(drive.Root), cancellationToken);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    protected override async Task<(bool Succeeded, string? Error)> RestoreDefaultCoreAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            await Task.Run(PagingFile.RestoreAutomatic, cancellationToken);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
