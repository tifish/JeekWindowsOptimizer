using JeekTools;

namespace JeekWindowsOptimizer;

/// <summary>
///     Builds the Disk Space tab's item list and answers drive questions. Items are
///     hand-written classes because each one talks to a different subsystem; there
///     is no data-driven variant yet.
/// </summary>
public static class DiskSpaceItemManager
{
    public static string SystemDriveRoot =>
        Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
        ?? @"C:\";

    /// <summary>Bare system drive letter ("C") for user-facing text such as the tab title.</summary>
    public static string SystemDriveLetter => SystemDriveRoot.TrimEnd('\\', ':');

    /// <summary>Formats a localized template that may contain a {0} placeholder for the system drive letter.</summary>
    public static string FormatWithSystemDrive(string template) =>
        template.Contains("{0}") ? string.Format(template, SystemDriveLetter) : template;

    public static List<DiskSpaceItem> CreateItems()
    {
        return
        [
            new RecycleBinCleanupItem(),
            new TempFilesCleanupItem(),
            new WindowsUpdateCacheCleanupItem(),
            new DeliveryOptimizationCacheCleanupItem(),
            new CrashDumpsCleanupItem(),
            new WindowsLogsCleanupItem(),
            new PreviousInstallationCleanupItem(),
            new ComponentStoreCleanupItem(),
            new PagingFileRelocationItem(),
            new UserFolderRelocationItem(
                KnownFolders.Desktop,
                "Desktop",
                "DesktopRelocationName",
                "DesktopRelocationDescription"
            ),
            new UserFolderRelocationItem(
                KnownFolders.Documents,
                "Documents",
                "DocumentsRelocationName",
                "DocumentsRelocationDescription"
            ),
            new UserFolderRelocationItem(
                KnownFolders.Downloads,
                "Downloads",
                "DownloadsRelocationName",
                "DownloadsRelocationDescription"
            ),
            new UserFolderRelocationItem(
                KnownFolders.Pictures,
                "Pictures",
                "PicturesRelocationName",
                "PicturesRelocationDescription"
            ),
            new UserFolderRelocationItem(
                KnownFolders.Music,
                "Music",
                "MusicRelocationName",
                "MusicRelocationDescription"
            ),
            new UserFolderRelocationItem(
                KnownFolders.Videos,
                "Videos",
                "VideosRelocationName",
                "VideosRelocationDescription"
            ),
        ];
    }

    public static bool IsOnSystemDrive(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var root = Path.GetPathRoot(path);
        return string.Equals(root, SystemDriveRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Ready fixed NTFS drives, system drive included, largest free space first.
    ///     Each relocation item drops the drive it currently sits on.
    /// </summary>
    public static List<DriveOption> GetTargetDrives()
    {
        var drives = new List<DriveOption>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    continue;
                if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool? isSsd = drive.TryIsSSD(out var ssd) ? ssd : null;
                drives.Add(new DriveOption(drive.Name, drive.AvailableFreeSpace, isSsd));
            }
            catch
            {
                // Drive vanished or is not accessible.
            }
        }

        return drives.OrderByDescending(d => d.FreeBytes).ToList();
    }

    public readonly record struct DriveUsage(string Letter, long TotalBytes, long FreeBytes)
    {
        public long UsedBytes => TotalBytes - FreeBytes;
        public double UsedPercent => TotalBytes > 0 ? UsedBytes * 100d / TotalBytes : 0;
    }

    public static DriveUsage? GetSystemDriveUsage()
    {
        try
        {
            var drive = new DriveInfo(SystemDriveRoot);
            return new DriveUsage(
                drive.Name.TrimEnd('\\', ':'),
                drive.TotalSize,
                drive.AvailableFreeSpace
            );
        }
        catch
        {
            return null;
        }
    }
}
