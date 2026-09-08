using System.Diagnostics;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

public sealed class DeveloperCacheCleanupItem : DiskSpaceCleanupItem
{
    public const string DeveloperGroup = "DiskSpaceDeveloperCleanup";
    public override string GroupNameKey => DeveloperGroup;
    public override string NameKey => "Developer" + Kind + "CleanupName";
    public override string DescriptionKey => "Developer" + Kind + "CleanupDescription";
    protected override bool DefaultChecked => false;
    public string Kind { get; }
    public IReadOnlyList<string> CachePaths { get; private set; } = [];
    private readonly DeveloperCachePaths _paths;
    private readonly Func<bool> _busy;

    public DeveloperCacheCleanupItem(string kind) : this(kind, new DeveloperCachePaths(), () => IsToolRunning(kind)) { }
    internal DeveloperCacheCleanupItem(string kind, DeveloperCachePaths paths, Func<bool> busy)
    {
        if (!DeveloperCachePaths.Kinds.Contains(kind)) throw new ArgumentException("Unknown developer cache", nameof(kind));
        Kind = kind; _paths = paths; _busy = busy;
    }

    private static bool IsToolRunning(string kind)
    {
        string[] names = kind switch
        {
            "Pip" => ["pip", "python", "pythonw"],
            "Npm" or "Npx" or "Yarn" => ["node"],
            "Gradle" or "GradleDistributions" or "Maven" => ["java", "javaw"],
            "Cargo" => ["cargo", "rustc"],
            "GoBuild" => ["go"],
            "VsComponentModel" => ["devenv"],
            "VsCode" => ["Code", "Code - Insiders"],
            "PackageCache" => ["msiexec", "vs_installer", "vs_installershell", "setup"],
            _ => [],
        };
        foreach (var name in names)
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length > 0) return true; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return false;
    }

    private string[] Resolve() => _paths.Resolve(Kind).Select(Path.GetFullPath)
        .Where(DiskSpaceItemManager.IsOnSystemDrive).Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private void Validate(string path, CancellationToken token)
    {
        try { _paths.Validate(path, token); }
        catch (IOException) { throw new IOException(Localizer.Get("DeveloperCacheUnsafePath")); }
    }

    protected override Task<long> ScanCore(CancellationToken cancellationToken)
    {
        CachePaths = Resolve();
        long size = 0;
        foreach (var path in CachePaths)
        {
            Validate(path, cancellationToken);
            size = checked(size + FixedDirectoryCleanupItem.Measure(path, cancellationToken));
        }
        return Task.FromResult(size);
    }

    protected override Task<bool> CleanCore(CancellationToken cancellationToken)
    {
        if (!CachePaths.SequenceEqual(Resolve(), StringComparer.OrdinalIgnoreCase))
            throw new IOException(Localizer.Get("DeveloperCachePathChanged"));
        // Validate every target before deleting anything.
        foreach (var path in CachePaths) Validate(path, cancellationToken);
        var complete = true;
        foreach (var path in CachePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_busy()) throw new IOException(Localizer.Get("DeveloperCacheBusy"));
            Validate(path, cancellationToken);
            FileSystemCleaner.DeleteDirectoryContents(path, cancellationToken);
            complete &= !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any();
        }
        return Task.FromResult(complete);
    }

    protected override string BuildStatusText()
    {
        var status = base.BuildStatusText();
        if (status.Length > 0) return status;
        return State == DiskSpaceItemState.Scanned && CachePaths.Count > 0
            ? string.Join(Environment.NewLine, CachePaths) : "";
    }
}
