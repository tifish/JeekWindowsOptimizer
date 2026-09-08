using System.Diagnostics;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

/// <summary>Only Chromium disk/code caches; never whole profiles or site storage.</summary>
public sealed class BrowserCacheCleanupItem : DiskSpaceCleanupItem
{
    private readonly string _browser;
    private readonly string _root;
    private readonly Func<bool> _isRunning;

    public BrowserCacheCleanupItem(string browser)
        : this(browser, Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            browser == "Edge" ? @"Microsoft\Edge\User Data" : @"Google\Chrome\User Data"),
            () => IsBrowserRunning(browser == "Edge" ? "msedge" : "chrome")) { }

    internal BrowserCacheCleanupItem(string browser, string root, Func<bool> isRunning)
    {
        _browser = browser;
        _root = root;
        _isRunning = isRunning;
    }

    public override string NameKey => _browser + "CacheCleanupName";
    public override string DescriptionKey => "BrowserCacheCleanupDescription";

    public IReadOnlyList<string> CachePaths => string.Equals(Path.GetPathRoot(_root),
        DiskSpaceItemManager.SystemDriveRoot, StringComparison.OrdinalIgnoreCase)
        ? FindCachePaths(_root) : [];

    internal static bool IsPlainPath(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (FileSystemCleaner.IsReparsePoint(directory.FullName))
                return false;
        return true;
    }

    private static IReadOnlyList<string> FindCachePaths(string root)
    {
        if (!Directory.Exists(root) || !IsPlainPath(root))
            return [];
        var result = new List<string>();
        foreach (var profile in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(profile);
            if (name != "Default" && name != "Guest Profile"
                && !(name.StartsWith("Profile ", StringComparison.Ordinal)
                    && int.TryParse(name.AsSpan(8), out _)))
                continue;
            foreach (var cache in new[] { "Cache", "Code Cache" })
            {
                var path = Path.Join(profile, cache);
                if (Directory.Exists(path) && IsPlainPath(path))
                    result.Add(path);
            }
        }
        return result;
    }

    private static bool IsBrowserRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    protected override Task<long> ScanCore(CancellationToken cancellationToken) =>
        Task.FromResult(CachePaths.Sum(path => FileSystemCleaner.GetDirectorySize(path, cancellationToken)));

    protected override Task<bool> CleanCore(CancellationToken cancellationToken)
    {
        if (_isRunning())
            throw new InvalidOperationException(string.Format(Localizer.Get("BrowserCacheCloseFirst"), _browser));
        var paths = CachePaths;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_isRunning())
                throw new InvalidOperationException(string.Format(Localizer.Get("BrowserCacheCloseFirst"), _browser));
            if (IsPlainPath(path))
                FileSystemCleaner.DeleteDirectoryContents(path, cancellationToken);
        }
        return Task.FromResult(paths.All(path => FileSystemCleaner.GetDirectorySize(path, cancellationToken) == 0));
    }
}
