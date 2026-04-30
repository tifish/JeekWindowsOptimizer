using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using JeekTools;
using Microsoft.Extensions.Logging;
using AvBitmap = Avalonia.Media.Imaging.Bitmap;
using ZLogger;

namespace JeekWindowsOptimizer;

[SupportedOSPlatform("windows")]
internal static class ToolIconExtractor
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(ToolIconExtractor));

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string szFileName,
        int nIconIndex,
        nint[] phiconLarge,
        nint[] phiconSmall,
        uint nIcons
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);

    /// <summary>
    /// Shell + GDI 编码为 PNG 字节，可在后台线程调用（随后在 UI 线程用其构造 Avalonia <see cref="AvBitmap"/>）。
    /// </summary>
    internal static byte[]? TryEncodeToolIconPng(string iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
            return null;

        var expandedIconPath = Environment.ExpandEnvironmentVariables(iconPath);
        var resolvedIconPath = ResolveIconPath(expandedIconPath);
        if (string.IsNullOrWhiteSpace(resolvedIconPath))
        {
            Log.ZLogWarning($"Failed to resolve tool icon path: {iconPath}");
            return null;
        }

        try
        {
            using var png = TryParseIconResource(resolvedIconPath, out var filePath, out var index)
                ? EncodeIconResource(filePath, index) ?? EncodeShellIcon(filePath)
                : EncodeShellIcon(resolvedIconPath) ?? EncodeAssociatedIcon(resolvedIconPath);

            if (png is null)
                Log.ZLogWarning($"Failed to extract tool icon: {iconPath} -> {resolvedIconPath}");

            return png?.ToArray();
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to extract tool icon: {iconPath} -> {resolvedIconPath}");

            try
            {
                var associatedIconPath = TryParseIconResource(
                    resolvedIconPath,
                    out var filePath,
                    out _
                )
                    ? filePath
                    : resolvedIconPath;
                using var ms = EncodeAssociatedIcon(associatedIconPath);

                if (ms is null)
                    Log.ZLogWarning(
                        $"Failed to extract associated tool icon: {associatedIconPath}"
                    );

                return ms?.ToArray();
            }
            catch (Exception fallbackEx)
            {
                Log.ZLogWarning(
                    fallbackEx,
                    $"Failed to extract fallback associated tool icon: {iconPath}"
                );
                return null;
            }
        }
    }

    /// <summary>
    /// 从文件 Shell 视图加载小图标（与列表等 UI 小尺寸更匹配）。应在 UI 线程调用。
    /// </summary>
    public static AvBitmap? TryLoadToolIcon(string iconPath)
    {
        var bytes = TryEncodeToolIconPng(iconPath);

        return bytes is null ? null : new AvBitmap(new MemoryStream(bytes));
    }

    private static string? ResolveIconPath(string iconPath)
    {
        if (TryParseIconResource(iconPath, out var resourceFilePath, out var index))
        {
            var resolvedResourceFilePath = ResolveIconFilePath(resourceFilePath);
            return resolvedResourceFilePath is null ? null : $"{resolvedResourceFilePath},{index}";
        }

        return ResolveIconFilePath(iconPath);
    }

    private static string? ResolveIconFilePath(string filePath)
    {
        if (File.Exists(filePath))
            return filePath;

        if (Path.IsPathRooted(filePath))
            return null;

        var systemPath = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var systemFilePath = Path.Join(systemPath, filePath);
        if (File.Exists(systemFilePath))
            return systemFilePath;

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        ))
        {
            try
            {
                var pathFilePath = Path.Join(directory, filePath);
                if (File.Exists(pathFilePath))
                    return pathFilePath;
            }
            catch
            {
                // Invalid PATH entries should not prevent icon loading.
            }
        }

        return null;
    }

    private static bool TryParseIconResource(string iconPath, out string filePath, out int index)
    {
        var match = Regex.Match(iconPath, @"^(?<path>.+),(?<index>-?\d+)$");
        if (match.Success)
        {
            filePath = match.Groups["path"].Value.Trim().Trim('"');
            index = int.Parse(match.Groups["index"].Value);
            return true;
        }

        filePath = iconPath;
        index = 0;
        return false;
    }

    private static MemoryStream? EncodeShellIcon(string fullPath)
    {
        if (!File.Exists(fullPath))
            return null;

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

        try
        {
            using var icon = (Icon)Icon.FromHandle(shfi.hIcon).Clone();
            using var bmp = icon.ToBitmap();
            var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            return ms;
        }
        finally
        {
            DestroyIcon(shfi.hIcon);
        }
    }

    private static MemoryStream? EncodeIconResource(string filePath, int index)
    {
        if (!File.Exists(filePath))
            return null;

        var smallIcons = new nint[1];
        var largeIcons = new nint[1];

        if (ExtractIconEx(filePath, index, largeIcons, smallIcons, 1) == 0 && index < 0)
            _ = ExtractIconEx(filePath, -index, largeIcons, smallIcons, 1);

        var iconHandle = smallIcons[0] != 0 ? smallIcons[0] : largeIcons[0];
        if (iconHandle == 0)
        {
            Log.ZLogWarning($"Icon resource not found: {filePath},{index}");
            return null;
        }

        try
        {
            using var icon = (Icon)Icon.FromHandle(iconHandle).Clone();
            using var bmp = icon.ToBitmap();
            var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            return ms;
        }
        finally
        {
            foreach (var handle in smallIcons.Concat(largeIcons).Where(handle => handle != 0))
                DestroyIcon(handle);
        }
    }

    private static MemoryStream? EncodeAssociatedIcon(string fullPath)
    {
        if (!File.Exists(fullPath))
            return null;

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
