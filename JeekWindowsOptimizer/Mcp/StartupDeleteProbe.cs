using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;
using Jeek.Avalonia.Localization;
using JeekWindowsOptimizer.Startup;
using Microsoft.Win32;

namespace JeekWindowsOptimizer.Mcp;

/// <summary>
///     End-to-end regression checks for deleting orphaned startup entries across all mechanisms
///     (Run, RunOnce, Startup folder, Scheduled Task, Service/Driver, Explorer/IE extensions) plus
///     the existing-file safety guard and ViewModel state transitions.
///     <para>
///         Every fixture uses a unique temporary name and is cleaned up in a finally block; no real
///         user startup entry or decision ledger entry is modified.
///     </para>
/// </summary>
internal static class StartupDeleteProbe
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";
    private const string ExplorerPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string UserBlockedPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

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
            RegistryRunAndRunOnceScenario(token, Check);
            ExistingFileSafetyScenario(token, Check);
            StartupFolderScenario(token, Check);
            ScheduledTaskScenario(token, Check);
            ServiceAndBootDriverScenario(token, Check);
            ShellExtensionScenario(token, Check);
        });

        await ViewModelScenarioAsync(Check);

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
            IsEnabled = entry.IsEnabled,
        };
    }

    private static void RegistryRunAndRunOnceScenario(string token, CheckFn check)
    {
        var runName = $"JeekDeleteProbeRun_{token}";
        var runOnceName = $"JeekDeleteProbeRunOnce_{token}";
        var missingExe = $@"C:\JeekDeleteProbeMissing_{token}\missing.exe";

        try
        {
            using (var runKey = Registry.CurrentUser.CreateSubKey(RunPath, writable: true))
                runKey!.SetValue(runName, $"\"{missingExe}\" --silent", RegistryValueKind.String);
            using (var approvedKey = Registry.CurrentUser.CreateSubKey($@"{ApprovedRoot}\Run", writable: true))
                approvedKey!.SetValue(runName, new byte[12], RegistryValueKind.Binary);
            using (var runOnceKey = Registry.CurrentUser.CreateSubKey(RunOncePath, writable: true))
                runOnceKey!.SetValue(runOnceName, missingExe, RegistryValueKind.String);

            var scanned = StartupRegistryScanner.Scan();
            var runRaw = scanned.FirstOrDefault(e => e.Kind == StartupItemKind.LogonRegistry && e.Name == runName);
            var runOnceRaw = scanned.FirstOrDefault(e => e.Kind == StartupItemKind.RunOnce && e.Name == runOnceName);

            check("orphaned HKCU Run entry is scanned and deletable",
                runRaw is not null && Materialize(runRaw) is { IsOrphaned: true, CanDelete: true });
            check("orphaned HKCU RunOnce entry is read-only to toggle yet deletable",
                runOnceRaw is not null && Materialize(runOnceRaw) is { CanToggle: false, IsOrphaned: true, CanDelete: true });

            if (runRaw is not null)
            {
                var result = StartupToggle.Delete(Materialize(runRaw));
                using var runKey = Registry.CurrentUser.OpenSubKey(RunPath);
                using var approvedKey = Registry.CurrentUser.OpenSubKey($@"{ApprovedRoot}\Run");
                check("deleting orphaned Run entry removes both Run and StartupApproved values",
                    result.Succeeded
                    && runKey?.GetValue(runName) is null
                    && approvedKey?.GetValue(runName) is null,
                    result.Error ?? "");
            }

            if (runOnceRaw is not null)
            {
                var result = StartupToggle.Delete(Materialize(runOnceRaw));
                using var runOnceKey = Registry.CurrentUser.OpenSubKey(RunOncePath);
                check("deleting orphaned RunOnce entry removes the registry value",
                    result.Succeeded && runOnceKey?.GetValue(runOnceName) is null,
                    result.Error ?? "");
            }
        }
        finally
        {
            DeleteRegistryValueQuietly(Registry.CurrentUser, RunPath, runName);
            DeleteRegistryValueQuietly(Registry.CurrentUser, $@"{ApprovedRoot}\Run", runName);
            DeleteRegistryValueQuietly(Registry.CurrentUser, RunOncePath, runOnceName);
        }
    }

    private static void ExistingFileSafetyScenario(string token, CheckFn check)
    {
        var valueName = $"JeekDeleteProbeGuard_{token}";
        var tempFile = Path.Combine(Path.GetTempPath(), $"JeekDeleteProbe_{token}.tmp");

        try
        {
            File.WriteAllText(tempFile, "probe");
            using (var runKey = Registry.CurrentUser.CreateSubKey(RunPath, writable: true))
                runKey!.SetValue(valueName, $"\"{tempFile}\"", RegistryValueKind.String);

            var raw = StartupRegistryScanner.Scan()
                .FirstOrDefault(e => e.Kind == StartupItemKind.LogonRegistry && e.Name == valueName);
            var existingItem = raw is not null ? Materialize(raw) : null;

            check("an entry whose target file exists is not deletable",
                existingItem is { IsOrphaned: false, CanDelete: false });

            if (existingItem is not null)
            {
                var refused = StartupToggle.Delete(existingItem);
                using var runKey = Registry.CurrentUser.OpenSubKey(RunPath);
                check("delete refuses an entry whose target file exists",
                    !refused.Succeeded && runKey?.GetValue(valueName) is not null);
            }

            // Simulate a file that was missing at scan time and reappeared before Delete ran.
            File.Delete(tempFile);
            var staleOrphan = raw is not null ? Materialize(raw) : null;
            check("stale scan sees missing file as deletable",
                staleOrphan is { IsOrphaned: true, CanDelete: true });

            File.WriteAllText(tempFile, "reappeared");
            if (staleOrphan is not null)
            {
                var raceResult = StartupToggle.Delete(staleOrphan);
                using var runKey = Registry.CurrentUser.OpenSubKey(RunPath);
                check("delete re-checks disk and aborts if the file reappeared",
                    !raceResult.Succeeded
                    && raceResult.Error == Localizer.Get("StartupDeleteFileExistsNow")
                    && runKey?.GetValue(valueName) is not null,
                    raceResult.Error ?? "");
            }
        }
        finally
        {
            DeleteRegistryValueQuietly(Registry.CurrentUser, RunPath, valueName);
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    private static void StartupFolderScenario(string token, CheckFn check)
    {
        var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var fileName = $"JeekDeleteProbe_{token}.lnk";
        var lnkPath = Path.Combine(startupDir, fileName);
        var missingExe = $@"C:\JeekDeleteProbeMissing_{token}\missing.exe";

        try
        {
            CreateShortcut(lnkPath, missingExe);
            using (var approved = Registry.CurrentUser.CreateSubKey($@"{ApprovedRoot}\StartupFolder", writable: true))
                approved!.SetValue(fileName, new byte[12], RegistryValueKind.Binary);

            var raw = StartupFolderScanner.Scan().FirstOrDefault(e => e.Name == fileName);
            var item = raw is not null ? Materialize(raw) : null;
            check("broken Startup folder shortcut is scanned as orphaned and deletable",
                item is { IsOrphaned: true, CanDelete: true },
                item?.ImagePath ?? "(not found)");

            if (item is not null)
            {
                var result = StartupToggle.Delete(item);
                using var approved = Registry.CurrentUser.OpenSubKey($@"{ApprovedRoot}\StartupFolder");
                check("deleting broken Startup folder shortcut removes file and StartupApproved value",
                    result.Succeeded && !File.Exists(lnkPath) && approved?.GetValue(fileName) is null,
                    result.Error ?? "");
            }
        }
        finally
        {
            try { if (File.Exists(lnkPath)) File.Delete(lnkPath); } catch { }
            DeleteRegistryValueQuietly(Registry.CurrentUser, $@"{ApprovedRoot}\StartupFolder", fileName);
        }
    }

    private static void ScheduledTaskScenario(string token, CheckFn check)
    {
        var taskName = $"JeekDeleteProbe_{token}";
        var taskPath = "\\" + taskName;
        var missingExe = $@"C:\JeekDeleteProbeMissing_{token}\missing.exe";

        try
        {
            CreateLogonTask(taskName, missingExe);
            var raw = ScheduledTaskScanner.Scan()
                .FirstOrDefault(e => string.Equals(e.Name, taskPath, StringComparison.OrdinalIgnoreCase));
            var item = raw is not null ? Materialize(raw) : null;

            check("orphaned scheduled task is scanned as deletable",
                item is { IsOrphaned: true, CanDelete: true },
                item?.ImagePath ?? "(not found)");

            if (item is not null)
            {
                var result = StartupToggle.Delete(item);
                check("deleting orphaned scheduled task removes it from Task Scheduler",
                    result.Succeeded && !WindowsScheduledTask.Exists(taskPath),
                    result.Error ?? "");
            }
        }
        finally
        {
            if (WindowsScheduledTask.Exists(taskPath))
                WindowsScheduledTask.TryDelete(taskPath);
        }
    }

    private static void ServiceAndBootDriverScenario(string token, CheckFn check)
    {
        var svcName = $"JeekDeleteProbeSvc_{token}";
        var bootName = $"JeekDeleteProbeBoot_{token}";
        var missingExe = $@"C:\JeekDeleteProbeMissing_{token}\missing.exe";
        var missingSys = $@"C:\JeekDeleteProbeMissing_{token}\missing.sys";

        try
        {
            using (var svcKey = Registry.LocalMachine.CreateSubKey($@"{ServicesPath}\{svcName}", writable: true))
            {
                svcKey!.SetValue("Type", 0x10, RegistryValueKind.DWord);
                svcKey.SetValue("Start", 3, RegistryValueKind.DWord);
                svcKey.SetValue("ImagePath", missingExe, RegistryValueKind.ExpandString);
            }
            StartupLocalState.RememberServiceStart(svcName, 2);

            using (var bootKey = Registry.LocalMachine.CreateSubKey($@"{ServicesPath}\{bootName}", writable: true))
            {
                bootKey!.SetValue("Type", 1, RegistryValueKind.DWord);
                bootKey.SetValue("Start", 0, RegistryValueKind.DWord);
                bootKey.SetValue("ImagePath", missingSys, RegistryValueKind.ExpandString);
            }

            var scanned = ServiceRegistryScanner.Scan();
            var svcRaw = scanned.FirstOrDefault(e => e.Name == svcName);
            var bootRaw = scanned.FirstOrDefault(e => e.Name == bootName);
            var svcItem = svcRaw is not null ? Materialize(svcRaw) : null;
            var bootItem = bootRaw is not null ? Materialize(bootRaw) : null;

            check("orphaned service is scanned as deletable",
                svcItem is { IsOrphaned: true, CanDelete: true });
            check("boot-critical driver stays non-deletable even when orphaned",
                bootItem is { IsOrphaned: true, CanToggle: false, CanDelete: false });

            if (svcItem is not null)
            {
                var result = StartupToggle.Delete(svcItem);
                using var probe = Registry.LocalMachine.OpenSubKey($@"{ServicesPath}\{svcName}");
                check("deleting orphaned service removes its registry key and forgets saved start mode",
                    result.Succeeded
                    && probe is null
                    && StartupLocalState.RecallServiceStart(svcName) == 3,
                    result.Error ?? "");
            }

            if (bootItem is not null)
            {
                check("live guard rejects changing a boot driver", !CanChangeStart(bootItem));
                var refused = StartupToggle.Delete(bootItem);
                using var probe = Registry.LocalMachine.OpenSubKey($@"{ServicesPath}\{bootName}");
                check("delete refuses a boot-critical driver and leaves its key intact",
                    !refused.Succeeded && probe is not null);
            }
        }
        finally
        {
            StartupLocalState.ForgetServiceStart(svcName);
            try { Registry.LocalMachine.DeleteSubKeyTree($@"{ServicesPath}\{svcName}", false); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree($@"{ServicesPath}\{bootName}", false); } catch { }
        }
    }

    private static bool CanChangeStart(StartupItem item)
    {
        try { ServiceRegistryScanner.ReadMutableStart(item); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static void ShellExtensionScenario(string token, CheckFn check)
    {
        var clsid = Guid.NewGuid().ToString("B").ToUpperInvariant();
        var missingDll = $@"C:\JeekDeleteProbeMissing_{token}\missing.dll";
        var clsidKey = $@"Software\Classes\CLSID\{clsid}";
        var explorerHook = $@"{ExplorerPath}\ShellServiceObjects\{clsid}";
        var bhoHook = $@"{ExplorerPath}\Browser Helper Objects\{clsid}";

        try
        {
            using (var server = Registry.CurrentUser.CreateSubKey($@"{clsidKey}\InProcServer32", writable: true))
                server!.SetValue("", missingDll, RegistryValueKind.String);
            using (Registry.CurrentUser.CreateSubKey(explorerHook, writable: true)) { }
            using (Registry.CurrentUser.CreateSubKey(bhoHook, writable: true)) { }
            using (var blocked = Registry.CurrentUser.CreateSubKey(UserBlockedPath, writable: true))
                blocked!.SetValue(clsid, "", RegistryValueKind.String);

            var scanned = ShellExtensionScanner.Scan();
            var explorerRaw = scanned.FirstOrDefault(e =>
                e.Kind == StartupItemKind.ExplorerExtension
                && e.Handles.Contains(clsid, StringComparer.OrdinalIgnoreCase));
            var ieRaw = scanned.FirstOrDefault(e =>
                e.Kind == StartupItemKind.InternetExplorerExtension
                && e.Handles.Contains(clsid, StringComparer.OrdinalIgnoreCase));

            var explorerItem = explorerRaw is not null ? Materialize(explorerRaw) : null;
            var ieItem = ieRaw is not null ? Materialize(ieRaw) : null;

            check("orphaned Explorer and IE extensions are scanned as deletable",
                explorerItem is { IsOrphaned: true, CanDelete: true }
                && ieItem is { IsOrphaned: true, CanDelete: true });

            if (explorerItem is not null && ieItem is not null)
            {
                var expResult = StartupToggle.Delete(explorerItem);
                var ieResult = StartupToggle.Delete(ieItem);

                using var expProbe = Registry.CurrentUser.OpenSubKey(explorerHook);
                using var bhoProbe = Registry.CurrentUser.OpenSubKey(bhoHook);
                using var blockedProbe = Registry.CurrentUser.OpenSubKey(UserBlockedPath);

                var rescan = ShellExtensionScanner.Scan();
                var stillPresent = rescan.Any(e => e.Handles.Contains(clsid, StringComparer.OrdinalIgnoreCase));

                check("deleting orphaned Explorer and IE extensions removes their hooks and blocked entry",
                    expResult.Succeeded
                    && ieResult.Succeeded
                    && expProbe is null
                    && bhoProbe is null
                    && blockedProbe?.GetValue(clsid) is null
                    && !stillPresent,
                    $"{expResult.Error} {ieResult.Error}".Trim());
            }
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(explorerHook, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(bhoHook, false); } catch { }
            DeleteRegistryValueQuietly(Registry.CurrentUser, UserBlockedPath, clsid);
            try { Registry.CurrentUser.DeleteSubKeyTree(clsidKey, false); } catch { }
        }
    }

    private static async Task ViewModelScenarioAsync(CheckFn check)
    {
        using var release = new ManualResetEventSlim();
        var failNext = 0;
        var uiThreadCalls = 0;

        using var vm = new MainViewModel
        {
            StartupDeleteHandler = _ =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                    Interlocked.Increment(ref uiThreadCalls);
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    return StartupDeleteResult.Fail("Probe timed out.");
                return Interlocked.Exchange(ref failNext, 0) == 1
                    ? StartupDeleteResult.Fail("Simulated delete failure")
                    : StartupDeleteResult.Ok();
            },
        };

        var orphan = new StartupItem
        {
            Kind = StartupItemKind.LogonRegistry,
            Key = "DeleteProbe|orphan",
            Name = "OrphanApp",
            Location = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
            ImagePath = @"C:\Missing\orphan.exe",
            IsOrphaned = true,
            DeleteTarget = new RegistryValueDeleteTarget(
                RegistryHive.CurrentUser,
                RegistryView.Registry64,
                RunPath,
                "OrphanApp",
                "Run"
            ),
            IsEnabled = true,
        };
        var healthy = new StartupItem
        {
            Kind = StartupItemKind.LogonRegistry,
            Key = "DeleteProbe|healthy",
            Name = "HealthyApp",
            Location = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
            ImagePath = @"C:\Windows\System32\cmd.exe",
            IsOrphaned = false,
            DeleteTarget = new RegistryValueDeleteTarget(
                RegistryHive.CurrentUser,
                RegistryView.Registry64,
                RunPath,
                "HealthyApp",
                "Run"
            ),
            IsEnabled = true,
        };

        vm.AllStartupGroups.Add(new StartupGroup(StartupItemKind.LogonRegistry, [orphan, healthy]));

        Task work = Task.CompletedTask;
        try
        {
            // Non-orphaned item must be ignored.
            var healthyDeleted = await vm.DeleteStartupItemAsync(healthy, confirm: false);
            check("ViewModel refuses deleting a non-orphaned item", !healthyDeleted && vm.StartupItems.Count() == 2);

            // In-flight delete state & failure path.
            failNext = 1;
            work = vm.DeleteStartupItemAsync(orphan, confirm: false);
            check("deleting row shows deleting status and disables actions while in flight",
                orphan.IsDeleting
                && orphan.IsApplying
                && !orphan.CanDeleteNow
                && !orphan.CanChoose
                && orphan.StatusKey == "StartupStatusDeleting"
                && !vm.ScanStartupCommand.CanExecute(null));

            release.Set();
            var failedResult = await ((Task<bool>)work).WaitAsync(TimeSpan.FromSeconds(15));
            check("failed delete keeps item, reports formatted error, and restores controls",
                !failedResult
                && !orphan.IsDeleting
                && orphan.CanDeleteNow
                && orphan.ErrorMessage == "Simulated delete failure"
                && orphan.StatusText == string.Format(Localizer.Get("StartupDeleteFailed"), "Simulated delete failure")
                && vm.StartupItems.Contains(orphan));

            // Success path removes item and updates group & summary counts.
            var okResult = await vm.DeleteStartupItemAsync(orphan, confirm: false);
            check("successful delete removes item off UI thread and updates counts",
                okResult
                && uiThreadCalls == 0
                && !vm.StartupItems.Contains(orphan)
                && vm.AllStartupGroups.Count == 1
                && vm.AllStartupGroups[0].ItemCount == 1
                && vm.StartupTotalCount == 1);

            // Deleting the last item in a group removes the empty group too.
            var solo = new StartupItem
            {
                Kind = StartupItemKind.RunOnce,
                Key = "DeleteProbe|solo",
                Name = "SoloRunOnce",
                Location = @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce",
                ImagePath = @"C:\Missing\solo.exe",
                IsOrphaned = true,
                CanToggle = false,
                DeleteTarget = new RegistryValueDeleteTarget(
                    RegistryHive.CurrentUser,
                    RegistryView.Registry64,
                    RunOncePath,
                    "SoloRunOnce",
                    null
                ),
                IsEnabled = true,
            };
            vm.AllStartupGroups.Add(new StartupGroup(StartupItemKind.RunOnce, [solo]));
            var soloResult = await vm.DeleteStartupItemAsync(solo, confirm: false);
            check("deleting the last item in a group removes the empty group",
                soloResult
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

    private static void CreateShortcut(string lnkPath, string targetPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell ProgID not found.");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)!;
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                [lnkPath]
            )!;
            shortcut.GetType().InvokeMember(
                "TargetPath",
                BindingFlags.SetProperty,
                null,
                shortcut,
                [targetPath]
            );
            shortcut.GetType().InvokeMember(
                "Save",
                BindingFlags.InvokeMethod,
                null,
                shortcut,
                null
            );
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
                Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void CreateLogonTask(string taskName, string exePath)
    {
        var serviceType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Schedule.Service ProgID not found.");
        object? service = null;
        object? root = null;
        object? definition = null;
        object? triggers = null;
        object? trigger = null;
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
            trigger = WindowsScheduledTask.InvokeCom(triggers, "Create", 9)!; // TASK_TRIGGER_LOGON

            actions = WindowsScheduledTask.GetComProperty<object>(definition, "Actions")!;
            action = WindowsScheduledTask.InvokeCom(actions, "Create", 0)!; // TASK_ACTION_EXEC
            action.GetType().InvokeMember("Path", BindingFlags.SetProperty, null, action, [exePath]);

            // TASK_CREATE_OR_UPDATE = 6, TASK_LOGON_INTERACTIVE_TOKEN = 3.
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
            WindowsScheduledTask.ReleaseCom(trigger);
            WindowsScheduledTask.ReleaseCom(triggers);
            WindowsScheduledTask.ReleaseCom(definition);
            WindowsScheduledTask.ReleaseCom(root);
            WindowsScheduledTask.ReleaseCom(service);
        }
    }
}
