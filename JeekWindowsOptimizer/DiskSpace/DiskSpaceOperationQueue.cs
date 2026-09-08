namespace JeekWindowsOptimizer;

public readonly record struct DiskSpaceOperationResult(bool Succeeded, long FreedBytes = 0, bool RequiresReboot = false);

/// <summary>A single FIFO worker. All queue operations and notifications use the UI thread.</summary>
public sealed class DiskSpaceOperationQueue
{
    private readonly Queue<(DiskSpaceItem Item, Func<Task<DiskSpaceOperationResult>> Run, TaskCompletionSource<DiskSpaceOperationResult> Completion)> _pending = new();
    public event Action? Changed;
    public bool IsRunning { get; private set; }
    public DiskSpaceItem? CurrentItem { get; private set; }
    public int PendingCount => _pending.Count;
    public long FreedBytes { get; private set; }
    public int FailedCount { get; private set; }
    public int CompletedCount { get; private set; }
    public bool RequiresReboot { get; private set; }

    public bool CanEnqueue(DiskSpaceItem item) => !item.IsBusy
        && item != CurrentItem
        && !_pending.Any(entry => entry.Item == item);

    public Task<DiskSpaceOperationResult>? Enqueue(DiskSpaceItem item, Func<Task<DiskSpaceOperationResult>> run)
    {
        if (!CanEnqueue(item))
            return null;
        var completion = new TaskCompletionSource<DiskSpaceOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.Enqueue((item, run, completion));
        item.QueuePosition = _pending.Count;
        if (!IsRunning)
        {
            IsRunning = true;
            FreedBytes = 0;
            FailedCount = 0;
            CompletedCount = 0;
            RequiresReboot = false;
            _ = DrainAsync();
        }
        Changed?.Invoke();
        return completion.Task;
    }

    private async Task DrainAsync()
    {
        // Let a batch enqueue all entries before even a synchronous validation failure completes.
        await Task.Yield();
        while (_pending.TryDequeue(out var entry))
        {
            CurrentItem = entry.Item;
            entry.Item.QueuePosition = 0;
            var position = 0;
            foreach (var pending in _pending)
                pending.Item.QueuePosition = ++position;
            Changed?.Invoke();
            try
            {
                var result = await entry.Run();
                FreedBytes += result.FreedBytes;
                RequiresReboot |= result.Succeeded && result.RequiresReboot;
                if (!result.Succeeded)
                    FailedCount++;
                CompletedCount++;
                entry.Completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                FailedCount++;
                CompletedCount++;
                entry.Item.ReportOperationFailure(ex.Message);
                entry.Completion.TrySetResult(new(false));
            }
        }
        CurrentItem = null;
        IsRunning = false;
        Changed?.Invoke();
    }
}
