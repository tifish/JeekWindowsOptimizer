using System.Runtime.InteropServices;

namespace JeekWindowsOptimizer;

/// <summary>Recycle Bin size query and emptying through the shell API.</summary>
public static class RecycleBin
{
    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    // x64 layout: DWORD cbSize + padding, then two 8-byte integers (24 bytes).
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    /// <summary>Total size of recycled items; all drives when <paramref name="rootPath" /> is null.</summary>
    public static long GetSize(string? rootPath = null)
    {
        var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(rootPath, ref info) == 0 ? info.i64Size : 0;
    }

    public static long GetItemCount(string? rootPath = null)
    {
        var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(rootPath, ref info) == 0 ? info.i64NumItems : 0;
    }

    /// <summary>Empties the bin silently; all drives when <paramref name="rootPath" /> is null.</summary>
    public static bool Empty(string? rootPath = null)
    {
        var result = SHEmptyRecycleBin(
            IntPtr.Zero,
            rootPath,
            SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND
        );
        // S_OK, or E_UNEXPECTED when the bin is already empty.
        return result == 0 || result == unchecked((int)0x8000FFFF);
    }

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}
