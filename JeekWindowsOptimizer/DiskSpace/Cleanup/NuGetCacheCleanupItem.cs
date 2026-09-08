namespace JeekWindowsOptimizer;

public sealed class NuGetCacheCleanupItem : DiskSpaceCleanupItem
{
    private readonly bool _packages;
    private readonly NuGetCache _cache;
    private string? _path;

    public NuGetCacheCleanupItem(bool packages) : this(packages, new NuGetCache()) { }

    internal NuGetCacheCleanupItem(bool packages, NuGetCache cache)
    {
        _packages = packages;
        _cache = cache;
        IsChecked = false;
    }

    public override string GroupNameKey => DeveloperCacheCleanupItem.DeveloperGroup;
    protected override bool DefaultChecked => false;
    public override string NameKey => _packages ? "NuGetPackagesCleanupName" : "NuGetHttpCacheCleanupName";
    public override string DescriptionKey => _packages ? "NuGetPackagesCleanupDescription" : "NuGetHttpCacheCleanupDescription";
    private string Kind => _packages ? "global-packages" : "http-cache";

    protected override async Task<long> ScanCore(CancellationToken cancellationToken)
    {
        _path = await _cache.GetPathAsync(Kind, cancellationToken);
        if (_path is null || !IsOnSystemDrive(_path))
            return 0;
        NuGetCache.ValidatePath(_path, cancellationToken);
        return FileSystemCleaner.GetDirectorySize(_path, cancellationToken);
    }

    private static bool IsOnSystemDrive(string path) => string.Equals(
        Path.GetPathRoot(path), DiskSpaceItemManager.SystemDriveRoot, StringComparison.OrdinalIgnoreCase);

    protected override async Task<bool> CleanCore(CancellationToken cancellationToken)
    {
        if (_path is null || !IsOnSystemDrive(_path))
            return true;
        await _cache.ClearAsync(Kind, _path, cancellationToken);
        return FileSystemCleaner.GetDirectorySize(_path, cancellationToken) == 0;
    }
}
