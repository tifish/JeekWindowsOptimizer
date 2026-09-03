namespace JeekWindowsOptimizer;

/// <summary>
///     Size measurement and best-effort deletion for cache/temp style directories.
///     Reparse points (junctions, symlinks) are never followed, hidden and system
///     files are counted, and locked files are skipped silently.
/// </summary>
public static class FileSystemCleaner
{
    private static EnumerationOptions RecursiveOptions =>
        new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

    private static EnumerationOptions TopLevelOptions =>
        new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    public static long GetDirectorySize(
        string directoryPath,
        CancellationToken cancellationToken = default
    )
    {
        if (!Directory.Exists(directoryPath) || IsReparsePoint(directoryPath))
            return 0;

        long total = 0;
        try
        {
            foreach (
                var file in new DirectoryInfo(directoryPath).EnumerateFiles("*", RecursiveOptions)
            )
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    total += file.Length;
                }
                catch
                {
                    // File vanished or is inaccessible; skip it.
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Root became inaccessible; report what was counted.
        }

        return total;
    }

    public static long GetFilesSize(
        string directoryPath,
        string searchPattern,
        CancellationToken cancellationToken = default
    )
    {
        if (!Directory.Exists(directoryPath))
            return 0;

        long total = 0;
        try
        {
            foreach (
                var file in new DirectoryInfo(directoryPath).EnumerateFiles(
                    searchPattern,
                    TopLevelOptions
                )
            )
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    total += file.Length;
                }
                catch
                {
                    // Skip files that vanished mid-enumeration.
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Inaccessible directory.
        }

        return total;
    }

    public static long GetFileSize(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    ///     Deletes everything inside <paramref name="directoryPath" /> but keeps the
    ///     directory itself. Returns the number of bytes actually freed.
    /// </summary>
    public static long DeleteDirectoryContents(
        string directoryPath,
        CancellationToken cancellationToken = default
    )
    {
        if (!Directory.Exists(directoryPath) || IsReparsePoint(directoryPath))
            return 0;

        long freed = 0;
        try
        {
            foreach (
                var entry in new DirectoryInfo(directoryPath).EnumerateFileSystemInfos(
                    "*",
                    TopLevelOptions
                )
            )
            {
                cancellationToken.ThrowIfCancellationRequested();
                freed += entry switch
                {
                    DirectoryInfo dir => DeleteDirectory(dir.FullName, cancellationToken),
                    FileInfo file => DeleteFile(file),
                    _ => 0,
                };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Root became inaccessible.
        }

        return freed;
    }

    /// <summary>
    ///     Deletes a directory tree and returns the bytes freed. Locked files are left
    ///     behind, and the directory is only removed when it ends up empty.
    /// </summary>
    public static long DeleteDirectory(
        string directoryPath,
        CancellationToken cancellationToken = default
    )
    {
        if (!Directory.Exists(directoryPath))
            return 0;

        if (IsReparsePoint(directoryPath))
        {
            // Remove the link itself, never its target.
            try
            {
                Directory.Delete(directoryPath, false);
            }
            catch
            {
                // Leave it.
            }
            return 0;
        }

        var freed = DeleteDirectoryContents(directoryPath, cancellationToken);
        try
        {
            Directory.Delete(directoryPath, false);
        }
        catch
        {
            // Not empty (locked files) or in use; leave it.
        }

        return freed;
    }

    public static long DeleteFiles(
        string directoryPath,
        string searchPattern,
        CancellationToken cancellationToken = default
    )
    {
        if (!Directory.Exists(directoryPath))
            return 0;

        long freed = 0;
        try
        {
            foreach (
                var file in new DirectoryInfo(directoryPath).EnumerateFiles(
                    searchPattern,
                    TopLevelOptions
                )
            )
            {
                cancellationToken.ThrowIfCancellationRequested();
                freed += DeleteFile(file);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Inaccessible directory.
        }

        return freed;
    }

    public static long DeleteFile(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            return info.Exists ? DeleteFile(info) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static long DeleteFile(FileInfo file)
    {
        try
        {
            var length = file.Length;
            if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                file.Attributes &= ~FileAttributes.ReadOnly;
            file.Delete();
            return length;
        }
        catch
        {
            // Locked by a running process or access denied.
            return 0;
        }
    }
}
