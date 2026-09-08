using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

public sealed class DockerRelocationItem : DiskSpaceRelocationItem
{
    private string? _anchor;
    public override string NameKey => "DockerMigrationName";
    public override string DescriptionKey => "DockerMigrationDescription";
    public override string MoveNotice => Localizer.Get(DescriptionKey);
    protected override bool AllowSystemDriveTarget => true;
    public override string DefaultLocationText => _anchor ?? "";
    public override bool IsAtDefaultLocation => _anchor is null || CurrentLocation.Equals(_anchor, StringComparison.OrdinalIgnoreCase);
    public override string GetTargetPath(DriveOption drive) => Path.Combine(drive.Root, "DockerData", "disk");

    protected override async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var result = await Task.Run(() =>
        {
            var anchor = DockerDiskStorage.Discover();
            if (anchor is not null && !Directory.Exists(anchor) && Directory.Exists(anchor + ".jeek-backup"))
                throw new IOException(string.Format(Localizer.Get("DockerMigrationRecoveryRequired"), anchor + ".jeek-backup"));
            var source = anchor is null ? "" : DockerDiskStorage.Resolve(anchor);
            var size = anchor is null ? 0 : DockerDiskStorage.Measure(source);
            return (anchor, source, size);
        }, cancellationToken);
        _anchor = result.anchor;
        CurrentLocation = result.source;
        IsOnSystemDrive = DiskSpaceItemManager.IsOnSystemDrive(CurrentLocation);
        SizeBytes = result.size;
        OnPropertyChanged(nameof(DefaultLocationText));
        OnPropertyChanged(nameof(IsAtDefaultLocation));
        OnPropertyChanged(nameof(CanRestoreDefault));
    }

    protected override string BuildStatusText() => State == DiskSpaceItemState.Scanned && !HasCurrentLocation
        ? Localizer.Get("DockerMigrationMissing") : base.BuildStatusText();

    public override Task<(bool Succeeded, string? Error)> CheckAsync(DriveOption drive, CancellationToken cancellationToken = default)
        => Task.Run<(bool, string?)>(() =>
        {
            try
            {
                WslStorage.RequireDockerStopped();
                DockerDiskStorage.Validate(_anchor ?? throw new IOException(Localizer.Get("DockerMigrationMissing")),
                    CurrentLocation, GetTargetPath(drive), false);
                return (true, null);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }, cancellationToken);

    protected override Task<(bool Succeeded, string? Error)> MoveCoreAsync(DriveOption drive, CancellationToken cancellationToken)
        => RunAsync(GetTargetPath(drive), false, cancellationToken);

    protected override Task<(bool Succeeded, string? Error)> RestoreDefaultCoreAsync(CancellationToken cancellationToken)
        => RunAsync(DefaultLocationText, true, cancellationToken);

    private async Task<(bool Succeeded, string? Error)> RunAsync(string target, bool restore, CancellationToken ct)
    {
        var anchor = _anchor ?? throw new IOException(Localizer.Get("DockerMigrationMissing"));
        var source = CurrentLocation;
        var progress = new Progress<string>(text => ProgressText = text);
        try
        {
            await Task.Run(() => DockerDiskStorage.MoveAsync(anchor, source, target, restore,
                WslStorage.RequireDockerStopped, progress, ct), ct);
            return (true, null);
        }
        finally { CurrentLocation = DockerDiskStorage.Resolve(anchor); }
    }
}
