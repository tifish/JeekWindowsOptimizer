using System.Reflection;
using System.Text;
using Avalonia.Threading;
using Jeek.Avalonia.Localization;
using JeekWindowsOptimizer.Startup;
using Microsoft.Win32;

namespace JeekWindowsOptimizer.Mcp;

/// <summary>
///     Isolated end-to-end regression checks for:
///     1. RunOnce "run now and delete" (real scratch command execution, registry deletion, safety guards, ViewModel).
///     2. Scheduled task enabled/disabled status and trigger timing (COM scratch task + localization).
///     3. System service startup mode and running status (SCM snapshot, live ReadRuntimeSnapshot, localization).
/// </summary>
internal static class StartupDetailsProbe
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

    private delegate void CheckFn(string name, bool passed, string detail = "");

    public static async Task<string> RunAsync()
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

        var token = Guid.NewGuid().ToString("N")[..8];

        await Task.Run(() =>
        {
            RunOnceEndToEndScenario(token, Check);
            ScheduledTaskMetadataScenario(token, Check);
            ServiceMetadataScenario(Check);
        });

        await RunOnceViewModelScenarioAsync(Check);

        report.AppendLine();
        report.AppendLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
        return report.ToString();
    }

    private static StartupItem Materialize(RawStartupEntry entry)
    {
        var expanded = StartupIdentity.ExpandPath(entry.ImagePath);
        return new StartupItem
        {
            Kind = entry.Kind,
            Key = StartupIdentity.ComputeKey(entry.Kind, entry.Location, entry.Name, entry.ImagePath),
            Name = entry.Name,
            Location = entry.Location,
            Command = entry.Command,
            ImagePath = expanded,
            HookPoints = entry.HookPoints,
            Handles = entry.Handles,
            IsOrphaned = expanded.Length > 0 && !File.Exists(expanded),
            DeleteTarget = entry.DeleteTarget,
            CanToggle = entry.CanToggle,
            ReadOnlyReasonKey = entry.ReadOnlyReasonKey,
            RestoreState = entry.RestoreState,
            TaskTriggers = entry.TaskTriggers,
            ServiceStartMode = entry.ServiceRuntime?.StartMode,
            ServiceDelayedAutoStart = entry.ServiceRuntime?.DelayedAutoStart ?? false,
            ServiceTriggerStart = entry.ServiceRuntime?.TriggerStart ?? false,
            ServiceRunState = entry.ServiceRuntime?.RunState,
            IsEnabled = entry.IsEnabled,
        };
    }

    private static void RunOnceEndToEndScenario(string token, CheckFn check)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"Jeek RunOnce Probe {token}");
        var scriptPath = Path.Combine(tempDir, "run once test.cmd");
        var markerPath = Path.Combine(tempDir, "marker.txt");
        var runOnceName = $"JeekRunOnceProbe_{token}";
        var runName = $"JeekRunOnceGuardRun_{token}";
        var staleName = $"JeekRunOnceStale_{token}";
        var staleFile = Path.Combine(tempDir, "vanishing.cmd");

        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(scriptPath, $"@echo off\r\necho executed>\"{markerPath}\"\r\n");
            File.WriteAllText(staleFile, "@echo off\r\n");

            using (var runOnceKey = Registry.CurrentUser.CreateSubKey(RunOncePath, writable: true))
            {
                runOnceKey!.SetValue(runOnceName, $"\"{scriptPath}\"", RegistryValueKind.String);
                runOnceKey.SetValue(staleName, $"\"{staleFile}\"", RegistryValueKind.String);
            }

            using (var runKey = Registry.CurrentUser.CreateSubKey(RunPath, writable: true))
                runKey!.SetValue(runName, $"\"{scriptPath}\"", RegistryValueKind.String);

            var scanned = StartupRegistryScanner.Scan();
            var runOnceRaw = scanned.FirstOrDefault(e => e.Kind == StartupItemKind.RunOnce && e.Name == runOnceName);
            var runRaw = scanned.FirstOrDefault(e => e.Kind == StartupItemKind.LogonRegistry && e.Name == runName);
            var staleRaw = scanned.FirstOrDefault(e => e.Kind == StartupItemKind.RunOnce && e.Name == staleName);

            var runOnceItem = runOnceRaw is not null ? Materialize(runOnceRaw) : null;
            var runItem = runRaw is not null ? Materialize(runRaw) : null;
            var staleItem = staleRaw is not null ? Materialize(staleRaw) : null;

            check("healthy RunOnce entry is runnable-and-deletable and not orphaned",
                runOnceItem is { IsRunOnce: true, IsOrphaned: false, CanToggle: false, CanDelete: false, CanRunOnceAndDelete: true });
            check("normal Run entry is not marked CanRunOnceAndDelete",
                runItem is { IsRunOnce: false, CanRunOnceAndDelete: false });

            if (runItem is not null)
            {
                var refused = StartupToggle.RunOnceAndDelete(runItem);
                using var runKey = Registry.CurrentUser.OpenSubKey(RunPath);
                check("RunOnceAndDelete refuses normal Run entries and leaves them intact",
                    !refused.Succeeded && runKey?.GetValue(runName) is not null);
            }

            if (runOnceItem is not null)
            {
                var result = StartupToggle.RunOnceAndDelete(runOnceItem);
                var markerReached = WaitForMarker(markerPath, TimeSpan.FromSeconds(5));
                using var runOnceKey = Registry.CurrentUser.OpenSubKey(RunOncePath);
                var removedFromRegistry = runOnceKey?.GetValue(runOnceName) is null;
                check("RunOnceAndDelete executes command (with spaces in path) and deletes registry value",
                    result.Succeeded && markerReached && removedFromRegistry,
                    $"ok={result.Succeeded} marker={markerReached} removed={removedFromRegistry} err={result.Error}");

                // Running the same stale item again must report missing.
                var second = StartupToggle.RunOnceAndDelete(runOnceItem);
                check("RunOnceAndDelete reports missing when registry value is already gone",
                    !second.Succeeded && second.Error == Localizer.Get("StartupRunOnceMissing"),
                    second.Error ?? "");
            }

            if (staleItem is not null)
            {
                File.Delete(staleFile);
                var missingFileResult = StartupToggle.RunOnceAndDelete(staleItem);
                using var runOnceKey = Registry.CurrentUser.OpenSubKey(RunOncePath);
                check("RunOnceAndDelete aborts and keeps registry value if target file vanished after scan",
                    !missingFileResult.Succeeded
                    && missingFileResult.Error == Localizer.Get("StartupRunOnceFileMissing")
                    && runOnceKey?.GetValue(staleName) is not null,
                    missingFileResult.Error ?? "");
            }
        }
        finally
        {
            DeleteRegistryValueQuietly(Registry.CurrentUser, RunOncePath, runOnceName);
            DeleteRegistryValueQuietly(Registry.CurrentUser, RunOncePath, staleName);
            DeleteRegistryValueQuietly(Registry.CurrentUser, RunPath, runName);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void ScheduledTaskMetadataScenario(string token, CheckFn check)
    {
        var taskName = $"JeekMetaProbe_{token}";
        var taskPath = "\\" + taskName;
        var savedLanguage = Localizer.Language;

        try
        {
            CreateMultiTriggerTask(taskName, @"C:\Windows\System32\cmd.exe");

            var rawEnabled = ScheduledTaskScanner.Scan()
                .FirstOrDefault(e => string.Equals(e.Name, taskPath, StringComparison.OrdinalIgnoreCase));
            var itemEnabled = rawEnabled is not null ? Materialize(rawEnabled) : null;

            check("scheduled task scans enabled state and all 4 triggers",
                itemEnabled is { IsScheduledTask: true, IsEnabled: true, IsTaskEnabled: true, IsTaskDisabled: false, HasTaskTriggers: true }
                && itemEnabled.TaskTriggers.Count == 4,
                itemEnabled is null ? "(not found)" : $"triggers={itemEnabled.TaskTriggers.Count}");

            if (itemEnabled is not null)
            {
                var (enState, enTriggers) = SetLanguageAndRead("en", () =>
                    (itemEnabled.TaskEnabledText, itemEnabled.TaskTriggersText));
                check("scheduled task EN status and triggers format properly",
                    enState == "Enabled"
                    && enTriggers.Contains("At logon", StringComparison.Ordinal)
                    && enTriggers.Contains("30s", StringComparison.Ordinal)
                    && enTriggers.Contains("At startup", StringComparison.Ordinal)
                    && enTriggers.Contains("On unlock", StringComparison.Ordinal)
                    && enTriggers.Contains("Daily at 08:30", StringComparison.Ordinal)
                    && enTriggers.Contains("trigger disabled", StringComparison.Ordinal),
                    $"{enState} | {enTriggers}");

                var (zhState, zhTriggers) = SetLanguageAndRead("zh", () =>
                    (itemEnabled.TaskEnabledText, itemEnabled.TaskTriggersText));
                check("scheduled task ZH status and triggers format properly",
                    zhState == "已启用"
                    && zhTriggers.Contains("用户登录时", StringComparison.Ordinal)
                    && zhTriggers.Contains("30秒", StringComparison.Ordinal)
                    && zhTriggers.Contains("系统启动时", StringComparison.Ordinal)
                    && zhTriggers.Contains("解锁时", StringComparison.Ordinal)
                    && zhTriggers.Contains("每天 08:30", StringComparison.Ordinal)
                    && zhTriggers.Contains("触发器已禁用", StringComparison.Ordinal),
                    $"{zhState} | {zhTriggers}");

                var disableResult = StartupToggle.Apply(itemEnabled, enabled: false);
                var rawDisabled = ScheduledTaskScanner.Scan()
                    .FirstOrDefault(e => string.Equals(e.Name, taskPath, StringComparison.OrdinalIgnoreCase));
                var itemDisabled = rawDisabled is not null ? Materialize(rawDisabled) : null;
                var zhDisabled = itemDisabled is not null
                    ? SetLanguageAndRead("zh", () => itemDisabled.TaskEnabledText)
                    : "";
                check("disabled scheduled task scans as disabled with localized badge text",
                    disableResult.Succeeded
                    && itemDisabled is { IsEnabled: false, IsTaskEnabled: false, IsTaskDisabled: true }
                    && zhDisabled == "已禁用",
                    zhDisabled.Length > 0 ? zhDisabled : "(not found)");
            }
        }
        finally
        {
            SetLanguage(savedLanguage);
            if (WindowsScheduledTask.Exists(taskPath))
                WindowsScheduledTask.TryDelete(taskPath);
        }
    }

    private static void ServiceMetadataScenario(CheckFn check)
    {
        var savedLanguage = Localizer.Language;

        try
        {
            var scanned = ServiceRegistryScanner.Scan();
            var rpcRaw = scanned.FirstOrDefault(e =>
                e.Kind == StartupItemKind.Service
                && string.Equals(e.Name, "RpcSs", StringComparison.OrdinalIgnoreCase));
            var rpcItem = rpcRaw is not null ? Materialize(rpcRaw) : null;

            check("RpcSs scans as Automatic and Running",
                rpcItem is
                {
                    IsService: true,
                    HasServiceStartMode: true,
                    HasServiceRunState: true,
                    ServiceStartMode: StartupServiceStartMode.Automatic,
                    ServiceRunState: StartupServiceRunState.Running,
                    IsServiceAutomatic: true,
                    IsServiceRunning: true,
                },
                rpcItem is null ? "(not found)" : $"{rpcItem.ServiceStartMode}/{rpcItem.ServiceRunState}");

            var services = scanned.Where(e => e.Kind == StartupItemKind.Service).ToList();
            check("all scanned services carry start mode and run state",
                services.Count > 20
                && services.All(e => e.ServiceRuntime is not null && e.ServiceRuntime.RunState != StartupServiceRunState.Unknown),
                $"services={services.Count}");

            var liveRpc = ServiceRegistryScanner.ReadRuntimeSnapshot("RpcSs");
            check("ReadRuntimeSnapshot reports live Automatic and Running for RpcSs",
                liveRpc is { StartMode: StartupServiceStartMode.Automatic, RunState: StartupServiceRunState.Running },
                liveRpc?.ToString() ?? "(null)");

            // Localization & live ApplyServiceRuntime check.
            var synthetic = new StartupItem
            {
                Kind = StartupItemKind.Service,
                Key = "MetaProbe|svc",
                Name = "SyntheticSvc",
                Location = @"HKLM\SYSTEM\CurrentControlSet\Services\SyntheticSvc",
                IsEnabled = true,
                ServiceStartMode = StartupServiceStartMode.Automatic,
                ServiceDelayedAutoStart = true,
                ServiceTriggerStart = false,
                ServiceRunState = StartupServiceRunState.Running,
            };

            var enAutoDelayed = SetLanguageAndRead("en", () =>
                $"{synthetic.ServiceStartModeText} | {synthetic.ServiceRunStateText}");
            var zhAutoDelayed = SetLanguageAndRead("zh", () =>
                $"{synthetic.ServiceStartModeText} | {synthetic.ServiceRunStateText}");

            check("service start mode and run state localize cleanly (EN & ZH)",
                enAutoDelayed == "Automatic (Delayed) | Running"
                && zhAutoDelayed == "自动（延迟启动） | 正在运行",
                $"en='{enAutoDelayed}', zh='{zhAutoDelayed}'");

            synthetic.ApplyServiceRuntime(new ServiceRuntimeSnapshot(
                StartupServiceStartMode.Disabled,
                false,
                false,
                StartupServiceRunState.Stopped
            ));
            check("ApplyServiceRuntime updates start mode and run state live",
                synthetic.ServiceStartMode == StartupServiceStartMode.Disabled
                && synthetic.ServiceRunState == StartupServiceRunState.Stopped
                && synthetic.IsServiceNonAutomatic
                && synthetic.IsServiceStoppedOrPaused
                && synthetic.ServiceStartModeText == "禁用"
                && synthetic.ServiceRunStateText == "已停止",
                $"{synthetic.ServiceStartModeText} | {synthetic.ServiceRunStateText}");
        }
        finally
        {
            SetLanguage(savedLanguage);
        }
    }

    private static async Task RunOnceViewModelScenarioAsync(CheckFn check)
    {
        using var release = new ManualResetEventSlim();
        var failNext = 0;
        var uiThreadCalls = 0;

        using var vm = new MainViewModel
        {
            StartupRunOnceHandler = _ =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                    Interlocked.Increment(ref uiThreadCalls);
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    return StartupRunOnceResult.Fail("Probe timed out.");
                return Interlocked.Exchange(ref failNext, 0) == 1
                    ? StartupRunOnceResult.Fail("Simulated run failure")
                    : StartupRunOnceResult.Ok();
            },
        };

        var runOnce = new StartupItem
        {
            Kind = StartupItemKind.RunOnce,
            Key = "MetaProbe|runonce",
            Name = "SetupFinish",
            Location = @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce",
            Command = @"C:\Windows\System32\cmd.exe /c echo ok",
            ImagePath = @"C:\Windows\System32\cmd.exe",
            IsOrphaned = false,
            CanToggle = false,
            ReadOnlyReasonKey = "StartupStatusRunOnce",
            DeleteTarget = new RegistryValueDeleteTarget(
                RegistryHive.CurrentUser,
                RegistryView.Registry64,
                RunOncePath,
                "SetupFinish",
                null
            ),
            IsEnabled = true,
        };

        vm.AllStartupGroups.Add(new StartupGroup(StartupItemKind.RunOnce, [runOnce]));

        Task work = Task.CompletedTask;
        try
        {
            failNext = 1;
            work = vm.RunOnceAndDeleteStartupItemAsync(runOnce, confirm: false);
            check("running RunOnce row shows running status and disables actions while in flight",
                runOnce.IsRunningOnce
                && runOnce.IsApplying
                && !runOnce.CanRunOnceAndDeleteNow
                && runOnce.StatusKey == "StartupStatusRunningOnce"
                && !vm.ScanStartupCommand.CanExecute(null));

            release.Set();
            var failed = await ((Task<bool>)work).WaitAsync(TimeSpan.FromSeconds(15));
            check("failed RunOnce keeps item, formats error, and restores controls",
                !failed
                && !runOnce.IsRunningOnce
                && runOnce.CanRunOnceAndDeleteNow
                && runOnce.ErrorMessage == "Simulated run failure"
                && runOnce.StatusText == string.Format(Localizer.Get("StartupRunOnceFailed"), "Simulated run failure")
                && vm.StartupItems.Contains(runOnce));

            var succeeded = await vm.RunOnceAndDeleteStartupItemAsync(runOnce, confirm: false);
            check("successful RunOnce runs off UI thread and removes item and empty group",
                succeeded
                && uiThreadCalls == 0
                && !vm.StartupItems.Contains(runOnce)
                && vm.AllStartupGroups.All(g => g.Kind != StartupItemKind.RunOnce)
                && vm.StartupGroups.All(g => g.Kind != StartupItemKind.RunOnce)
                && vm.StartupGroupNavItems.All(n => n.NameKey != "StartupKindRunOnce"));
        }
        finally
        {
            release.Set();
            try { await work; } catch { }
        }
    }

    private static bool WaitForMarker(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path).Contains("executed", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // Still being written.
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static void DeleteRegistryValueQuietly(RegistryKey root, string subKey, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }

    private static void CreateMultiTriggerTask(string taskName, string exePath)
    {
        var serviceType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Schedule.Service ProgID not found.");
        object? service = null;
        object? root = null;
        object? definition = null;
        object? triggers = null;
        var createdTriggers = new List<object>();
        object? actions = null;
        object? action = null;
        object? registered = null;
        try
        {
            service = Activator.CreateInstance(serviceType)!;
            WindowsScheduledTask.InvokeCom(service, "Connect");
            root = WindowsScheduledTask.InvokeCom(service, "GetFolder", "\\")!;
            definition = WindowsScheduledTask.InvokeCom(service, "NewTask", 0)!;

            triggers = WindowsScheduledTask.GetComProperty<object>(definition, "Triggers")!;

            var logon = WindowsScheduledTask.InvokeCom(triggers, "Create", 9)!;
            createdTriggers.Add(logon);
            SetProp(logon, "Delay", "PT30S");

            var boot = WindowsScheduledTask.InvokeCom(triggers, "Create", 8)!;
            createdTriggers.Add(boot);

            var unlock = WindowsScheduledTask.InvokeCom(triggers, "Create", 11)!;
            createdTriggers.Add(unlock);
            SetProp(unlock, "StateChange", 8);

            var daily = WindowsScheduledTask.InvokeCom(triggers, "Create", 2)!;
            createdTriggers.Add(daily);
            SetProp(daily, "StartBoundary", "2026-01-01T08:30:00");
            SetProp(daily, "DaysInterval", (short)1);
            SetProp(daily, "Enabled", false);

            actions = WindowsScheduledTask.GetComProperty<object>(definition, "Actions")!;
            action = WindowsScheduledTask.InvokeCom(actions, "Create", 0)!;
            SetProp(action, "Path", exePath);

            registered = WindowsScheduledTask.InvokeCom(
                root,
                "RegisterTaskDefinition",
                taskName,
                definition,
                6,
                "",
                "",
                3
            );
        }
        finally
        {
            WindowsScheduledTask.ReleaseCom(registered);
            WindowsScheduledTask.ReleaseCom(action);
            WindowsScheduledTask.ReleaseCom(actions);
            foreach (var trigger in createdTriggers)
                WindowsScheduledTask.ReleaseCom(trigger);
            WindowsScheduledTask.ReleaseCom(triggers);
            WindowsScheduledTask.ReleaseCom(definition);
            WindowsScheduledTask.ReleaseCom(root);
            WindowsScheduledTask.ReleaseCom(service);
        }
    }

    private static void SetLanguage(string language) =>
        Dispatcher.UIThread.Invoke(() => Localizer.Language = language);

    private static T SetLanguageAndRead<T>(string language, Func<T> reader) =>
        Dispatcher.UIThread.Invoke(() =>
        {
            Localizer.Language = language;
            return reader();
        });

    private static void SetProp(object target, string propertyName, object value)
    {
        target.GetType().InvokeMember(
            propertyName,
            BindingFlags.SetProperty,
            binder: null,
            target: target,
            args: [value]
        );
    }
}
