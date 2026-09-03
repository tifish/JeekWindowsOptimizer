namespace JeekWindowsOptimizer;

/// <summary>Windows\Temp plus the current user's temp directory.</summary>
public class TempFilesCleanupItem : DiskSpaceCleanupItem
{
    public override string NameKey => "TempFilesCleanupName";
    public override string DescriptionKey => "TempFilesCleanupDescription";

    public static IReadOnlyList<string> TempPaths
    {
        get
        {
            var paths = new List<string>
            {
                Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
            };

            var userTemp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
            if (!paths.Contains(userTemp, StringComparer.OrdinalIgnoreCase))
                paths.Add(userTemp);

            return paths;
        }
    }

    protected override Task<long> ScanCore(CancellationToken cancellationToken)
    {
        return Task.FromResult(
            TempPaths.Sum(path => FileSystemCleaner.GetDirectorySize(path, cancellationToken))
        );
    }

    protected override Task<bool> CleanCore(CancellationToken cancellationToken)
    {
        foreach (var path in TempPaths)
            FileSystemCleaner.DeleteDirectoryContents(path, cancellationToken);
        return Task.FromResult(true);
    }
}
