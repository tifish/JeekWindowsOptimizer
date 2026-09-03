using System.Runtime.InteropServices;

namespace JeekWindowsOptimizer;

/// <summary>
///     Shell known folders (Desktop, Documents, ...): current path lookup and
///     redirection through <c>IKnownFolderManager::Redirect</c>, the same call
///     Explorer's Location tab uses. It moves the contents, updates the registry
///     redirection, and writes the desktop.ini so the new folder keeps its icon
///     and localized name.
/// </summary>
public static class KnownFolders
{
    public static readonly Guid Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    public static readonly Guid Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    public static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    public static readonly Guid Pictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    public static readonly Guid Music = new("4BD8D571-6D19-48D3-BE97-422220080E43");
    public static readonly Guid Videos = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");

    private const uint KF_FLAG_DONT_VERIFY = 0x00004000;

    // KF_REDIRECT_FLAGS (shobjidl_core.h). Note USER_EXCLUSIVE is 0x1 and CHECK_ONLY is
    // 0x10; mixing them up turns a "dry run" into a real redirect.
    private const uint KF_REDIRECT_COPY_CONTENTS = 0x00000200;
    private const uint KF_REDIRECT_DEL_SOURCE_CONTENTS = 0x00000400;

    public static string? GetPath(Guid folderId)
    {
        var hr = SHGetKnownFolderPath(ref folderId, KF_FLAG_DONT_VERIFY, IntPtr.Zero, out var pathPtr);
        if (hr != 0 || pathPtr == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUni(pathPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    /// <summary>
    ///     Redirects the folder to <paramref name="targetPath" />, moving its contents.
    ///     Runs on a dedicated STA thread because the move can take minutes. There is no
    ///     dry-run mode; callers validate with <see cref="ValidateRedirectTarget" /> first.
    ///     The call reports no progress and shows no copy dialog — see the DiskSpace
    ///     README for why driving the shell's own dialog is not an option here.
    /// </summary>
    public static Task<(bool Succeeded, string? Error)> Redirect(
        Guid folderId,
        string targetPath,
        IntPtr ownerWindow = default
    )
    {
        var completion = new TaskCompletionSource<(bool, string?)>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(RedirectCore(folderId, targetPath, ownerWindow));
            }
            catch (Exception ex)
            {
                completion.SetResult((false, ex.Message));
            }
        })
        {
            IsBackground = true,
            Name = "KnownFolderRedirect",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task;
    }

    /// <summary>
    ///     Local sanity checks before a redirect: the target must be an absolute path on
    ///     a ready NTFS drive, must not be the current location, must not sit inside the
    ///     current location, and must not already be another known folder.
    /// </summary>
    public static string? ValidateRedirectTarget(Guid folderId, string currentPath, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || !Path.IsPathRooted(targetPath))
            return "Target path must be absolute.";

        var root = Path.GetPathRoot(targetPath);
        if (string.IsNullOrEmpty(root))
            return "Target path has no drive.";

        try
        {
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return $"Drive {drive.Name} is not ready.";
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                return $"Drive {drive.Name} is {drive.DriveFormat}; known folders need NTFS.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        var normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        if (string.Equals(normalizedTarget, root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return "A drive root cannot be a known folder.";

        if (!string.IsNullOrEmpty(currentPath))
        {
            var normalizedCurrent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentPath));
            if (string.Equals(normalizedTarget, normalizedCurrent, StringComparison.OrdinalIgnoreCase))
                return "The folder is already there.";
            if (normalizedTarget.StartsWith(normalizedCurrent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "The target is inside the folder being moved.";
        }

        foreach (var otherId in new[] { Desktop, Documents, Downloads, Pictures, Music, Videos })
        {
            if (otherId == folderId)
                continue;
            var otherPath = GetPath(otherId);
            if (string.IsNullOrEmpty(otherPath))
                continue;
            var normalizedOther = Path.TrimEndingDirectorySeparator(Path.GetFullPath(otherPath));
            if (string.Equals(normalizedTarget, normalizedOther, StringComparison.OrdinalIgnoreCase))
                return $"The target is already used by another user folder ({otherPath}).";
        }

        return null;
    }

    private static (bool Succeeded, string? Error) RedirectCore(
        Guid folderId,
        string targetPath,
        IntPtr ownerWindow
    )
    {
        // Known subfolders move along with the parent, exactly like Explorer's Location
        // tab. KF_REDIRECT_EXCLUDE_ALL_KNOWN_SUBFOLDERS is not usable here: on Windows 11
        // Music/Pictures/... each have a "Local" twin (FOLDERID_LocalMusic etc.) that
        // resolves to the same directory, and excluding it fails the whole call with
        // "there is a folder in the same location that can't be redirected".
        // KF_REDIRECT_WITH_UI (0x20) is deliberately not set: it only governs conflict
        // and error prompts, never a progress dialog, and any shell UI raised from this
        // worker thread has nothing pumping messages for it.
        var flags = KF_REDIRECT_COPY_CONTENTS | KF_REDIRECT_DEL_SOURCE_CONTENTS;

        var manager = (IKnownFolderManager)new KnownFolderManagerClass();
        try
        {
            var hr = manager.Redirect(
                ref folderId,
                ownerWindow,
                flags,
                targetPath,
                0,
                IntPtr.Zero,
                out var errorPtr
            );

            string? error = null;
            if (errorPtr != IntPtr.Zero)
            {
                error = Marshal.PtrToStringUni(errorPtr);
                Marshal.FreeCoTaskMem(errorPtr);
            }

            if (hr == 0)
                return (true, null);

            return (false, string.IsNullOrWhiteSpace(error)
                ? Marshal.GetExceptionForHR(hr)?.Message ?? $"HRESULT 0x{hr:X8}"
                : error);
        }
        finally
        {
            Marshal.FinalReleaseComObject(manager);
        }
    }

    [DllImport("Shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        ref Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath
    );

    [ComImport]
    [Guid("4df0c730-df9d-4ae3-9153-aa6b82e9795a")]
    private class KnownFolderManagerClass;

    [ComImport]
    [Guid("8BE2D872-86AA-4d47-B776-32CCA40C7018")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKnownFolderManager
    {
        // Vtable order matters; only Redirect is used.
        void FolderIdFromCsidl(int nCsidl, out Guid pfid);
        void FolderIdToCsidl(ref Guid rfid, out int pnCsidl);
        void GetFolderIds(out IntPtr ppKFId, out uint pCount);
        void GetFolder(ref Guid rfid, out IntPtr ppkf);
        void GetFolderByName([MarshalAs(UnmanagedType.LPWStr)] string pszCanonicalName, out IntPtr ppkf);
        void RegisterFolder(ref Guid rfid, IntPtr pKFD);
        void UnregisterFolder(ref Guid rfid);
        void FindFolderFromPath([MarshalAs(UnmanagedType.LPWStr)] string pszPath, int mode, out IntPtr ppkf);
        void FindFolderFromIDList(IntPtr pidl, out IntPtr ppkf);

        [PreserveSig]
        int Redirect(
            ref Guid rfid,
            IntPtr hwnd,
            uint flags,
            [MarshalAs(UnmanagedType.LPWStr)] string pszTargetPath,
            uint cFolders,
            IntPtr pExclusion,
            out IntPtr ppszError
        );
    }
}
