using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AvBitmap = Avalonia.Media.Imaging.Bitmap;

namespace JeekWindowsOptimizer;

[SupportedOSPlatform("windows")]
internal static class ToolIconExtractor
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0;
    private const uint SHGFI_SMALLICON = 1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags
    );

    /// <summary>
    /// Shell + GDI 编码为 PNG 字节，可在后台线程调用（随后在 UI 线程用其构造 Avalonia <see cref="AvBitmap"/>）。
    /// </summary>
    internal static byte[]? TryEncodeToolIconPng(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            return null;

        try
        {
            using var png = EncodeShellIcon(fullPath) ?? EncodeAssociatedIcon(fullPath);

            return png?.ToArray();
        }
        catch
        {
            try
            {
                using var ms = EncodeAssociatedIcon(fullPath);

                return ms?.ToArray();
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 从文件 Shell 视图加载小图标（与列表等 UI 小尺寸更匹配）。应在 UI 线程调用。
    /// </summary>
    public static AvBitmap? TryLoadToolIcon(string fullPath)
    {
        var bytes = TryEncodeToolIconPng(fullPath);

        return bytes is null ? null : new AvBitmap(new MemoryStream(bytes));
    }

    private static MemoryStream? EncodeShellIcon(string fullPath)
    {
        var shfi = new SHFILEINFO
        {
            szDisplayName = new string('\0', 260),
            szTypeName = new string('\0', 80),
        };

        _ = SHGetFileInfo(
            fullPath,
            0,
            ref shfi,
            (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_SMALLICON
        );
        if (shfi.hIcon == 0)
            return null;

        using (var icon = Icon.FromHandle(shfi.hIcon))
        {
            using var bmp = icon.ToBitmap();
            var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            return ms;
        }
    }

    private static MemoryStream? EncodeAssociatedIcon(string fullPath)
    {
        using var icon = Icon.ExtractAssociatedIcon(fullPath);
        if (icon is null)
            return null;

        using var bmp = icon.ToBitmap();
        var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        return ms;
    }
}
