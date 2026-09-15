using Jeek.Avalonia.Localization;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer.Startup;

/// <summary>The outcome of putting one decision into effect.</summary>
/// <param name="Warning">
///     Set when the decision was recorded in the system but has not fully taken effect yet, for
///     example a disabled service that could not be stopped right now.
/// </param>
public readonly record struct StartupToggleResult(
    bool Succeeded,
    string? Error,
    bool RequiresExplorerRestart,
    string? Warning = null
)
{
    public static StartupToggleResult Ok(bool requiresExplorerRestart = false, string? warning = null) =>
        new(true, null, requiresExplorerRestart, warning);

    public static StartupToggleResult Fail(string? error) => new(false, error, false);
}

/// <summary>
///     Turns a decision into an actual system change.
///     <para>
///         Every mechanism here is Windows' own on/off switch rather than a private scheme: the
///         StartupApproved flags Task Manager writes, a service's Start value, the Task Scheduler
///         Enabled flag, Explorer's blocked-CLSID list. Registrations are never deleted or moved.
///         That keeps this tab consistent with what the rest of Windows reports, and makes
///         re-enabling exact instead of approximate.
///     </para>
/// </summary>
public static class StartupToggle
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(StartupToggle));

    public static StartupToggleResult Apply(StartupItem item, bool enabled)
    {
        if (!item.CanToggle)
            return StartupToggleResult.Fail("This entry is read-only.");

        try
        {
            switch (item.Kind)
            {
                case StartupItemKind.LogonRegistry:
                case StartupItemKind.StartupFolder:
                    return StartupRegistryScanner.SetEnabled(item.RestoreState, enabled, out var registryError)
                        ? StartupToggleResult.Ok()
                        : StartupToggleResult.Fail(registryError);

                case StartupItemKind.Service:
                case StartupItemKind.Driver:
                    return ApplyService(item, enabled);

                case StartupItemKind.ScheduledTask:
                {
                    var path = item.RestoreState ?? item.Name;
                    var result = WindowsScheduledTask.TrySetEnabled(path, enabled);
                    return result switch
                    {
                        WindowsScheduledTask.SetEnabledResult.Success => StartupToggleResult.Ok(),
                        WindowsScheduledTask.SetEnabledResult.NotFound => StartupToggleResult.Fail(
                            "The scheduled task no longer exists."
                        ),
                        WindowsScheduledTask.SetEnabledResult.AccessDenied =>
                            StartupToggleResult.Fail("Access to the scheduled task was denied."),
                        _ => StartupToggleResult.Fail("The scheduled task could not be changed."),
                    };
                }

                case StartupItemKind.ExplorerExtension:
                    return ShellExtensionScanner.SetEnabled(item.RestoreState, enabled, out var shellError)
                        ? StartupToggleResult.Ok(requiresExplorerRestart: true)
                        : StartupToggleResult.Fail(shellError);

                case StartupItemKind.InternetExplorerExtension:
                    // A browser add-on needs the browser's own switch. Explorer's blocked list does
                    // not stop the browser host loading it, so using that here would report success
                    // while the add-on kept loading.
                    return BrowserAddOnRegistry.SetEnabled(item.RestoreState, enabled, out var addOnError)
                        ? StartupToggleResult.Ok()
                        : StartupToggleResult.Fail(addOnError);

                default:
                    return StartupToggleResult.Fail("Unsupported startup entry type.");
            }
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to apply startup decision for {item.Name}");
            return StartupToggleResult.Fail(ex.Message);
        }
    }

    private const int StartAutomatic = 2;
    private const int StartDisabled = 4;

    private static StartupToggleResult ApplyService(StartupItem item, bool enabled)
    {
        var isDriver = item.Kind == StartupItemKind.Driver;

        if (enabled)
        {
            // Put back the start type the service had before it was denied, not a guessed default.
            var start = StartupLocalState.RecallServiceStart(item.Name);
            if (!ServiceRegistryScanner.SetStartMode(item.Name, start, out var enableError))
                return StartupToggleResult.Fail(enableError);

            StartupLocalState.ForgetServiceStart(item.Name);

            if (isDriver)
                return StartupToggleResult.Ok(warning: Localizer.Get("StartupWarningDriverReboot"));

            // The mirror of stopping on deny: an automatic service that was stopped because it was
            // denied would otherwise stay down until the next reboot. Manual services are left for
            // whatever triggers them, which is what Manual means.
            if (start == StartAutomatic
                && !ServiceRuntime.Start(item.Name, ServiceRuntime.DefaultTimeout, out var startError))
                return StartupToggleResult.Ok(warning: string.Format(
                    Localizer.Get("StartupWarningServiceStartFailed"), startError));

            return StartupToggleResult.Ok();
        }

        // Capture the current start type before overwriting it, or the original is lost forever.
        if (int.TryParse(item.RestoreState, out var current))
            StartupLocalState.RememberServiceStart(item.Name, current);

        // Disable first. If stopping then fails the service at least cannot come back after a
        // reboot, and the row says it is still running rather than pretending otherwise.
        if (!ServiceRegistryScanner.SetStartMode(item.Name, StartDisabled, out var disableError))
            return StartupToggleResult.Fail(disableError);

        // Kernel drivers are not unloaded on the spot. Many refuse, and yanking a filter driver
        // that is in use can take the machine down with it; the disabled start type is enough.
        if (isDriver)
            return StartupToggleResult.Ok(warning: Localizer.Get("StartupWarningDriverReboot"));

        return StartupToggleResult.Ok(warning: StopServiceAndInstances(item.Name));
    }

    /// <summary>
    ///     Stops a denied service and, for a user-service template, the per-logon instances that are
    ///     what actually run. Returns a warning for whatever is still running, or null.
    /// </summary>
    private static string? StopServiceAndInstances(string serviceName)
    {
        var results = new List<ServiceStopResult>
        {
            ServiceRuntime.Stop(serviceName, ServiceRuntime.DefaultTimeout),
        };

        foreach (var instance in ServiceRuntime.FindUserServiceInstances(serviceName))
            results.Add(ServiceRuntime.Stop(instance, ServiceRuntime.DefaultTimeout));

        var problem = results.FirstOrDefault(result => !result.IsStopped);
        if (problem is null)
            return null;

        Log.ZLogWarning(
            $"Service {problem.ServiceName} was disabled but not stopped: {problem.Outcome} {problem.Error}"
        );

        return problem.Outcome switch
        {
            ServiceStopOutcome.HasRunningDependents => string.Format(
                Localizer.Get("StartupWarningServiceHasDependents"),
                string.Join(", ", problem.RunningDependents.Take(3))
                    + (problem.RunningDependents.Count > 3 ? "…" : "")
            ),
            ServiceStopOutcome.CannotStop => Localizer.Get("StartupWarningServiceCannotStop"),
            ServiceStopOutcome.TimedOut => Localizer.Get("StartupWarningServiceStopTimedOut"),
            _ => string.Format(Localizer.Get("StartupWarningServiceStopFailed"), problem.Error ?? ""),
        };
    }
}
