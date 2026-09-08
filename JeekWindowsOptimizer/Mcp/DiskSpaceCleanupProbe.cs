namespace JeekWindowsOptimizer.Mcp;

/// <summary>Deterministic checks driven through this worktree's Debug MCP.</summary>
internal static class DiskSpaceCleanupProbe
{
    public static async Task<string> RunAsync(string scenario)
    {
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
