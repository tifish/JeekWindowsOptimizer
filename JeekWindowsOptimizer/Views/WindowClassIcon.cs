using System.Runtime.InteropServices;

namespace JeekWindowsOptimizer.Views;

// The app runs elevated, so UIPI drops the shell's WM_GETICON to our window and the taskbar falls back to the
// window class icon. Avalonia registers its class without one, which leaves the taskbar showing a generic icon.
internal static class WindowClassIcon
{
    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;
    private const uint WM_SETICON = 0x0080;

    public static void ApplyExeIcon(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || Environment.ProcessPath is not { } exePath)
            return;

        var large = new IntPtr[1];
        var small = new IntPtr[1];
        if (ExtractIconEx(exePath, 0, large, small, 1) == 0)
            return;

        // The icons live for the process lifetime as the class icons, so they are never destroyed.
        if (large[0] != IntPtr.Zero)
        {
            SetClassLongPtr(hwnd, GCLP_HICON, large[0]);
            SendMessage(hwnd, WM_SETICON, 1, large[0]);
        }

        if (small[0] != IntPtr.Zero)
        {
            SetClassLongPtr(hwnd, GCLP_HICONSM, small[0]);
            SendMessage(hwnd, WM_SETICON, 0, small[0]);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern IntPtr SetClassLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, nint wParam, IntPtr lParam);
}
