using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

public sealed class LcuCleanupItem : FixedDirectoryCleanupItem
{
    public override string NameKey => "LcuCleanupName";
    public override string DescriptionKey => "LcuCleanupDescription";
    protected override bool DefaultChecked => false;
    public bool NeedsReboot { get; private set; }
    public bool ServicingBusy { get; private set; }
    public override bool CanClean => !NeedsReboot && !ServicingBusy;
    private readonly Func<bool> _pending;
    private readonly Func<bool> _busy;
    private readonly Func<DateTime> _boot;

    public LcuCleanupItem() : this(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        WindowsServicingState.RebootPending, WindowsServicingState.Busy, () => WindowsServicingState.BootTimeUtc) { }
    internal LcuCleanupItem(string windowsDirectory, Func<bool> pending, Func<bool> busy, Func<DateTime> boot)
        : base(Path.Join(windowsDirectory, "servicing", "LCU")) { _pending = pending; _busy = busy; _boot = boot; }

    private bool ChangedSinceBoot()
    {
        if (!Directory.Exists(DirectoryPath) || !FileSystemCleaner.IsPlainDirectoryPath(DirectoryPath)) return false;
        var boot = _boot();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint };
        return new DirectoryInfo(DirectoryPath).EnumerateFileSystemInfos("*", options)
            .Any(entry => entry.CreationTimeUtc >= boot || entry.LastWriteTimeUtc >= boot);
    }

    protected override async Task<long> ScanCore(CancellationToken cancellationToken)
    {
        NeedsReboot = _pending() || ChangedSinceBoot();
        ServicingBusy = _busy();
        return await base.ScanCore(cancellationToken);
    }

    protected override void ValidateCleanup()
    {
        if (_pending() || ChangedSinceBoot()) throw new IOException(Localizer.Get("LcuNeedsReboot"));
        if (_busy()) throw new IOException(Localizer.Get("LcuServicingBusy"));
    }

    protected override Task<bool> CleanCore(CancellationToken cancellationToken)
    {
        ValidateCleanup();
        if (!FileSystemCleaner.IsPlainDirectoryPath(DirectoryPath)) throw new IOException("LCU path contains a reparse point.");
        if (!Directory.Exists(DirectoryPath)) return Task.FromResult(true);
        foreach (var entry in new DirectoryInfo(DirectoryPath).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Only servicing can start mid-cleanup. Re-testing timestamps here would both walk
            // the whole tree per entry and flag the directories this loop is emptying as changed.
            if (_busy()) throw new IOException(Localizer.Get("LcuServicingBusy"));
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            if (entry is DirectoryInfo) FileSystemCleaner.DeleteDirectory(entry.FullName, cancellationToken);
            else FileSystemCleaner.DeleteFile(entry.FullName);
        }
        return Task.FromResult(!Directory.EnumerateFileSystemEntries(DirectoryPath).Any());
    }

    protected override string BuildStatusText()
    {
        var status = base.BuildStatusText();
        if (status.Length > 0) return status;
        return NeedsReboot ? Localizer.Get("LcuNeedsReboot") : ServicingBusy ? Localizer.Get("LcuServicingBusy") : "";
    }
}
