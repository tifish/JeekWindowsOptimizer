namespace JeekWindowsOptimizer;

/// <summary>One fixed allowlisted directory. Never follows links, including ancestor junctions.</summary>
public abstract class FixedDirectoryCleanupItem : DiskSpaceCleanupItem
{
    public string DirectoryPath { get; }
    protected FixedDirectoryCleanupItem(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        if (string.Equals(DirectoryPath.TrimEnd('\\'), Path.GetPathRoot(DirectoryPath)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A drive root is not a cache directory.", nameof(directory));
    }

    protected virtual void ValidateCleanup() { }

    internal static long Measure(string path, CancellationToken token)
    {
        if (!FileSystemCleaner.IsPlainDirectoryPath(path)) return 0;
        if (!Directory.Exists(path))
        {
            // Missing is empty; access denied must not be presented as a successful empty scan.
            try { File.GetAttributes(path); }
            catch (FileNotFoundException) { return 0; }
            catch (DirectoryNotFoundException) { return 0; }
        }
        long size = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
        { token.ThrowIfCancellationRequested(); size = checked(size + file.Length); }
        return size;
    }

    protected override Task<long> ScanCore(CancellationToken cancellationToken) =>
        Task.FromResult(Measure(DirectoryPath, cancellationToken));

    protected override Task<bool> CleanCore(CancellationToken cancellationToken)
    {
        ValidateCleanup();
        if (!FileSystemCleaner.IsPlainDirectoryPath(DirectoryPath))
            throw new IOException("Cleanup path contains a reparse point: " + DirectoryPath);
        FileSystemCleaner.DeleteDirectoryContents(DirectoryPath, cancellationToken);
        // Include zero-length locked files and inaccessible subdirectories in completion checks.
        return Task.FromResult(!Directory.Exists(DirectoryPath) || !Directory.EnumerateFileSystemEntries(DirectoryPath).Any());
    }
}
