namespace JeekWindowsOptimizer.Mcp;

internal static class PnpmStoreProbe
{
    internal static async Task<string> RunAsync()
    {
        var root = Path.Join(Path.GetTempPath(), "JeekPnpmProbe-" + Guid.NewGuid().ToString("N"));
        var version = Path.Join(root, "store", "v3");
        Directory.CreateDirectory(version);
        try
        {
            var keep = Path.Join(version, "referenced");
            var unused = Path.Join(version, "unused");
            File.WriteAllBytes(keep, new byte[200]);
            File.WriteAllBytes(unused, new byte[100]);
            var project = Path.Join(root, "project");
            Directory.CreateDirectory(project);
            var projectFile = Path.Join(project, "keep");
            File.WriteAllBytes(projectFile, new byte[1000]);
            Directory.CreateDirectory(Path.Join(version, "projects"));
            Directory.CreateSymbolicLink(Path.Join(version, "projects", "registered"), project);
            var backend = new FakeStore(version, unused);
            var item = new PnpmStoreCleanupItem(backend);
            await item.RefreshAsync();
            DiskSpaceCleanupProbe.Require(item.SizeBytes == 300 && !item.IsChecked, "store upper bound and opt-in");
            await item.CleanAsync();
            DiskSpaceCleanupProbe.Require(item.State == DiskSpaceItemState.Done && item.FreedBytes == 100
                && item.SizeBytes == 200 && File.Exists(keep) && File.Exists(projectFile), "prune retains referenced content and succeeds with remaining bytes");
            backend.Fail = true;
            await item.CleanAsync();
            DiskSpaceCleanupProbe.Require(item.State == DiskSpaceItemState.Failed && File.Exists(keep), "native failure preserves content");
            var absent = new PnpmStoreCleanupItem(new FakeStore(null, unused));
            await absent.RefreshAsync();
            DiskSpaceCleanupProbe.Require(absent.SizeBytes == 0, "missing pnpm");
            DiskSpaceCleanupProbe.Require(PnpmStore.ParsePath(version + "\r\n") == version, "native path parsing");
            var real = new PnpmStore(root, Path.Join(root, "real store with spaces"));
            var cli = "not installed";
            if (real.IsInstalled)
            {
                var actual = await real.GetPathAsync(CancellationToken.None);
                DiskSpaceCleanupProbe.Require(actual is not null && actual.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "native CLI isolated store");
                Directory.CreateDirectory(actual!);
                Directory.CreateDirectory(Path.Join(actual!, "projects"));
                Directory.CreateSymbolicLink(Path.Join(actual!, "projects", "registered"), project);
                await real.PruneAsync(actual!, CancellationToken.None);
                DiskSpaceCleanupProbe.Require(File.Exists(projectFile), "native prune preserves registered project target");
                DiskSpaceCleanupProbe.Require(File.Exists(keep), "native prune stays in pinned store");
                cli = "PASS real CLI path/prune in temporary store";
            }
            return "PASS pnpm: opt-in, upper-bound scan, unused-only prune, referenced data retained, native failure, missing tool; " + cli;
        }
        finally { FileSystemCleaner.DeleteDirectory(root); }
    }

    private sealed class FakeStore(string? path, string unused) : IPnpmStore
    {
        public bool Fail { get; set; }
        public Task<string?> GetPathAsync(CancellationToken token) => Task.FromResult(path);
        public Task PruneAsync(string expectedPath, CancellationToken token)
        {
            if (Fail || expectedPath != path) throw new IOException("Injected pnpm failure");
            File.Delete(unused);
            return Task.CompletedTask;
        }
    }
}
