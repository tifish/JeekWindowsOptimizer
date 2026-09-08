namespace JeekWindowsOptimizer.Mcp;

/// <summary>Deterministic checks driven through this worktree's Debug MCP.</summary>
internal static class DiskSpaceCleanupProbe
{
    public static async Task<string> RunAsync(string scenario)
    {
        if (scenario == "browser")
            return await BrowserAsync();
        if (scenario != "accuracy")
            throw new ArgumentException("Unknown scenario: " + scenario);

        var item = new MeasurementItem();
        await item.RefreshAsync(); // Old UI estimate is deliberately different.
        Require(await item.CleanAsync() == 60 && item.IsFreedBytesKnown, "fresh measurement");
        item.FailRescan = true;
        Require(await item.CleanAsync() == 0 && !item.IsFreedBytesKnown
            && item.SizeBytes is null && item.State == DiskSpaceItemState.Failed
            && item.FreedBytes == 0, "rescan failure and stale result reset");
        item.FailRescan = false;
        item.CancelClean = true;
        Require(await item.CleanAsync() == 0 && !item.IsFreedBytesKnown
            && item.SizeBytes is null && item.State == DiskSpaceItemState.Failed, "cancellation");
        item.CancelClean = false;
        item.FailClean = true;
        Require(await item.CleanAsync() == 0 && !item.IsFreedBytesKnown
            && item.State == DiskSpaceItemState.Failed, "cleanup exception");
        item.FailClean = false;
        Require(await item.CleanAsync() == 60 && item.State == DiskSpaceItemState.Done,
            "successful retry");
        return "PASS accuracy: fresh measurement, rescan failure, stale result reset, cancellation, cleanup exception, retry";
    }

    internal static void Require(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException("FAIL: " + name);
    }

    private static async Task<string> BrowserAsync()
    {
        var root = Path.Join(Path.GetTempPath(), "JeekCleanupProbe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var data = Path.Join(root, "User Data");
            string Write(string relative)
            {
                var path = Path.Join(data, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[100]);
                return path;
            }
            Write(@"Default\Cache\Cache_Data\entry");
            Write(@"Profile 1\Code Cache\js\entry");
            var locked = Write(@"Default\Cache\locked");
            var keep = new[] { Write(@"Default\Network\Cookies"), Write(@"Default\Login Data"),
                Write(@"Default\History"), Write(@"Default\Local Storage\data"),
                Write(@"Unrecognized\Cache\data") };
            var running = true;
            var item = new BrowserCacheCleanupItem("Edge", data, () => running);
            await item.RefreshAsync();
            Require(item.SizeBytes == 300, "multiple profiles and exact cache allowlist");
            await item.CleanAsync();
            Require(item.State == DiskSpaceItemState.Failed && File.Exists(locked), "running browser blocked");
            running = false;
            using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await item.CleanAsync();
                Require(item.State == DiskSpaceItemState.Failed && item.SizeBytes == 100,
                    "locked cache reports incomplete");
            }
            await item.CleanAsync();
            Require(item.State == DiskSpaceItemState.Done && item.SizeBytes == 0
                && keep.All(File.Exists), "retry and preserve profile data");
            var outside = Path.Join(root, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllBytes(Path.Join(outside, "keep"), new byte[50]);
            Directory.CreateSymbolicLink(Path.Join(data, "Profile 2"), outside);
            Directory.CreateDirectory(Path.Join(data, "Profile 3"));
            Directory.CreateSymbolicLink(Path.Join(data, "Profile 3", "Cache"), outside);
            await item.RefreshAsync();
            await item.CleanAsync();
            Require(item.SizeBytes == 0 && File.Exists(Path.Join(outside, "keep")), "linked profiles and caches skipped");
            return "PASS browser: multiple profiles, cache allowlist, running guard, locked file, retry, preserved data, reparse points";
        }
        finally { FileSystemCleaner.DeleteDirectory(root); }
    }

    private sealed class MeasurementItem : DiskSpaceCleanupItem
    {
        public override string NameKey => "TempFilesCleanupName";
        public override string DescriptionKey => "TempFilesCleanupDescription";
        public bool FailRescan { get; set; }
        public bool CancelClean { get; set; }
        public bool FailClean { get; set; }
        private bool _afterClean;

        protected override Task<long> ScanCore(CancellationToken cancellationToken)
        {
            if (State == DiskSpaceItemState.Scanning)
                return Task.FromResult(1000L);
            if (_afterClean)
            {
                _afterClean = false;
                if (FailRescan)
                    throw new IOException("Injected rescan failure");
                return Task.FromResult(40L);
            }
            return Task.FromResult(100L);
        }

        protected override Task<bool> CleanCore(CancellationToken cancellationToken)
        {
            if (CancelClean)
                throw new OperationCanceledException();
            if (FailClean)
                throw new IOException("Injected cleanup failure");
            _afterClean = true;
            return Task.FromResult(true);
        }
    }
}
