using System.Diagnostics;
using System.Text;
using JeekTools;

namespace JeekWindowsOptimizer.Mcp;

internal static class AutoUpdateProbe
{
    public static async Task<string> RunAsync()
    {
        var results = new List<string>();
        VerifyRuntimeLayout(results);
        await VerifySingleInstanceAsync(results).ConfigureAwait(false);
        await VerifyAutoUpdateScriptAsync(results).ConfigureAwait(false);
        return "PASS\n" + string.Join('\n', results.Select(r => "  - " + r));
    }

    private static void VerifyRuntimeLayout(List<string> results)
    {
        var programDir = AppPaths.ProgramDir;
        var runtimeDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var expectedRuntimeDir = Path.Combine(programDir, "Runtime");

        Require(
            string.Equals(runtimeDir, expectedRuntimeDir, StringComparison.OrdinalIgnoreCase),
            $"Expected RuntimeDir '{expectedRuntimeDir}', got '{runtimeDir}'."
        );
        Require(
            string.Equals(
                Path.GetDirectoryName(Environment.ProcessPath ?? ""),
                programDir,
                StringComparison.OrdinalIgnoreCase),
            $"Expected ExecutablePath inside '{programDir}', got '{Environment.ProcessPath}'."
        );
        Require(File.Exists(Path.Combine(programDir, "JeekWindowsOptimizer.exe")), "JeekWindowsOptimizer.exe missing from ProgramDir.");
        Require(File.Exists(Path.Combine(runtimeDir, "JeekWindowsOptimizer.dll")), "JeekWindowsOptimizer.dll missing from RuntimeDir.");
        Require(File.Exists(Path.Combine(runtimeDir, "JeekWindowsOptimizerMcp.exe")), "JeekWindowsOptimizerMcp.exe missing from RuntimeDir.");
        Require(!File.Exists(Path.Combine(runtimeDir, "JeekWindowsOptimizer.exe")), "Duplicate JeekWindowsOptimizer.exe should not exist in RuntimeDir.");
        Require(!Directory.Exists(Path.Combine(programDir, "Libs")), "Legacy NetBeauty Libs directory should not exist in ProgramDir.");

        var strayRootBinaries = Directory.EnumerateFiles(programDir)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name)
                && !string.Equals(name, "JeekWindowsOptimizer.exe", StringComparison.OrdinalIgnoreCase)
                && (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        Require(
            strayRootBinaries.Length == 0,
            $"Unexpected binary/runtime files in ProgramDir: {string.Join(", ", strayRootBinaries)}"
        );

        results.Add($"nested Runtime layout verified (ProgramDir={programDir}, RuntimeDir={runtimeDir})");
    }

    private static async Task VerifySingleInstanceAsync(List<string> results)
    {
        var primary = App.SingleInstance;
        Require(primary is { IsPrimary: true }, "Current running app must hold the primary AppSingleInstance mutex.");

        // Same directory must be detected as a duplicate and deliver activation via IPC.
        var beforeCount = App.ActivationRequestCount;
        using (var duplicate = AppSingleInstance.Acquire(AppPaths.ProgramDir))
        {
            Require(!duplicate.IsPrimary, "Second acquire for the same app directory must not become primary.");
            Require(duplicate.NotifyPrimaryInstance(2000), "Duplicate NotifyPrimaryInstance() must succeed.");
        }

        await WaitForConditionAsync(
            () => App.ActivationRequestCount > beforeCount,
            TimeSpan.FromSeconds(3),
            "Primary instance did not receive in-process IPC activation signal."
        ).ConfigureAwait(false);
        results.Add($"in-process duplicate detection + IPC activation (count {beforeCount} -> {App.ActivationRequestCount})");

        // Different directory must be independent so parallel worktrees/installs coexist.
        var isolatedDir = Path.Combine(Path.GetTempPath(), $"JwoSingleInstanceProbe_{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolatedDir);
        try
        {
            var isolatedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var otherPrimary = AppSingleInstance.Acquire(isolatedDir))
            {
                Require(otherPrimary.IsPrimary, "Different app directory must acquire its own primary mutex.");
                otherPrimary.StartActivationListener(() => isolatedSignal.TrySetResult(true));

                using var otherDuplicate = AppSingleInstance.Acquire(isolatedDir);
                Require(!otherDuplicate.IsPrimary, "Second acquire for the isolated directory must not become primary.");
                Require(otherDuplicate.NotifyPrimaryInstance(2000), "Isolated duplicate IPC signal must succeed.");

                var signaled = await Task.WhenAny(isolatedSignal.Task, Task.Delay(3000)).ConfigureAwait(false);
                Require(signaled == isolatedSignal.Task, "Isolated primary listener did not receive activation signal.");
            }

            // Re-acquiring after disposal must succeed cleanly.
            using (var reacquired = AppSingleInstance.Acquire(isolatedDir))
            {
                Require(reacquired.IsPrimary, "Re-acquiring after primary disposal must succeed.");
            }

            results.Add("per-directory isolation + clean mutex release/re-acquire");
        }
        finally
        {
            TryDeleteDirectory(isolatedDir);
        }

        // End-to-end process launch: spawning a second copy of this exe must activate us and exit 0.
        var exePath = Environment.ProcessPath;
        Require(!string.IsNullOrEmpty(exePath) && File.Exists(exePath), "Environment.ProcessPath must point to the running exe.");

        var countBeforeSpawn = App.ActivationRequestCount;
        using var child = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = AppPaths.ProgramDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to launch duplicate process.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await child.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { child.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("Spawned duplicate JeekWindowsOptimizer.exe did not exit within 10s.");
        }

        Require(child.ExitCode == 0, $"Duplicate process exited with code {child.ExitCode}, expected 0.");
        await WaitForConditionAsync(
            () => App.ActivationRequestCount > countBeforeSpawn,
            TimeSpan.FromSeconds(3),
            "Primary instance did not receive activation from spawned duplicate process."
        ).ConfigureAwait(false);
        results.Add($"real duplicate exe exited with code 0 and signaled primary (count {countBeforeSpawn} -> {App.ActivationRequestCount})");
    }

    private static async Task VerifyAutoUpdateScriptAsync(List<string> results)
    {
        var scriptPath = Path.Combine(AppPaths.ProgramDir, "AutoUpdate.ps1");
        Require(File.Exists(scriptPath), $"AutoUpdate.ps1 not found at {scriptPath}");

        var pingSource = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        Require(File.Exists(pingSource), $"PING.EXE not found at {pingSource}");

        var tempRoot = Path.Combine(Path.GetTempPath(), $"JwoAutoUpdateProbe_{Guid.NewGuid():N}");
        var installDir = Path.Combine(tempRoot, "Install");
        var otherDir = Path.Combine(tempRoot, "OtherInstall");
        var updateRoot = Path.Combine(tempRoot, "Update");
        var stageDir = Path.Combine(updateRoot, "package");

        Process? installProc = null;
        Process? otherProc = null;

        try
        {
            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(otherDir);
            Directory.CreateDirectory(stageDir);

            // Prepare fake install directory with preserved & replaceable files.
            File.Copy(scriptPath, Path.Combine(installDir, "AutoUpdate.ps1"), overwrite: true);
            File.Copy(pingSource, Path.Combine(installDir, "JeekWindowsOptimizer.exe"), overwrite: true);
            File.Copy(pingSource, Path.Combine(otherDir, "JeekWindowsOptimizer.exe"), overwrite: true);

            Directory.CreateDirectory(Path.Combine(installDir, "Runtime"));
            File.WriteAllText(Path.Combine(installDir, "Runtime", "old.dll"), "stale-runtime");
            Directory.CreateDirectory(Path.Combine(installDir, "Config"));
            File.WriteAllText(Path.Combine(installDir, "Config", "user.json"), "keep-config");
            Directory.CreateDirectory(Path.Combine(installDir, "Logs"));
            File.WriteAllText(Path.Combine(installDir, "Logs", "app.log"), "keep-log");
            Directory.CreateDirectory(Path.Combine(installDir, "Tools", "Activator"));
            File.WriteAllText(Path.Combine(installDir, "Tools", "Activator", "Run.cmd"), "keep-activator");
            Directory.CreateDirectory(Path.Combine(installDir, "Tools", "StaleTool"));
            File.WriteAllText(Path.Combine(installDir, "Tools", "StaleTool", "stale.txt"), "remove-me");
            File.WriteAllText(Path.Combine(installDir, "obsolete.txt"), "remove-me");

            // Prepare staged update package.
            File.WriteAllText(Path.Combine(stageDir, "JeekWindowsOptimizer.exe"), "updated-exe-payload");
            Directory.CreateDirectory(Path.Combine(stageDir, "Runtime"));
            Directory.CreateDirectory(Path.Combine(stageDir, "Data"));
            foreach (var file in new[] { @"Runtime\JeekWindowsOptimizer.dll", @"Runtime\JeekWindowsOptimizer.runtimeconfig.json",
                @"Runtime\JeekWindowsOptimizerMcp.exe", @"Data\Languages.tab" })
                File.WriteAllText(Path.Combine(stageDir, file), "fixture");
            File.WriteAllText(Path.Combine(stageDir, "Runtime", "new.dll"), "v-next-runtime");
            File.WriteAllText(Path.Combine(stageDir, "payload.txt"), "v-next");
            Directory.CreateDirectory(Path.Combine(stageDir, "Tools", "NewTool"));
            File.WriteAllText(Path.Combine(stageDir, "Tools", "NewTool", "tool.txt"), "new-tool");
            File.Copy(scriptPath, Path.Combine(stageDir, "AutoUpdate.ps1"), overwrite: true);
            File.WriteAllText(Path.Combine(updateRoot, "version.txt"), "999");
            File.WriteAllText(Path.Combine(updateRoot, "unrelated.txt"), "keep-sibling");

            // Start one blocking process inside InstallDir and one decoy process in OtherInstall.
            // Meanwhile this real Debug JeekWindowsOptimizer.exe is also running in AppPaths.ProgramDir!
            installProc = StartSleepProcess(Path.Combine(installDir, "JeekWindowsOptimizer.exe"), installDir);
            otherProc = StartSleepProcess(Path.Combine(otherDir, "JeekWindowsOptimizer.exe"), otherDir);

            // Run the copied AutoUpdate.ps1 inside InstallDir WITHOUT passing -InstallDir
            // (and with a different WorkingDirectory) to match AutoUpdater.LaunchInstall
            // and verify $PSScriptRoot resolution in Windows PowerShell 5.1.
            var installedScriptPath = Path.Combine(installDir, "AutoUpdate.ps1");
            using var updater = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    WorkingDirectory = tempRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
            };
            updater.StartInfo.ArgumentList.Add("-NoProfile");
            updater.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            updater.StartInfo.ArgumentList.Add("Bypass");
            updater.StartInfo.ArgumentList.Add("-File");
            updater.StartInfo.ArgumentList.Add(installedScriptPath);
            updater.StartInfo.ArgumentList.Add(stageDir);
            updater.StartInfo.ArgumentList.Add("-GracefulWaitSeconds");
            updater.StartInfo.ArgumentList.Add("1");
            updater.StartInfo.ArgumentList.Add("-CloseWaitSeconds");
            updater.StartInfo.ArgumentList.Add("1");
            updater.StartInfo.ArgumentList.Add("-NoRestart");

            updater.Start();
            var stdoutTask = updater.StandardOutput.ReadToEndAsync();
            var stderrTask = updater.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                await updater.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { updater.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("AutoUpdate.ps1 timed out (25s) — likely blocked on an out-of-directory process.");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            Require(
                updater.ExitCode == 0,
                $"AutoUpdate.ps1 failed with exit code {updater.ExitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}"
            );
            Require(installProc.HasExited, $"Process in InstallDir (PID {installProc.Id}) was not stopped.");
            Require(!otherProc.HasExited, $"Out-of-directory process (PID {otherProc.Id}) must NOT be stopped.");
            Require(
                stdout.Contains($"PID {installProc.Id}", StringComparison.Ordinal),
                $"Updater output should report blocking PID {installProc.Id}. Output:\n{stdout}"
            );
            Require(
                !stdout.Contains($"PID {otherProc.Id}", StringComparison.Ordinal)
                    && !stdout.Contains($"PID {Environment.ProcessId}", StringComparison.Ordinal),
                $"Updater output must not wait on out-of-directory PIDs. Output:\n{stdout}"
            );

            // Verify file replacement, preservation, and staging cleanup.
            Require(File.ReadAllText(Path.Combine(installDir, "JeekWindowsOptimizer.exe")) == "updated-exe-payload", "Updated exe was not copied.");
            Require(File.ReadAllText(Path.Combine(installDir, "Runtime", "new.dll")) == "v-next-runtime", "Updated Runtime payload was not copied.");
            Require(!File.Exists(Path.Combine(installDir, "Runtime", "old.dll")), "Stale Runtime file should be removed.");
            Require(File.ReadAllText(Path.Combine(installDir, "payload.txt")) == "v-next", "New payload file was not copied.");
            Require(!File.Exists(Path.Combine(installDir, "obsolete.txt")), "Obsolete file should be removed.");
            Require(!Directory.Exists(Path.Combine(installDir, "Tools", "StaleTool")), "Non-Activator tool folder should be removed.");
            Require(File.ReadAllText(Path.Combine(installDir, "Config", "user.json")) == "keep-config", "Config must be preserved.");
            Require(File.ReadAllText(Path.Combine(installDir, "Logs", "app.log")) == "keep-log", "Logs must be preserved.");
            Require(File.ReadAllText(Path.Combine(installDir, "Tools", "Activator", "Run.cmd")) == "keep-activator", "Tools\\Activator must be preserved.");
            Require(File.ReadAllText(Path.Combine(installDir, "Tools", "NewTool", "tool.txt")) == "new-tool", "New tool folder should be copied into Tools.");
            Require(File.Exists(Path.Combine(installDir, "AutoUpdate.ps1")), "AutoUpdate.ps1 must exist after update.");
            Require(!Directory.Exists(stageDir) && !File.Exists(Path.Combine(updateRoot, "version.txt")), "Staged package and version must be removed.");
            Require(File.ReadAllText(Path.Combine(updateRoot, "unrelated.txt")) == "keep-sibling", "Unrelated update-root files must be preserved.");

            results.Add($"AutoUpdate.ps1 filtered to InstallDir (stopped PID {installProc.Id}, ignored other-dir PID {otherProc.Id} & live Debug PID {Environment.ProcessId}), swapped files, preserved user data");
        }
        finally
        {
            TryKill(installProc);
            TryKill(otherProc);
            TryDeleteDirectory(tempRoot);
        }
    }

    private static Process StartSleepProcess(string exePath, string workingDirectory)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "127.0.0.1 -n 60",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException($"Failed to start fixture process {exePath}");
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout, string message)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!predicate())
        {
            if (Environment.TickCount64 >= deadline)
                throw new InvalidOperationException(message);
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void TryKill(Process? proc)
    {
        if (proc is null)
            return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        catch { }
        finally
        {
            proc.Dispose();
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch
            {
                Thread.Sleep(150);
            }
        }
    }
}
