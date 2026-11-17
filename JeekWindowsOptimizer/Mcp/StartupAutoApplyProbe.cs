using Avalonia.Threading;
using JeekWindowsOptimizer.Startup;

namespace JeekWindowsOptimizer.Mcp;

/// <summary>Exercises the real selection commands on a detached view model without system writes.</summary>
internal static class StartupAutoApplyProbe
{
    public static async Task<string> RunAsync()
    {
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var uiCalls = 0;
        var failNext = 0;
        var writesAllowed = true;
        var report = new List<string>();
        void Check(string name, bool passed)
        {
            if (!passed)
                throw new InvalidOperationException(name);
            report.Add($"PASS  {name}");
        }

        using var vm = new MainViewModel
        {
            StartupDecisionWriter = (_, _) => writesAllowed,
            StartupToggleHandler = (_, _) =>
            {
                Interlocked.Increment(ref calls);
                if (Dispatcher.UIThread.CheckAccess())
                    Interlocked.Increment(ref uiCalls);
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    return StartupToggleResult.Fail("Probe timed out.");
                return Interlocked.Exchange(ref failNext, 0) == 1
                    ? StartupToggleResult.Fail("Simulated apply failure")
                    : StartupToggleResult.Ok();
            },
        };
        StartupItem Item(string name, bool canToggle = true) => new()
        {
            Kind = StartupItemKind.LogonRegistry,
            Key = $"AutoApplyProbe|{name}",
            Name = name,
            Location = "Synthetic",
            IsEnabled = true,
            CanToggle = canToggle,
        };
        var first = Item("first");
        var second = Item("second");
        vm.AllStartupGroups.Add(new StartupGroup(first.Kind, [first, second]));
        Task firstWork = Task.CompletedTask;
        Task secondWork = Task.CompletedTask;
        try
        {
            firstWork = vm.DenyStartupItemCommand.ExecuteAsync(first);
            Check("selection immediately starts background apply", first.IsApplying
                && first.Decision == StartupDecision.Deny && !firstWork.IsCompleted);
            Check("applying row shows progress and disables its choices", !first.CanChoose
                && first.StatusKey == "StartupStatusApplying");
            Check("scan and baseline cannot race an apply", !vm.ScanStartupCommand.CanExecute(null)
                && !vm.AcceptStartupBaselineCommand.CanExecute(null));
            await vm.AllowStartupItemCommand.ExecuteAsync(first);
            Check("opposite choice cannot race the same row", first.Decision == StartupDecision.Deny);
            Check("another row can use the same decision command", second.CanChoose
                && vm.DenyStartupItemCommand.CanExecute(second));
            secondWork = vm.DenyStartupItemCommand.ExecuteAsync(second);
            Check("two rows apply independently", first.IsApplying && second.IsApplying
                && vm.StartupApplyingCount == 2);
            release.Set();
            await Task.WhenAll(firstWork, secondWork).WaitAsync(TimeSpan.FromSeconds(15));
            Check("both choices take effect without an Apply command", !first.IsEnabled
                && !second.IsEnabled && calls == 2 && uiCalls == 0);
            Check("row and scan controls recover after completion", first.CanChoose
                && !first.IsApplying && vm.StartupApplyingCount == 0
                && vm.ScanStartupCommand.CanExecute(null));

            failNext = 1;
            await vm.AllowStartupItemCommand.ExecuteAsync(first);
            Check("failure stays visible and leaves actual state unchanged", !first.IsEnabled
                && first.ErrorMessage == "Simulated apply failure" && first.CanChoose);
            await vm.AllowStartupItemCommand.ExecuteAsync(first);
            Check("selecting again retries and clears the error", first.IsEnabled
                && first.ErrorMessage is null && first.IsCompliant);

            var before = calls;
            await vm.AllowStartupItemCommand.ExecuteAsync(first);
            Check("already applied selection needs no system write", calls == before);
            writesAllowed = false;
            await vm.DenyStartupItemCommand.ExecuteAsync(first);
            Check("blocked decision storage prevents system changes", calls == before
                && first.Decision == StartupDecision.Allow && first.ErrorMessage is not null);
            var readOnly = Item("read-only", false);
            await vm.DenyStartupItemCommand.ExecuteAsync(readOnly);
            Check("read-only entries cannot be changed", readOnly.Decision is null && calls == before);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(firstWork, secondWork);
        }
        return string.Join(Environment.NewLine, report) + "\nAll checks passed.";
    }
}
