using System.Runtime.InteropServices;
using System.Text;

namespace JeekWindowsOptimizer;

/// <summary>Reads the target of a Windows shortcut without launching it.</summary>
public static class ShellLinkResolver
{
    // CLSID_ShellLink.
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

    // IPersistFile::Load open mode: read-only, deny nothing.
    private const uint StgmRead = 0;

    // SLGP_RAWPATH keeps environment variables and the MSI/darwin advertised-shortcut
    // indirection intact, and returns the stored text even when the target's drive is
    // currently missing.
    private const uint SlgpRawPath = 0x4;

    private const int MaxPath = 260;
    private const int MaxArguments = 1024;

    /// <summary>
    ///     Resolves a .lnk file's target path and arguments. Returns false when the file is not a
    ///     shortcut or cannot be read.
    /// </summary>
    /// <param name="linkPath">Full path of the .lnk file to read.</param>
    /// <param name="targetPath">The raw, unexpanded target path, or an empty string.</param>
    /// <param name="arguments">The command line arguments, or an empty string.</param>
    /// <returns>True when at least one of <paramref name="targetPath" /> and <paramref name="arguments" /> was read.</returns>
    public static bool TryResolve(string linkPath, out string targetPath, out string arguments)
    {
        targetPath = "";
        arguments = "";

        if (string.IsNullOrWhiteSpace(linkPath) || !File.Exists(linkPath))
            return false;

        object? shellLinkObject = null;
        IShellLinkW? shellLink = null;
        IPersistFile? persistFile = null;

        try
        {
            var shellLinkType = Type.GetTypeFromCLSID(ShellLinkClsid);
            if (shellLinkType == null)
                return false;

            shellLinkObject = Activator.CreateInstance(shellLinkType);
            if (shellLinkObject == null)
                return false;

            shellLink = (IShellLinkW)shellLinkObject;
            persistFile = (IPersistFile)shellLinkObject;

            persistFile.Load(linkPath, StgmRead);

            var pathBuffer = new StringBuilder(MaxPath);
            shellLink.GetPath(pathBuffer, pathBuffer.Capacity, IntPtr.Zero, SlgpRawPath);
            var resolvedPath = pathBuffer.ToString();

            // An advertised or pure-PIDL shortcut has no path; its arguments are still useful.
            var argumentsBuffer = new StringBuilder(MaxArguments);
            shellLink.GetArguments(argumentsBuffer, argumentsBuffer.Capacity);
            var resolvedArguments = argumentsBuffer.ToString();

            if (resolvedPath.Length == 0 && resolvedArguments.Length == 0)
                return false;

            targetPath = resolvedPath;
            arguments = resolvedArguments;
            return true;
        }
        catch (COMException)
        {
            targetPath = "";
            arguments = "";
            return false;
        }
        catch (Exception)
        {
            targetPath = "";
            arguments = "";
            return false;
        }
        finally
        {
            // The three references are views of one runtime callable wrapper, so only the first
            // release does any work; the rest are swallowed by their own guard.
            Release(persistFile);
            Release(shellLink);
            Release(shellLinkObject);
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject == null)
            return;

        try
        {
            Marshal.FinalReleaseComObject(comObject);
        }
        catch (Exception)
        {
            // Already released, or never a runtime callable wrapper: nothing left to free.
        }
    }

    /// <remarks>
    ///     The full vtable has to be declared in order: the members that are never called are
    ///     placeholders that keep the used ones in their correct slots.
    /// </remarks>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <remarks>IPersistFile derives from IPersist, so GetClassID occupies the first slot.</remarks>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
