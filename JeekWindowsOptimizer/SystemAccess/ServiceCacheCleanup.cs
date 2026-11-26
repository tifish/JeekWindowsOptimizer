using System.ComponentModel;
using System.ServiceProcess;

namespace JeekWindowsOptimizer;

internal static class ServiceCacheCleanup
{
    internal static Task<long> CleanAsync(IReadOnlyList<string> services, IReadOnlyList<string> paths,
        CancellationToken token) => Task.Run(() => WithServicesStopped(services, token,
            NeedsStop,
            name => ServiceRuntime.Stop(name, ServiceRuntime.DefaultTimeout),
            name => ServiceRuntime.Start(name, ServiceRuntime.DefaultTimeout, out _),
            () => paths.Sum(path => FileSystemCleaner.DeleteDirectoryContents(path, token))), token);

    private static bool NeedsStop(string name)
    {
        using var service = new ServiceController(name);
        try
        {
            return service.Status switch
            {
                ServiceControllerStatus.Running => true,
                ServiceControllerStatus.Stopped => false,
                _ => throw new IOException($"Service {name} is {service.Status}. Try again when it is running or stopped."),
            };
        }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 1060 })
        {
            return false; // This Windows installation does not have the service.
        }
    }

    internal static long WithServicesStopped(IReadOnlyList<string> services, CancellationToken token,
        Func<string, bool> isRunning, Func<string, ServiceStopResult> stop, Func<string, bool> start,
        Func<long> clean)
    {
        var restore = new Stack<string>();
        try
        {
            foreach (var name in services)
            {
                token.ThrowIfCancellationRequested();
                if (!isRunning(name))
                    continue;

                // A timeout can leave an accepted stop request pending.
                restore.Push(name);
                var result = stop(name);
                if (result.Outcome is ServiceStopOutcome.AlreadyStopped or ServiceStopOutcome.NotFound)
                    restore.Pop();
                if (!result.IsStopped)
                    throw new IOException($"Could not stop {name}: {result.Error ?? result.Outcome.ToString()}");
            }
            token.ThrowIfCancellationRequested();
            return clean();
        }
        finally
        {
            var failed = new List<string>();
            foreach (var name in restore)
                if (!start(name))
                    failed.Add(name);
            if (failed.Count > 0)
                throw new IOException($"Could not restart {string.Join(", ", failed)}.");
        }
    }
}
