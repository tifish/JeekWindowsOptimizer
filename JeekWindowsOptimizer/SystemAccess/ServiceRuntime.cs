using System.ServiceProcess;
using JeekTools;
using JeekWindowsOptimizer.Startup;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ZLogger;

namespace JeekWindowsOptimizer;

/// <summary>What happened when a running service was asked to stop.</summary>
public enum ServiceStopOutcome
{
    Stopped,
    AlreadyStopped,

    /// <summary>No such service. Normal for a user-service template, whose instances are what run.</summary>
    NotFound,

    /// <summary>Left running because other running services depend on it.</summary>
    HasRunningDependents,

    /// <summary>The service does not accept a stop request.</summary>
    CannotStop,

    TimedOut,
    Failed,
}

public sealed record ServiceStopResult(
    string ServiceName,
    ServiceStopOutcome Outcome,
    IReadOnlyList<string> RunningDependents,
    string? Error
)
{
    public bool IsStopped =>
        Outcome is ServiceStopOutcome.Stopped
            or ServiceStopOutcome.AlreadyStopped
            or ServiceStopOutcome.NotFound;
}

/// <summary>
///     Stops and starts services through the Service Control Manager and waits for the result.
///     <para>
///         <see cref="WindowsService" /> goes through WMI, whose StopService returns as soon as the
///         request is queued. That cannot tell "stopped" from "still stopping" or "refused", and a
///         decision that claims to have taken effect has to know which.
///     </para>
/// </summary>
public static class ServiceRuntime
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(ServiceRuntime));

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    ///     Stops one service. Services that depend on it are never stopped as a side effect: denying
    ///     one entry must not quietly stop others the user allowed, so a service with running
    ///     dependents is left running and reported instead.
    /// </summary>
    public static ServiceStopResult Stop(string serviceName, TimeSpan timeout)
    {
        try
        {
            using var controller = new ServiceController(serviceName);

            ServiceControllerStatus status;
            try
            {
                status = controller.Status;
            }
            catch (InvalidOperationException)
            {
                return new(serviceName, ServiceStopOutcome.NotFound, [], null);
            }

            if (status == ServiceControllerStatus.Stopped)
                return new(serviceName, ServiceStopOutcome.AlreadyStopped, [], null);

            // Checked before anything is sent, so this path never changes a running system.
            var runningDependents = RunningDependentsOf(controller);
            if (runningDependents.Count > 0)
                return new(serviceName, ServiceStopOutcome.HasRunningDependents, runningDependents, null);

            if (status != ServiceControllerStatus.StopPending)
            {
                if (!controller.CanStop)
                    return new(serviceName, ServiceStopOutcome.CannotStop, [], null);

                controller.Stop(stopDependentServices: false);
            }

            try
            {
                controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                return new(serviceName, ServiceStopOutcome.TimedOut, [], null);
            }

            return new(serviceName, ServiceStopOutcome.Stopped, [], null);
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to stop service {serviceName}");
            return new(serviceName, ServiceStopOutcome.Failed, [], Innermost(ex).Message);
        }
    }

    /// <summary>Starts a service and waits until it is running.</summary>
    public static bool Start(string serviceName, TimeSpan timeout, out string? error)
    {
        error = null;

        try
        {
            using var controller = new ServiceController(serviceName);
            var status = controller.Status;
            if (status == ServiceControllerStatus.Running)
                return true;

            if (status != ServiceControllerStatus.StartPending)
                controller.Start();

            controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
            return true;
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to start service {serviceName}");
            error = Innermost(ex).Message;
            return false;
        }
    }

    public static bool IsRunning(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            return controller.Status is ServiceControllerStatus.Running
                or ServiceControllerStatus.StartPending;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads the live running state of one service (or of a user-service template's instances).</summary>
    public static StartupServiceRunState GetRunState(string serviceName, bool isUserServiceTemplate = false)
    {
        var best = ReadSingleRunState(serviceName);

        if (isUserServiceTemplate)
        {
            foreach (var instance in FindUserServiceInstances(serviceName))
                best = PickMoreActive(best, ReadSingleRunState(instance));
        }

        return best;
    }

    /// <summary>
    ///     Enumerates every Win32 service's live running state in one SCM call, so scanning hundreds
    ///     of services does not pay per-service handle open overhead.
    /// </summary>
    public static ServiceRunStateSnapshot SnapshotRunStates()
    {
        var exact = new Dictionary<string, StartupServiceRunState>(StringComparer.OrdinalIgnoreCase);
        var templateBest = new Dictionary<string, StartupServiceRunState>(StringComparer.OrdinalIgnoreCase);
        ServiceController[]? controllers = null;

        try
        {
            controllers = ServiceController.GetServices();
            foreach (var controller in controllers)
            {
                try
                {
                    var name = controller.ServiceName;
                    var state = MapStatus(controller.Status);
                    exact[name] = state;

                    if (TryExtractUserServiceTemplate(name, out var template))
                    {
                        templateBest[template] = templateBest.TryGetValue(template, out var existing)
                            ? PickMoreActive(existing, state)
                            : state;
                    }
                }
                catch
                {
                    // Skip unreadable service entries.
                }
            }
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to snapshot Windows service running states");
        }
        finally
        {
            if (controllers is not null)
            {
                foreach (var controller in controllers)
                    controller.Dispose();
            }
        }

        return new ServiceRunStateSnapshot(exact, templateBest);
    }

    private static StartupServiceRunState ReadSingleRunState(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            return MapStatus(controller.Status);
        }
        catch
        {
            return StartupServiceRunState.Unknown;
        }
    }

    private static StartupServiceRunState MapStatus(ServiceControllerStatus status) =>
        status switch
        {
            ServiceControllerStatus.Running => StartupServiceRunState.Running,
            ServiceControllerStatus.Stopped => StartupServiceRunState.Stopped,
            ServiceControllerStatus.Paused => StartupServiceRunState.Paused,
            ServiceControllerStatus.StartPending => StartupServiceRunState.StartPending,
            ServiceControllerStatus.StopPending => StartupServiceRunState.StopPending,
            ServiceControllerStatus.PausePending => StartupServiceRunState.PausePending,
            ServiceControllerStatus.ContinuePending => StartupServiceRunState.ContinuePending,
            _ => StartupServiceRunState.Unknown,
        };

    internal static StartupServiceRunState PickMoreActive(
        StartupServiceRunState a,
        StartupServiceRunState b
    ) => Priority(b) > Priority(a) ? b : a;

    private static int Priority(StartupServiceRunState state) =>
        state switch
        {
            StartupServiceRunState.Running => 6,
            StartupServiceRunState.StartPending or StartupServiceRunState.ContinuePending => 5,
            StartupServiceRunState.StopPending or StartupServiceRunState.PausePending => 4,
            StartupServiceRunState.Paused => 3,
            StartupServiceRunState.Stopped => 2,
            _ => 1,
        };

    private static bool TryExtractUserServiceTemplate(string name, out string template)
    {
        template = "";
        var underscore = name.LastIndexOf('_');
        if (underscore <= 0 || underscore == name.Length - 1)
            return false;

        var suffix = name[(underscore + 1)..];
        if (!suffix.All(char.IsAsciiHexDigit))
            return false;

        template = name[..underscore];
        return true;
    }

    /// <summary>
    ///     The per-logon instances of a user-service template, e.g. <c>CDPUserSvc_4a2b1</c> for
    ///     <c>CDPUserSvc</c>. The template never runs itself; its instances do, so stopping a denied
    ///     template means stopping these.
    /// </summary>
    public static List<string> FindUserServiceInstances(string templateName)
    {
        var instances = new List<string>();
        var prefix = templateName + "_";

        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null)
                return instances;

            foreach (var name in services.GetSubKeyNames())
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var suffix = name[prefix.Length..];
                if (suffix.Length > 0 && suffix.All(char.IsAsciiHexDigit))
                    instances.Add(name);
            }
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to look for instances of user service {templateName}");
        }

        return instances;
    }

    private static List<string> RunningDependentsOf(ServiceController controller)
    {
        try
        {
            return
            [
                .. controller
                    .DependentServices.Where(dependent =>
                        dependent.Status != ServiceControllerStatus.Stopped
                    )
                    .Select(dependent => dependent.ServiceName),
            ];
        }
        catch
        {
            return [];
        }
    }

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is not null)
            ex = ex.InnerException;
        return ex;
    }
}

/// <summary>Snapshot of live SCM running states for fast O(1) lookup during startup scans.</summary>
public sealed class ServiceRunStateSnapshot
{
    private readonly Dictionary<string, StartupServiceRunState> _exact;
    private readonly Dictionary<string, StartupServiceRunState> _templateBest;

    internal ServiceRunStateSnapshot(
        Dictionary<string, StartupServiceRunState> exact,
        Dictionary<string, StartupServiceRunState> templateBest
    )
    {
        _exact = exact;
        _templateBest = templateBest;
    }

    public StartupServiceRunState GetState(string serviceName, bool isUserServiceTemplate)
    {
        var best = _exact.TryGetValue(serviceName, out var exactState)
            ? exactState
            : StartupServiceRunState.Unknown;

        if (isUserServiceTemplate && _templateBest.TryGetValue(serviceName, out var instanceState))
            best = ServiceRuntime.PickMoreActive(best, instanceState);

        if (best == StartupServiceRunState.Unknown && _exact.Count > 0)
            return StartupServiceRunState.Stopped;

        return best;
    }
}
