using JeekTools;

namespace JeekWindowsOptimizer.Mcp;

internal static class VirtualDiskMigrationProbe
{
    internal static async Task<string> RunNativeWslAsync()
    {
        await WslStorage.RequireMoveSupportAsync(default);
        var root = Path.Combine(Path.GetTempPath(), "JeekNativeWslProbe-" + Guid.NewGuid().ToString("N"));
        var name = "JeekMigrationProbe-" + Guid.NewGuid().ToString("N");
        var targetDrive = DiskSpaceItemManager.GetTargetDrives().FirstOrDefault(d => !d.IsSameDrive(root))
            ?? throw new IOException("Native probe needs two NTFS drives.");
        var originalDefault = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\" + WslStorage.RegistryPath, "DefaultDistribution", null);
        Directory.CreateDirectory(root);
        var archive = Path.Combine(root, "fixture.tar");
        var install = Path.Combine(root, "install");
        var registered = false;
        string? distroId = null;
        try
        {
            using (var file = File.Create(archive))
            using (var writer = new System.Formats.Tar.TarWriter(file))
            {
                var entry = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "jeek-fixture.txt")
                {
                    DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Jeek isolated migration fixture\n")),
                };
                writer.WriteEntry(entry);
            }
            var import = await WslStorage.RunAsync(["--import", name, install, archive, "--version", "2"], default);
            var distro = WslStorage.Discover().SingleOrDefault(d => d.Name == name);
            if (distro is not null) { registered = true; distroId = distro.Id; }
            Check(import.ExitCode == 0 && distro is not null, "native fixture import: " + import.Output);
            var item = new WslRelocationItem(distro!);
            await item.RefreshAsync();
            Check(await item.MoveAsync(targetDrive), "native move: " + item.ErrorMessage);
            Check(targetDrive.IsSameDrive(item.CurrentLocation), "native cross-volume registry update");
            var back = new DriveOption(Path.GetPathRoot(root)!, long.MaxValue, true);
            Check(await item.MoveAsync(back), "native return move: " + item.ErrorMessage);
            var exported = Path.Combine(root, "export.tar");
            var export = await WslStorage.RunAsync(["--export", name, exported], default);
            Check(export.ExitCode == 0, "native export verification: " + export.Output);
            using (var file = File.OpenRead(exported))
            using (var reader = new System.Formats.Tar.TarReader(file))
            {
                var found = false;
                while (reader.GetNextEntry() is { } entry)
                    if (entry.Name.TrimStart('.', '/') == "jeek-fixture.txt")
                    {
                        using var text = new StreamReader(entry.DataStream!);
                        found = text.ReadToEnd() == "Jeek isolated migration fixture\n";
                    }
                Check(found, "native round trip preserves filesystem payload");
            }
            Check(Equals(originalDefault, Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\" + WslStorage.RegistryPath, "DefaultDistribution", null)),
                "default user distribution unchanged");
            return "PASS native WSL: isolated tar import, cross-volume move, return move, registered path, exported filesystem payload, default distribution preserved";
        }
        finally
        {
            // Only unregister the random fixture created here, and only while its identity still matches.
            if (registered && WslStorage.Discover().Any(d => d.Name == name && d.Id == distroId))
            {
                var cleanup = await WslStorage.RunAsync(["--unregister", name], default);
                if (cleanup.ExitCode != 0) throw new IOException("Could not remove isolated WSL fixture " + name);
                registered = false;
            }
            if (!registered) Directory.Delete(root, true);
        }
    }

    internal static async Task<string> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "JeekMigrationProbe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var crossDriveRoot = Path.Combine(AppContext.BaseDirectory, ".migration-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(crossDriveRoot);
        var anchor = Path.Combine(root, "original");
        var first = Path.Combine(crossDriveRoot, "first");
        var second = Path.Combine(root, "second");
        var payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);
        Directory.CreateDirectory(anchor);
        await File.WriteAllBytesAsync(Path.Combine(anchor, DockerDiskStorage.DiskName), payload);
        var checks = new List<string>();
        try
        {
            await Reject(async () => await DockerDiskStorage.MoveAsync(anchor, anchor, first, false,
                () => throw new IOException("running fixture"), null, default));
            Check(!Directory.Exists(first), "running guard");
            using (var locked = File.Open(Path.Combine(anchor, DockerDiskStorage.DiskName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await Reject(() => Move(anchor, first));
            Check(!Directory.Exists(first), "locked disk preserves source");
            Directory.CreateDirectory(first);
            await File.WriteAllTextAsync(Path.Combine(first, "keep.txt"), "keep");
            await Reject(() => Move(anchor, first));
            Check(File.ReadAllText(Path.Combine(first, "keep.txt")) == "keep", "existing target preserved");
            File.Delete(Path.Combine(first, "keep.txt"));
            Directory.Delete(first);
            var guardCalls = 0;
            await Reject(() => DockerDiskStorage.MoveAsync(anchor, anchor, first, false, () =>
            {
                if (++guardCalls != 2) return;
                Directory.CreateDirectory(first);
                File.WriteAllText(Path.Combine(first, DockerDiskStorage.DiskName), "concurrent destination");
            }, null, default));
            Check(File.ReadAllText(Path.Combine(first, DockerDiskStorage.DiskName)) == "concurrent destination", "concurrent target never overwritten or deleted");
            File.Delete(Path.Combine(first, DockerDiskStorage.DiskName));
            Directory.Delete(first);
            await Reject(() => DockerDiskStorage.MoveAsync(anchor, anchor, first, false,
                () => { }, null, default, () => throw new IOException("injected copy-to-switch failure")));
            Check(!Directory.Exists(first) && File.Exists(Path.Combine(anchor, DockerDiskStorage.DiskName)), "pre-commit rollback");
            await Reject(() => DockerDiskStorage.MoveAsync(anchor, anchor, first, false,
                () => { }, null, default, afterAnchorMoved: () => throw new IOException("injected path switch failure")));
            Check(!Directory.Exists(first) && !Directory.Exists(anchor + ".jeek-backup")
                && File.Exists(Path.Combine(anchor, DockerDiskStorage.DiskName)), "path switch failure restores original directory");
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                try { await DockerDiskStorage.MoveAsync(anchor, anchor, first, false, () => { }, null, cts.Token); }
                catch (OperationCanceledException) { }
                Check(!Directory.Exists(first), "cancel cleans only its own copy");
            }
            await Move(anchor, first);
            Check(JunctionPoint.Exists(anchor) && DockerDiskStorage.Resolve(anchor) == first
                && (await File.ReadAllBytesAsync(Path.Combine(anchor, DockerDiskStorage.DiskName))).SequenceEqual(payload)
                && !Directory.Exists(anchor + ".jeek-backup"), "Docker move and byte verification");
            await Move(first, second);
            Check(!Directory.Exists(first) && DockerDiskStorage.Resolve(anchor) == second, "repeat move releases old drive");
            await DockerDiskStorage.MoveAsync(anchor, second, anchor, true, () => { }, null, default);
            Check(!JunctionPoint.Exists(anchor) && !Directory.Exists(second)
                && (await File.ReadAllBytesAsync(Path.Combine(anchor, DockerDiskStorage.DiskName))).SequenceEqual(payload), "restore removes junction and preserves bytes");
            var linked = Path.Combine(root, "linked");
            JunctionPoint.Create(linked, anchor, false);
            try { await Reject(() => Move(anchor, Path.Combine(linked, "nested"))); }
            finally { JunctionPoint.Delete(linked); }
            checks.Add("Docker: offline guard, locks, conflict and concurrent target, copy/switch failure rollback, cancellation, verified copy, repeat move, restore, redirected target rejection; crossVolume="
                + !string.Equals(Path.GetPathRoot(root), Path.GetPathRoot(crossDriveRoot), StringComparison.OrdinalIgnoreCase));

            var wslSource = Path.Combine(root, "wsl-original");
            Directory.CreateDirectory(wslSource);
            await File.WriteAllBytesAsync(Path.Combine(wslSource, "ext4.vhdx"), payload);
            var distro = new WslDistribution(Guid.NewGuid().ToString("B"), "Fixture distro with spaces", wslSource, 2, "ext4.vhdx");
            var calls = new List<string[]>();
            var failStop = false;
            var failMove = false;
            var wrongRegistration = false;
            var item = new WslRelocationItem(distro, () => distro, (args, _) =>
            {
                calls.Add(args);
                if (args[0] == "--terminate") return Task.FromResult((failStop ? 1 : 0, "fixture stop"));
                if (failMove) return Task.FromResult((1, "fixture move failure"));
                if (!wrongRegistration)
                {
                    Directory.Move(distro.Location, args[3]);
                    distro = distro with { Location = args[3] };
                }
                return Task.FromResult((0, ""));
            }, _ => Task.CompletedTask);
            var drive = new DriveOption(Path.Combine(root, "target"), long.MaxValue, true);
            await item.RefreshAsync();
            Check(!item.IsChecked && !item.CanRestoreDefault, "WSL opt-in and no invented default");
            failStop = true;
            Check(!await item.MoveAsync(drive) && calls.Count == 1 && Directory.Exists(wslSource), "stop failure blocks move");
            failStop = false;
            failMove = true;
            Check(!await item.MoveAsync(drive) && Directory.Exists(wslSource), "command failure preserves registration");
            failMove = false;
            wrongRegistration = true;
            Check(!await item.MoveAsync(drive), "success exit code is insufficient");
            wrongRegistration = false;
            Check(await item.MoveAsync(drive) && item.CurrentLocation == item.GetTargetPath(drive)
                && calls.Last().SequenceEqual(new[] { "--manage", distro.Name, "--move", item.GetTargetPath(drive) })
                && calls.All(c => !c.Contains("--unregister"))
                && (await File.ReadAllBytesAsync(Path.Combine(item.CurrentLocation, "ext4.vhdx"))).SequenceEqual(payload), "WSL arguments, registration, file and state verified");
            var unsupported = new WslRelocationItem(distro, () => distro,
                (_, _) => throw new Exception("must not execute"), _ => throw new IOException("unsupported WSL"));
            await unsupported.RefreshAsync();
            Check(!(await unsupported.CheckAsync(new DriveOption(Path.Combine(root, "unsupported"), long.MaxValue, true))).Succeeded,
                "unsupported WSL fails before mutation");
            checks.Add("WSL: stop failure, command failure, registration verification, spaced name arguments, intact payload, no unregister, unsupported version");
            return "PASS " + string.Join("; ", checks);
        }
        finally
        {
            // Fixture-owned tree only. Remove junctions explicitly before recursive cleanup.
            foreach (var entry in new[] { anchor, anchor + ".jeek-backup" })
                if (JunctionPoint.Exists(entry)) JunctionPoint.Delete(entry);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(crossDriveRoot)) Directory.Delete(crossDriveRoot, true);
        }

        Task Move(string source, string target) => DockerDiskStorage.MoveAsync(anchor, source, target, false, () => { }, null, default);
    }

    private static void Check(bool condition, string message) => DiskSpaceCleanupProbe.Require(condition, message);
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (IOException) { return; }
        throw new InvalidOperationException("Expected migration to be rejected.");
    }
}
