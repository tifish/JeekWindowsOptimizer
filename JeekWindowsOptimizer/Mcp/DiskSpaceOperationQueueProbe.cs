namespace JeekWindowsOptimizer.Mcp;

internal static class DiskSpaceOperationQueueProbe
{
    public static async Task<string> RunAsync(MainViewModel vm)
    {
        if (vm.IsDiskSpaceActive)
            throw new InvalidOperationException("Wait until the current disk operation/scan finishes.");
        var trace = new List<string>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Cleanup("A", trace, gate.Task);
        var later = new Cleanup("C", trace, Task.CompletedTask);
        var drive = new DriveOption(Path.GetPathRoot(AppContext.BaseDirectory)!, long.MaxValue, true);
        var failedMove = new Move("B", trace, drive) { RejectCheck = true };
        var moved = new Move("D", trace, drive);
        var restored = new Move("E", trace, drive);
        var jobs = new List<Task>();
        try
        {
            var a = vm.CleanDiskSpaceItemsAsync([first], confirm: false);
            jobs.Add(a);
            await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(vm.IsDiskSpaceBusy && !vm.CanScanDiskSpace, "scan excluded during execution");
            Check(!vm.CleanDiskSpaceItemCommand.CanExecute(first)
                && vm.CleanDiskSpaceItemCommand.CanExecute(later)
                && vm.MoveDiskSpaceItemCommand.CanExecute(moved), "other row commands remain enabled");
            var b = vm.RunRelocationBatchAsync([(failedMove, drive), (moved, drive)]);
            jobs.Add(b);
            var c = vm.CleanDiskSpaceItemsAsync([later], confirm: false);
            jobs.Add(c);
            var e = vm.RestoreDiskSpaceItemDefaultAsync(restored, confirm: false);
            jobs.Add(e);
            Check(failedMove.QueuePosition == 1 && moved.QueuePosition == 2
                && later.QueuePosition == 3 && restored.QueuePosition == 4, "FIFO positions across batch, cleanup and restore");
            Check(moved.IsBusy && !moved.CanMove && !moved.CanCheck
                && !vm.MoveDiskSpaceItemCommand.CanExecute(moved)
                && moved.MoveButtonText == later.CleanButtonText
                && first.CleanButtonText != later.CleanButtonText, "queued rows locked with distinct button labels");
            Check(await vm.CleanDiskSpaceItemsAsync([first, later, later], false) == 0
                && !await vm.MoveDiskSpaceItemAsync(moved, drive, false)
                && vm.OperationQueue.PendingCount == 4, "duplicate requests ignored");
            // A UI selector is disabled, but even an external change must not retarget an approved job.
            moved.SelectedTargetDrive = new DriveOption(@"Z:\", long.MaxValue, false);
            gate.TrySetResult();
            await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(15));
            Check(trace.SequenceEqual(new[] { "A", "B", "D", "C", "E" }), "one FIFO worker continues after failure");
            Check((await b) == (1, 1) && await a == 100 && await c == 100 && await e,
                "per-request completion results");
            Check(moved.ExecutedDrive == drive.Root && restored.Restored, "pinned destination and restore action");
            Check(vm.OperationQueue.FailedCount == 1 && vm.OperationQueue.CompletedCount == 5
                && vm.OperationQueue.FreedBytes == 200 && !vm.IsDiskSpaceBusy
                && vm.OperationQueue.PendingCount == 0 && later.QueuePosition == 0
                && moved.QueuePosition == 0, "drain totals and state reset");
            var retry = new Cleanup("F", trace, Task.CompletedTask);
            Check(await vm.CleanDiskSpaceItemsAsync([retry], false) == 100
                && vm.OperationQueue.FailedCount == 0 && vm.OperationQueue.CompletedCount == 1,
                "new queue session resets totals");
            var full = new Move("Full", trace, drive) { ScanBytes = long.MaxValue };
            var changed = new Move("Changed", trace, drive) { RefreshSource = @"C:\ChangedSource" };
            var lateChecks = await vm.RunRelocationBatchAsync([(full, drive), (changed, drive)]);
            Check(lateChecks == (0, 2) && full.ExecutedDrive is null && changed.ExecutedDrive is null
                && full.State == DiskSpaceItemState.Failed && changed.State == DiskSpaceItemState.Failed
                && vm.OperationQueue.CompletedCount == 2 && vm.OperationQueue.FailedCount == 2,
                "fresh space and source validation prevents moves");
            return "PASS queue: mixed FIFO, batch order, per-row availability, duplicates, pinned target, move validation failure, continuation, restore, results, idle reset, late space/source checks";
        }
        finally
        {
            gate.TrySetResult();
            await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private static void Check(bool condition, string message) => DiskSpaceCleanupProbe.Require(condition, message);

    private sealed class Cleanup : DiskSpaceCleanupItem
    {
        private readonly string _name;
        private readonly List<string> _trace;
        private readonly Task _gate;
        private long _bytes = 100;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override string NameKey => _name;
        public override string DescriptionKey => "TempFilesCleanupDescription";

        // The fixtures start scanned, without writing any files.
        public Cleanup(string name, List<string> trace, Task gate)
        {
            _name = name;
            _trace = trace;
            _gate = gate;
            SizeBytes = 100;
            State = DiskSpaceItemState.Scanned;
        }

        protected override Task<long> ScanCore(CancellationToken cancellationToken) => Task.FromResult(_bytes);
        protected override async Task<bool> CleanCore(CancellationToken cancellationToken)
        {
            _trace.Add(_name);
            Started.TrySetResult();
            await _gate.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            _bytes = 0;
            return true;
        }
    }

    private sealed class Move : DiskSpaceRelocationItem
    {
        private readonly string _name;
        private readonly List<string> _trace;
        public bool RejectCheck { get; init; }
        public long ScanBytes { get; init; } = 1;
        public string? RefreshSource { get; init; }
        public string? ExecutedDrive { get; private set; }
        public bool Restored { get; private set; }
        public override string NameKey => _name;
        public override string DescriptionKey => "DownloadsRelocationDescription";
        public override string DefaultLocationText => @"C:\ProbeDefault";
        public override bool IsAtDefaultLocation => Restored;

        public Move(string name, List<string> trace, DriveOption drive)
        {
            _name = name;
            _trace = trace;
            CurrentLocation = @"C:\ProbeSource";
            SizeBytes = 1;
            State = DiskSpaceItemState.Scanned;
            SelectedTargetDrive = drive;
        }

        protected override Task RefreshCoreAsync(CancellationToken cancellationToken)
        {
            SizeBytes = ScanBytes;
            if (RefreshSource is not null)
                CurrentLocation = RefreshSource;
            return Task.CompletedTask;
        }

        public override string GetTargetPath(DriveOption drive) => Path.Join(drive.Root, "ProbeDestination");
        public override Task<(bool Succeeded, string? Error)> CheckAsync(DriveOption drive,
            CancellationToken cancellationToken = default)
        {
            _trace.Add(_name);
            return Task.FromResult<(bool, string?)>((!RejectCheck, RejectCheck ? "Injected target unavailable" : null));
        }
        protected override Task<(bool Succeeded, string? Error)> MoveCoreAsync(DriveOption drive, CancellationToken cancellationToken)
        {
            ExecutedDrive = drive.Root;
            return Task.FromResult<(bool, string?)>((true, null));
        }
        protected override Task<(bool Succeeded, string? Error)> RestoreDefaultCoreAsync(CancellationToken cancellationToken)
        {
            _trace.Add(_name);
            Restored = true;
            return Task.FromResult<(bool, string?)>((true, null));
        }
    }
}
