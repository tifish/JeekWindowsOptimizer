using System.Diagnostics;
using Microsoft.Win32;

namespace JeekWindowsOptimizer;

/// <summary>
///     Drives the built-in Disk Cleanup (cleanmgr.exe) non-interactively: each
///     handler under VolumeCaches gets a StateFlags value for a private profile id,
///     then <c>cleanmgr /sagerun:id</c> processes exactly those handlers.
/// </summary>
public static class DiskCleanupTool
{
    private const string VolumeCachesKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";

    /// <summary>Profile id reserved for this app so user-defined sageset profiles stay untouched.</summary>
    private const int ProfileId = 4137;

    private static string StateFlagsName => $"StateFlags{ProfileId:0000}";

    /// <summary>Handlers that remove a previous Windows installation and its setup leftovers.</summary>
    public static readonly string[] PreviousInstallationHandlers =
    [
        "Previous Installations",
        "Temporary Setup Files",
        "Setup Log Files",
        "Windows Upgrade Log Files",
    ];

    public static bool HandlerExists(string handlerName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{VolumeCachesKeyPath}\{handlerName}");
        return key is not null;
    }

    /// <summary>Runs cleanmgr for the given handlers only. Returns false when nothing could be scheduled.</summary>
    public static async Task<bool> Run(
        IEnumerable<string> handlerNames,
        CancellationToken cancellationToken = default
    )
    {
        var scheduled = new List<string>();
        using (var root = Registry.LocalMachine.OpenSubKey(VolumeCachesKeyPath, writable: true))
        {
            if (root is null)
                return false;

            // Reset the whole profile so a stale flag from an earlier run cannot leak in.
            foreach (var name in root.GetSubKeyNames())
            {
                using var handler = root.OpenSubKey(name, writable: true);
                handler?.DeleteValue(StateFlagsName, throwOnMissingValue: false);
            }

            foreach (var name in handlerNames)
            {
                using var handler = root.OpenSubKey(name, writable: true);
                if (handler is null)
                    continue;
                handler.SetValue(StateFlagsName, 2, RegistryValueKind.DWord);
                scheduled.Add(name);
            }
        }

        if (scheduled.Count == 0)
            return false;

        try
        {
            using var process = Process.Start(
                new ProcessStartInfo("cleanmgr.exe", $"/sagerun:{ProfileId}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            );
            if (process is null)
                return false;

            await process.WaitForExitAsync(cancellationToken);
            return true;
        }
        finally
        {
            using var root = Registry.LocalMachine.OpenSubKey(VolumeCachesKeyPath, writable: true);
            if (root is not null)
            {
                foreach (var name in scheduled)
                {
                    using var handler = root.OpenSubKey(name, writable: true);
                    handler?.DeleteValue(StateFlagsName, throwOnMissingValue: false);
                }
            }
        }
    }
}
