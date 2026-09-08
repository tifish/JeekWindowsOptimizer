using System.Diagnostics;
using Microsoft.Win32;

namespace JeekWindowsOptimizer;

internal static class WindowsServicingState
{
    internal static DateTime BootTimeUtc => DateTime.UtcNow.AddMilliseconds(-Environment.TickCount64);

    internal static bool RebootPending()
    {
        foreach (var path in new[] {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootInProgress",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key is not null) return true;
        }
        using var session = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        if (session?.GetValue("PendingFileRenameOperations") is string[] pending && pending.Any(s => !string.IsNullOrEmpty(s))) return true;
        return File.Exists(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS", "pending.xml"));
    }

    internal static bool Busy()
    {
        foreach (var name in new[] { "TiWorker", "TrustedInstaller", "dism", "DismHost", "MoUsoCoreWorker", "setuphost" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length > 0) return true; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\PackagesPending");
        return key is not null;
    }
}
