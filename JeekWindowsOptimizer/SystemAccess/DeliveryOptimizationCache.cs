namespace JeekWindowsOptimizer;

/// <summary>
///     Delivery Optimization peer cache. Windows keeps it in one of two places
///     depending on build; both are checked. The DoSvc service owns the files, so
///     it is stopped around the delete and restarted if it was running.
/// </summary>
public static class DeliveryOptimizationCache
{
    private const string ServiceName = "DoSvc";

    public static IReadOnlyList<string> CachePaths
    {
        get
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return
            [
                Path.Join(windows, @"SoftwareDistribution\DeliveryOptimization"),
                Path.Join(
                    windows,
                    @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"
                ),
            ];
        }
    }

    public static long GetSize(CancellationToken cancellationToken = default)
    {
        return CachePaths.Sum(path => FileSystemCleaner.GetDirectorySize(path, cancellationToken));
    }

    public static Task<long> Clean(CancellationToken cancellationToken = default) =>
        ServiceCacheCleanup.CleanAsync([ServiceName], CachePaths, cancellationToken);
}
