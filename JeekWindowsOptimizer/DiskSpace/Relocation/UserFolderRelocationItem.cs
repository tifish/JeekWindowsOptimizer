using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

/// <summary>
///     A shell known folder (Desktop, Documents, ...) moved to the root of another
///     drive (<c>D:\Documents</c>) through the shell's own redirect call, so
///     libraries, pins, and desktop.ini stay consistent. The root is chosen over a
///     per-user subtree because it is where users look for it, survives a Windows
///     reinstall, and does not depend on the account name; multi-user machines are
///     not a design target.
/// </summary>
public class UserFolderRelocationItem(
    Guid folderId,
    string defaultFolderName,
    string nameKey,
    string descriptionKey
) : DiskSpaceRelocationItem
{
    public Guid FolderId { get; } = folderId;

    public override string NameKey => nameKey;
    public override string DescriptionKey => descriptionKey;

    /// <summary>Owner window for any UI the shell raises during the move.</summary>
    public static IntPtr OwnerWindowHandle { get; set; }

    protected override async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var path = KnownFolders.GetPath(FolderId) ?? "";
        CurrentLocation = path;
        IsOnSystemDrive = DiskSpaceItemManager.IsOnSystemDrive(path);
        SizeBytes = path.Length == 0
            ? 0
            : await Task.Run(
                () => FileSystemCleaner.GetDirectorySize(path, cancellationToken),
                cancellationToken
            );
    }

    /// <summary>
    ///     Always the canonical English name (Documents, Downloads, ...): Explorer shows
    ///     the localized display name through desktop.ini, and a reinstalled Windows
    ///     recognizes the English name as the same kind of folder.
    /// </summary>
    public override string GetTargetPath(DriveOption drive)
    {
        return Path.Join(drive.Root, defaultFolderName);
    }

    /// <summary>True when the target already exists with content, which the shell merges into.</summary>
    public override bool TargetHasContent(DriveOption drive)
    {
        try
        {
            var target = GetTargetPath(drive);
            return Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any();
        }
        catch
        {
            return false;
        }
    }

    public override Task<(bool Succeeded, string? Error)> CheckAsync(
        DriveOption drive,
        CancellationToken cancellationToken = default
    )
    {
        var error = KnownFolders.ValidateRedirectTarget(FolderId, CurrentLocation, GetTargetPath(drive));
        return Task.FromResult((error is null, error));
    }

    protected override Task<(bool Succeeded, string? Error)> MoveCoreAsync(
        DriveOption drive,
        CancellationToken cancellationToken
    )
    {
        return RedirectToAsync(GetTargetPath(drive));
    }

    /// <summary>
    ///     Redirects to an explicit path. Used by the normal move and by the debug
    ///     surface (which also needs to move a folder back to the system drive).
    /// </summary>
    public async Task<(bool Succeeded, string? Error)> RedirectToAsync(string target)
    {
        if (KnownFolders.ValidateRedirectTarget(FolderId, CurrentLocation, target) is { } error)
            return (false, error);

        var createdTarget = false;
        try
        {
            if (!Directory.Exists(target))
            {
                Directory.CreateDirectory(target);
                createdTarget = true;
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        var redirect = KnownFolders.Redirect(FolderId, target, OwnerWindowHandle);
        await ReportProgressUntilDone(redirect, target, SizeBytes ?? 0);
        var result = await redirect;

        if (!result.Succeeded)
            CleanUpEmptyTarget(target, createdTarget);

        return result;
    }

    /// <summary>
    ///     The redirect call reports no progress, so the row would just say "moving" for
    ///     minutes. Progress is derived from the target drive's free space — one cheap
    ///     call per tick, no directory walk: the drive fills up as the copy proceeds, and
    ///     once it holds everything the remaining time is spent deleting the originals.
    /// </summary>
    private async Task ReportProgressUntilDone(Task redirect, string target, long expectedBytes)
    {
        if (expectedBytes <= 0)
            return;

        var targetDrive = TryGetDrive(target);
        if (targetDrive is null)
            return;

        var targetFreeAtStart = TryGetFreeBytes(targetDrive);
        if (targetFreeAtStart < 0)
            return;

        var poll = TimeSpan.FromSeconds(1);
        while (!redirect.IsCompleted)
        {
            await Task.WhenAny(redirect, Task.Delay(poll));
            if (redirect.IsCompleted)
                break;

            var copied = targetFreeAtStart - TryGetFreeBytes(targetDrive);
            if (copied < 0)
                continue;

            // No byte count for the delete phase: the source drive only reports the
            // reclaimed space once the deletes are committed, so it reads 0 throughout.
            ProgressText = copied < (long)(expectedBytes * 0.995)
                ? string.Format(
                    Localizer.Get("DiskSpaceMoveProgressCopying"),
                    ByteSize.Format(Math.Min(copied, expectedBytes)),
                    ByteSize.Format(expectedBytes)
                )
                : Localizer.Get("DiskSpaceMoveProgressDeleting");
        }

        ProgressText = "";
    }

    private static DriveInfo? TryGetDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root);
        }
        catch
        {
            return null;
        }
    }

    private static long TryGetFreeBytes(DriveInfo drive)
    {
        try
        {
            return drive.AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Leaves no empty stub behind when a move we started did not go through.</summary>
    private static void CleanUpEmptyTarget(string target, bool createdTarget)
    {
        if (!createdTarget)
            return;

        try
        {
            if (Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any())
                Directory.Delete(target);
        }
        catch
        {
            // Best effort only.
        }
    }
}
