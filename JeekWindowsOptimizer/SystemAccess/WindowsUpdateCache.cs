using System.Diagnostics;
using JeekTools;

namespace JeekWindowsOptimizer;

/// <summary>
///     Windows Update download cache (SoftwareDistribution\Download). The update and
///     BITS services hold the files, so they are stopped around the delete and
///     started again only if they were running.
/// </summary>
public static class WindowsUpdateCache
{
    private static readonly string[] ServiceNames = ["wuauserv", "bits"];

    public static string DownloadPath =>
        Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"SoftwareDistribution\Download"
        );

    public static long GetSize(CancellationToken cancellationToken = default)
    {
        return FileSystemCleaner.GetDirectorySize(DownloadPath, cancellationToken);
    }

    /// <summary>Returns bytes freed; -1 when a service could not be restored.</summary>
    public static async Task<long> Clean(CancellationToken cancellationToken = default)
    {
        var servicesToRestore = new List<string>();
        foreach (var serviceName in ServiceNames)
        {
            if (await IsServiceRunning(serviceName))
            {
                servicesToRestore.Add(serviceName);
                await RunAndWait("sc.exe", $"stop {serviceName}");
            }
        }

        // Give the service control manager a moment to release file handles.
        if (servicesToRestore.Count > 0)
            await Task.Delay(1500, cancellationToken);

        var freed = FileSystemCleaner.DeleteDirectoryContents(DownloadPath, cancellationToken);

        var restored = true;
        foreach (var serviceName in servicesToRestore)
            restored &= await RunAndWait("sc.exe", $"start {serviceName}");

        return restored ? freed : -1;
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
}
