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
            return Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })
                is not null;
        }
        catch
        {
            return false;
        }
    }
}
