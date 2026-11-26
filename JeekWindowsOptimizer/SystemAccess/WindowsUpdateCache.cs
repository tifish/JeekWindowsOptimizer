namespace JeekWindowsOptimizer;

/// <summary>
///     Windows Update download cache (SoftwareDistribution\Download). The update and
///     BITS services hold the files, so they are stopped around the delete and
///     started again only if they were running.
/// </summary>
public static class WindowsUpdateCache
{
    private static readonly string[] ServiceNames = ["wuauserv", "bits"];

    public static string DownloadPath =>
        Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"SoftwareDistribution\Download"
        );

    public static long GetSize(CancellationToken cancellationToken = default)
    {
        return FileSystemCleaner.GetDirectorySize(DownloadPath, cancellationToken);
    }

    public static Task<long> Clean(CancellationToken cancellationToken = default) =>
        ServiceCacheCleanup.CleanAsync(ServiceNames, [DownloadPath], cancellationToken);
}
