using System.Diagnostics;

namespace JeekWindowsOptimizer;

/// <summary>
///     Delivery Optimization peer cache. Windows keeps it in one of two places
///     depending on build; both are checked. The DoSvc service owns the files, so
///     it is stopped around the delete and restarted if it was running.
/// </summary>
public static class DeliveryOptimizationCache
{
    private const string ServiceName = "DoSvc";

    public static IReadOnlyList<string> CachePaths
    {
        get
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return
            [
                Path.Join(windows, @"SoftwareDistribution\DeliveryOptimization"),
                Path.Join(
                    windows,
                    @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"
                ),
            ];
        }
    }

    public static long GetSize(CancellationToken cancellationToken = default)
    {
        return CachePaths.Sum(path => FileSystemCleaner.GetDirectorySize(path, cancellationToken));
    }

    public static async Task<long> Clean(CancellationToken cancellationToken = default)
    {
        var wasRunning = await IsServiceRunning();
        if (wasRunning)
        {
            await RunAndWait("sc.exe", $"stop {ServiceName}");
            await Task.Delay(1500, cancellationToken);
        }

        long freed = 0;
        foreach (var path in CachePaths)
            freed += FileSystemCleaner.DeleteDirectoryContents(path, cancellationToken);

        if (wasRunning)
            await RunAndWait("sc.exe", $"start {ServiceName}");

        return freed;
    }

    private static async Task<bool> IsServiceRunning()
    {
        var output = await JeekTools.Executor.RunWithOutput("sc.exe", $"query {ServiceName}");
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
