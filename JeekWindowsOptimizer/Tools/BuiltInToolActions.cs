using System.Diagnostics;

namespace JeekWindowsOptimizer;

internal static class BuiltInToolActions
{
    public static async Task<bool> Run(string action)
    {
        return action switch
        {
            "RestartExplorer" => await RestartExplorer(),
            "RefreshIconCache" => await RefreshIconCache(),
            _ => false,
        };
    }

    internal static async Task<bool> RestartExplorer()
    {
        await Task.Run(StopExplorer);
        await Task.Delay(500);
        return StartExplorer();
    }

    private static async Task<bool> RefreshIconCache()
    {
        await Task.Run(StopExplorer);
        await Task.Delay(500);

        FileSystemCleaner.DeleteFile(
            Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IconCache.db"
            )
        );

        var explorerCachePath = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\Explorer"
        );
        FileSystemCleaner.DeleteFiles(explorerCachePath, "iconcache_*.db");
        FileSystemCleaner.DeleteFiles(explorerCachePath, "thumbcache_*.db");

        return StartExplorer();
    }

    private static void StopExplorer()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var session = currentProcess.SessionId;
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            try
            {
                if (process.SessionId != session)
                    continue;
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
            var explorer = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var process = Process.Start(new ProcessStartInfo(explorer) { UseShellExecute = true });
            return process is not null;
        }
        catch
        {
            return false;
        }
    }
}
