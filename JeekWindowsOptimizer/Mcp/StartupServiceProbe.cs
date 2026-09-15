using System.Text;
using JeekWindowsOptimizer.Startup;
using Microsoft.Win32;

namespace JeekWindowsOptimizer.Mcp;

/// <summary>
///     Exercises denying and allowing a real service end to end: start type, stop on deny, start on
///     allow, and restoring exactly what was there before.
///     <para>
///         This touches a real service, so it goes through <see cref="StartupToggle" /> directly
///         rather than the decide command: nothing is written to the permanent decision ledger. The
///         original start type, delayed-start flag and running state are restored in a finally block
///         whatever happens. A dependency check is also run against RpcSs, which is read-only: the
///         dependents are found before any stop request would be sent, and RpcSs cannot be stopped
///         anyway.
///     </para>
/// </summary>
internal static class StartupServiceProbe
{
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    public static async Task<string> RunAsync(MainViewModel vm, string serviceName)
    {
        var report = new StringBuilder();
        var failures = 0;

        void Check(string name, bool passed, string detail = "")
        {
            if (!passed)
                failures++;
            report.Append(passed ? "PASS  " : "FAIL  ").Append(name);
            if (detail.Length > 0)
                report.Append("  [").Append(detail).Append(']');
            report.AppendLine();
        }

        await vm.EnsureStartupItemsAsync();

        var item = vm.StartupItems.FirstOrDefault(i =>
            i.Kind == StartupItemKind.Service
            && string.Equals(i.Name, serviceName, StringComparison.OrdinalIgnoreCase));
        if (item is null)
            return $"No service row named '{serviceName}'.";
        if (!item.CanToggle)
            return $"'{serviceName}' is read-only on the Startup tab.";

        var originalStart = ReadStart(serviceName);
        var originalDelayed = ReadDelayed(serviceName);
        var originallyRunning = ServiceRuntime.IsRunning(serviceName);
        var originalEnabled = item.IsEnabled;
        report.AppendLine(
            $"{serviceName}: start={originalStart} delayed={originalDelayed?.ToString() ?? "-"} running={originallyRunning}");
        report.AppendLine();

        try
        {
            // Deny: disabled and stopped.
            var deny = await Task.Run(() => StartupToggle.Apply(item, enabled: false));
            Check("deny succeeds", deny.Succeeded, deny.Error ?? "");
            Check("deny reports no warning for a stoppable service", deny.Warning is null, deny.Warning ?? "");
            Check("deny sets the start type to Disabled", ReadStart(serviceName) == 4,
                ReadStart(serviceName).ToString());
            Check("deny stops the service", !ServiceRuntime.IsRunning(serviceName));

            // Allow: original start type back, and an automatic service running again.
            var allow = await Task.Run(() => StartupToggle.Apply(item, enabled: true));
            Check("allow succeeds", allow.Succeeded, allow.Error ?? "");
            Check("allow restores the original start type", ReadStart(serviceName) == originalStart,
                ReadStart(serviceName).ToString());
            Check("allow keeps the delayed-start flag", ReadDelayed(serviceName) == originalDelayed,
                ReadDelayed(serviceName)?.ToString() ?? "-");
            if (originalStart == 2)
                Check("allow starts an automatic service again", ServiceRuntime.IsRunning(serviceName));

            // A service with running dependents is reported, not stopped along with them.
            var dependents = await Task.Run(() => ServiceRuntime.Stop("RpcSs", TimeSpan.FromSeconds(1)));
            Check("a service with running dependents is left running",
                dependents.Outcome == ServiceStopOutcome.HasRunningDependents
                && ServiceRuntime.IsRunning("RpcSs"),
                $"{dependents.Outcome}, {dependents.RunningDependents.Count} dependents");

            var missing = await Task.Run(() => ServiceRuntime.Stop("JeekNoSuchService_" + Guid.NewGuid().ToString("N")[..6], TimeSpan.FromSeconds(1)));
            Check("a missing service counts as stopped", missing.IsStopped, missing.Outcome.ToString());
        }
        finally
        {
            // Put everything back exactly, even if a check above threw.
            if (ReadStart(serviceName) != originalStart)
                ServiceRegistryScanner.SetStartMode(serviceName, originalStart, out _);
            if (ReadDelayed(serviceName) != originalDelayed)
                WriteDelayed(serviceName, originalDelayed);
            StartupLocalState.ForgetServiceStart(serviceName);

            if (originallyRunning && !ServiceRuntime.IsRunning(serviceName))
                ServiceRuntime.Start(serviceName, ServiceRuntime.DefaultTimeout, out _);
            else if (!originallyRunning && ServiceRuntime.IsRunning(serviceName))
                ServiceRuntime.Stop(serviceName, ServiceRuntime.DefaultTimeout);

            item.IsEnabled = originalEnabled;
            item.WarningMessage = null;
            item.ErrorMessage = null;
        }

        var restored = ReadStart(serviceName) == originalStart
            && ReadDelayed(serviceName) == originalDelayed
            && ServiceRuntime.IsRunning(serviceName) == originallyRunning;
        Check("original state fully restored", restored,
            $"start={ReadStart(serviceName)} delayed={ReadDelayed(serviceName)?.ToString() ?? "-"} running={ServiceRuntime.IsRunning(serviceName)}");

        report.AppendLine();
        report.AppendLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
        return report.ToString();
    }

    private static int ReadStart(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesPath}\{serviceName}");
        return key?.GetValue("Start") as int? ?? -1;
    }

    private static int? ReadDelayed(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesPath}\{serviceName}");
        return key?.GetValue("DelayedAutostart") as int?;
    }

    private static void WriteDelayed(string serviceName, int? value)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesPath}\{serviceName}", writable: true);
        if (key is null)
            return;
        if (value is null)
            key.DeleteValue("DelayedAutostart", throwOnMissingValue: false);
        else
            key.SetValue("DelayedAutostart", value.Value, RegistryValueKind.DWord);
    }
}
