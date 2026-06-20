using Avalonia.Threading;

namespace JeekWindowsOptimizer;

public enum OptimizationExecutionAffinity
{
    Ui,
    Background,
    ExclusiveBackground,
}

public static class OptimizationExecutionScheduler
{
    private static readonly SemaphoreSlim ExclusiveBackgroundLock = new(1, 1);

    // Guards against nested ExclusiveBackground execution. The lock above is a single
    // non-reentrant semaphore, so re-acquiring it from within an exclusive work item would
    // deadlock. This flag flows into Task.Run/await children via the ambient ExecutionContext,
    // so even transitive re-entry (e.g. Exclusive -> Background -> Exclusive) is caught and
    // fails fast instead of hanging the lock forever.
    private static readonly AsyncLocal<bool> InExclusiveBackground = new();

    public static Task RunAsync(
        OptimizationExecutionAffinity affinity,
        Action work,
        CancellationToken cancellationToken = default
    )
    {
        return RunAsync(
            affinity,
            () =>
            {
                work();
                return true;
            },
            cancellationToken
        );
    }

    public static Task<T> RunAsync<T>(
        OptimizationExecutionAffinity affinity,
        Func<T> work,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        return affinity switch
        {
            OptimizationExecutionAffinity.Ui => InvokeOnUiAsync(work, cancellationToken),
            OptimizationExecutionAffinity.Background => Task.Run(work, cancellationToken),
            OptimizationExecutionAffinity.ExclusiveBackground => RunExclusiveBackgroundAsync(
                () => Task.Run(work, cancellationToken),
                cancellationToken
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(affinity), affinity, null),
        };
    }

    public static Task RunAsync(
        OptimizationExecutionAffinity affinity,
        Func<Task> work,
        CancellationToken cancellationToken = default
    )
    {
        return RunAsync(
            affinity,
            async () =>
            {
                await work();
                return true;
            },
            cancellationToken
        );
    }

    public static Task<T> RunAsync<T>(
        OptimizationExecutionAffinity affinity,
        Func<Task<T>> work,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        return affinity switch
        {
            OptimizationExecutionAffinity.Ui => InvokeOnUiAsync(work, cancellationToken),
            OptimizationExecutionAffinity.Background => Task.Run(work, cancellationToken),
            OptimizationExecutionAffinity.ExclusiveBackground => RunExclusiveBackgroundAsync(
                () => Task.Run(work, cancellationToken),
                cancellationToken
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(affinity), affinity, null),
        };
    }

    private static async Task<T> RunExclusiveBackgroundAsync<T>(
        Func<Task<T>> work,
        CancellationToken cancellationToken
    )
    {
        if (InExclusiveBackground.Value)
            throw new InvalidOperationException(
                "Nested ExclusiveBackground execution is not allowed; re-entering the single "
                    + "exclusive lock would deadlock. Restructure the work so the inner operation "
                    + "runs after the outer one releases the lock."
            );

        await ExclusiveBackgroundLock.WaitAsync(cancellationToken);
        InExclusiveBackground.Value = true;
        try
        {
            return await work();
        }
        finally
        {
            InExclusiveBackground.Value = false;
            ExclusiveBackgroundLock.Release();
        }
    }

    private static Task<T> InvokeOnUiAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return Task.FromResult(work());

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        RegisterCancellation(completion, cancellationToken);

        Dispatcher.UIThread.Post(() =>
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }

    private static Task<T> InvokeOnUiAsync<T>(
        Func<Task<T>> work,
        CancellationToken cancellationToken
    )
    {
        if (Dispatcher.UIThread.CheckAccess())
            return work();

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        RegisterCancellation(completion, cancellationToken);

        Dispatcher.UIThread.Post(async () =>
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                completion.TrySetResult(await work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }

    private static void RegisterCancellation<T>(
        TaskCompletionSource<T> completion,
        CancellationToken cancellationToken
    )
    {
        if (!cancellationToken.CanBeCanceled)
            return;

        var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken)
        );
        completion.Task.ContinueWith(
            _ => registration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }
}
