using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

public sealed class WslRelocationItem : DiskSpaceRelocationItem
{
    private readonly string _id;
    private readonly string _name;
    private readonly bool _docker;
    private readonly Func<WslDistribution> _read;
    private readonly Func<string[], CancellationToken, Task<(int ExitCode, string Output)>> _run;
    private readonly Func<CancellationToken, Task> _requireSupport;
    internal WslRelocationItem(WslDistribution distro, Func<WslDistribution>? read = null,
        Func<string[], CancellationToken, Task<(int ExitCode, string Output)>>? run = null,
        Func<CancellationToken, Task>? requireSupport = null)
    {
        _id = distro.Id;
        _name = distro.Name;
        _docker = _name.Equals("docker-desktop-data", StringComparison.OrdinalIgnoreCase);
        _read = read ?? (() => WslStorage.Discover().SingleOrDefault(d => d.Id == _id)
            ?? throw new IOException(Localizer.Get("WslMigrationMissing")));
        _run = run ?? WslStorage.RunAsync;
        _requireSupport = requireSupport ?? WslStorage.RequireMoveSupportAsync;
    }
    public override string NameKey => "WslRelocation:" + _id;
    public override string Name => (_docker ? "Docker Desktop" : "WSL") + " · " + _name;
    public override string DescriptionKey => _docker ? "DockerLegacyMigrationDescription" : "WslMigrationDescription";
    public override string MoveNotice => Localizer.Get(DescriptionKey);
    public override bool SupportsRestoreDefault => false;
    protected override bool AllowSystemDriveTarget => true;
    public override string DefaultLocationText => "";
    public override bool IsAtDefaultLocation => true;
    public override string GetTargetPath(DriveOption drive)
    {
        var readable = new string(_name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).Take(40).ToArray()).TrimEnd('.', ' ');
        return Path.Combine(drive.Root, "WSL", readable + "-" + _id.Trim('{', '}')[..8]);
    }

    private WslDistribution Read() => _read();

    protected override async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var data = await Task.Run(Read, cancellationToken);
        CurrentLocation = data.Location;
        IsOnSystemDrive = DiskSpaceItemManager.IsOnSystemDrive(CurrentLocation);
        if (data.Version != 2) throw new IOException(Localizer.Get("WslMigrationVersionRequired"));
        SizeBytes = await Task.Run(() => new FileInfo(Path.Combine(data.Location, data.DiskName)).Length, cancellationToken);
    }

    public override async Task<(bool Succeeded, string? Error)> CheckAsync(DriveOption drive, CancellationToken cancellationToken = default)
    {
        try { await ValidateAsync(GetTargetPath(drive), cancellationToken); return (true, null); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private async Task ValidateAsync(string target, CancellationToken ct)
    {
        var data = Read();
        if (data.Version != 2) throw new IOException(Localizer.Get("WslMigrationVersionRequired"));
        if (!data.Location.Equals(CurrentLocation, StringComparison.OrdinalIgnoreCase))
            throw new IOException(Localizer.Get("DiskSpaceQueuedSourceChanged"));
        WslStorage.ValidateTarget(data.Location, target, new FileInfo(Path.Combine(data.Location, data.DiskName)).Length);
        if (_docker) WslStorage.RequireDockerStopped();
        await _requireSupport(ct);
    }

    protected override async Task<(bool Succeeded, string? Error)> MoveCoreAsync(DriveOption drive, CancellationToken cancellationToken)
    {
        var target = GetTargetPath(drive);
        await ValidateAsync(target, cancellationToken);
        var before = Read();
        ProgressText = Localizer.Get("WslMigrationMoving");
        cancellationToken.ThrowIfCancellationRequested();
        var stop = await _run(["--terminate", before.Name], CancellationToken.None);
        if (stop.ExitCode != 0) return (false, stop.Output);
        var stoppedSize = new FileInfo(Path.Combine(before.Location, before.DiskName)).Length;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var move = await _run(["--manage", before.Name, "--move", target], CancellationToken.None);
        var after = Read();
        CurrentLocation = after.Location;
        if (move.ExitCode != 0) return (false, move.Output);
        if (!after.Location.Equals(WslStorage.Normalize(target), StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(target, after.DiskName))
            || new FileInfo(Path.Combine(target, after.DiskName)).Length != stoppedSize)
            return (false, Localizer.Get("VirtualDiskMigrationVerificationFailed"));
        // Do not start the distribution: init/systemd may start the user's services and containers.
        return (true, null);
    }

    protected override Task<(bool Succeeded, string? Error)> RestoreDefaultCoreAsync(CancellationToken cancellationToken)
        => Task.FromResult<(bool, string?)>((false, Localizer.Get("VirtualDiskMigrationInvalidPath")));
}
