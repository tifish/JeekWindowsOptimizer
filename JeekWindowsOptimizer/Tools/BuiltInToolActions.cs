using System.Diagnostics;
using System.Runtime.InteropServices;
using JeekTools;

namespace JeekWindowsOptimizer;

internal static class BuiltInToolActions
{
    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    public static async Task<bool> Run(string action)
    {
        return action switch
        {
            "RestartExplorer" => await RestartExplorer(),
            "RefreshIconCache" => await RefreshIconCache(),
            "EmptyRecycleBin" => EmptyRecycleBin(),
            "CleanWindowsTemp" => CleanWindowsTemp(),
            "CleanWindowsUpdateCache" => await CleanWindowsUpdateCache(),
            _ => false,
        };
    }

    private static async Task<bool> RestartExplorer()
    {
        StopExplorer();
        await Task.Delay(500);
        return StartExplorer();
    }

    private static async Task<bool> RefreshIconCache()
    {
        StopExplorer();
        await Task.Delay(500);

        DeleteFileIfExists(Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IconCache.db"
        ));

        var explorerCachePath = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\Explorer"
        );
        DeleteFiles(explorerCachePath, "iconcache_*.db");
        DeleteFiles(explorerCachePath, "thumbcache_*.db");

        return StartExplorer();
    }

    private static bool EmptyRecycleBin()
    {
        return SHEmptyRecycleBin(
            IntPtr.Zero,
            null,
            SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND
        ) == 0;
    }

    private static bool CleanWindowsTemp()
    {
        var tempPath = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Temp"
        );
        DeleteDirectoryContents(tempPath);
        return Directory.Exists(tempPath);
    }

    private static async Task<bool> CleanWindowsUpdateCache()
    {
        var servicesToRestore = new List<string>();

        foreach (var serviceName in new[] { "wuauserv", "bits" })
        {
            if (await IsServiceRunning(serviceName))
            {
                servicesToRestore.Add(serviceName);
                await RunAndWait("sc.exe", $"stop {serviceName}");
            }
        }

        var downloadPath = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"SoftwareDistribution\Download"
        );
        DeleteDirectoryContents(downloadPath);

        var succeeded = true;
        foreach (var serviceName in servicesToRestore)
            succeeded &= await RunAndWait("sc.exe", $"start {serviceName}");

        return succeeded;
    }

    private static void StopExplorer()
    {
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            try
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            catch
            {
                // Explorer may already be exiting or protected by the current session state.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static bool StartExplorer()
    {
        try
        {
            return Process.Start(
                new ProcessStartInfo("explorer.exe") { UseShellExecute = true }
            ) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteFiles(string directoryPath, string searchPattern)
    {
        if (!Directory.Exists(directoryPath))
            return;

        foreach (var filePath in Directory.EnumerateFiles(directoryPath, searchPattern))
            DeleteFileIfExists(filePath);
    }

    private static void DeleteFileIfExists(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch
        {
            // Cache and temp files can be locked by running processes.
        }
    }

    private static void DeleteDirectoryContents(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return;

        foreach (var filePath in Directory.EnumerateFiles(directoryPath))
            DeleteFileIfExists(filePath);

        foreach (var childDirectoryPath in Directory.EnumerateDirectories(directoryPath))
        {
            try
            {
                Directory.Delete(childDirectoryPath, true);
            }
            catch
            {
                // Some temp/cache folders are expected to be locked.
            }
        }
    }

    private static async Task<bool> IsServiceRunning(string serviceName)
    {
        var output = await Executor.RunWithOutput("sc.exe", $"query {serviceName}");
        return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> RunAndWait(string fileName, string arguments)
    {
        using var process = Process.Start(
            new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        );
        if (process is null)
            return false;

        await process.WaitForExitAsync();
        return process.ExitCode == 0;
    }

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}
