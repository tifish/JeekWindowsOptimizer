using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jeek.Avalonia.Localization;
using JeekTools;
using JeekWindowsOptimizer.Startup;
using Microsoft.Extensions.Logging;
using MsBox.Avalonia.Enums;
using ZLogger;

namespace JeekWindowsOptimizer;

/// <summary>
///     Startup tab: an allow-list over everything that runs at startup, whether that is a Run key,
///     a Startup folder shortcut, a logon-triggered task, a service, a driver, or an Explorer
///     extension.
///     <para>
///         The tab shows two independent facts per row: what the system does today, and what the
///         user decided. They can disagree, and that disagreement is the point. A decision made on
///         another machine arrives through the synced ledger and shows up here as "denied, not yet
///         applied" rather than silently switching something off.
///     </para>
/// </summary>
public partial class MainViewModel
{
    private static readonly ILogger StartupLog = LogManager.CreateLogger("StartupTab");

    public List<StartupGroup> AllStartupGroups { get; } = [];
    public FastObservableCollection<StartupGroup> StartupGroups { get; } = [];
    public FastObservableCollection<GroupNavItem> StartupGroupNavItems { get; } = [];

    private readonly Dictionary<string, bool> _startupGroupExpanded = new(StringComparer.Ordinal);
    private string? _selectedStartupNavKey;
    private bool _startupScannedOnce;
    private bool _startupHandlersAttached;
    private bool _isLoadingStartupSettings;

    [ObservableProperty]
    public partial GroupNavItem? SelectedStartupGroupNavItem { get; set; }

    [ObservableProperty]
    public partial string StartupTabHeader { get; set; } = Localizer.Get("Startup");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartupActive))]
    [NotifyCanExecuteChangedFor(nameof(ScanStartupCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnforceStartupDecisionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(AcceptStartupBaselineCommand))]
    public partial bool IsStartupBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartupActive))]
    [NotifyCanExecuteChangedFor(nameof(ScanStartupCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnforceStartupDecisionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(AcceptStartupBaselineCommand))]
    public partial bool IsStartupScanning { get; private set; }

    [ObservableProperty]
    public partial string StartupSummaryText { get; set; } = "";

    /// <summary>Shown when the ledger could not be read, because decisions are not being saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartupWarning))]
    public partial string StartupWarningText { get; set; } = "";

    public bool HasStartupWarning => StartupWarningText.Length > 0;

    /// <summary>True on a machine whose first sweep has not been accepted yet.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptStartupBaselineCommand))]
    public partial bool IsStartupBaselinePending { get; set; }

    [ObservableProperty]
    public partial string StartupBaselinePromptText { get; set; } = "";

    public bool IsStartupActive => IsStartupBusy || IsStartupScanning;

    public bool IsStartupTabSelected => SelectedTabIndex == StartupTabIndex;

    // ---------- Filters ----------

    [ObservableProperty]
    public partial bool ShowWindowsStartupEntries { get; set; }

    [ObservableProperty]
    public partial bool HideMicrosoftStartupEntries { get; set; }

    [ObservableProperty]
    public partial bool ShowOnlyPendingStartupItems { get; set; }

    [ObservableProperty]
    public partial bool AutoEnforceStartupDecisions { get; set; }

    partial void OnShowWindowsStartupEntriesChanged(bool value)
    {
        if (_isLoadingStartupSettings)
            return;
        AppSettingsStore.SetShowWindowsStartupEntries(value);
        RefreshDisplayedStartupGroups();
    }

    partial void OnHideMicrosoftStartupEntriesChanged(bool value)
    {
        if (_isLoadingStartupSettings)
            return;
        AppSettingsStore.SetHideMicrosoftStartupEntries(value);
        RefreshDisplayedStartupGroups();
    }

    partial void OnShowOnlyPendingStartupItemsChanged(bool value)
    {
        if (_isLoadingStartupSettings)
            return;
        AppSettingsStore.SetShowOnlyPendingStartupItems(value);
        RefreshDisplayedStartupGroups();
    }

    partial void OnAutoEnforceStartupDecisionsChanged(bool value)
    {
        if (_isLoadingStartupSettings)
            return;
        AppSettingsStore.SetAutoEnforceStartupDecisions(value);
    }

    public void LoadStartupSettings()
    {
        _isLoadingStartupSettings = true;
        ShowWindowsStartupEntries = AppSettingsStore.Roaming.ShowWindowsStartupEntries;
        HideMicrosoftStartupEntries = AppSettingsStore.Roaming.HideMicrosoftStartupEntries;
        ShowOnlyPendingStartupItems = AppSettingsStore.Roaming.ShowOnlyPendingStartupItems;
        AutoEnforceStartupDecisions = AppSettingsStore.Roaming.AutoEnforceStartupDecisions;
        _isLoadingStartupSettings = false;
    }

    // ---------- Items ----------

    public IEnumerable<StartupItem> StartupItems =>
        AllStartupGroups.SelectMany(group => group.Items);

    /// <summary>Items the current filters let through. Decisions still cover the hidden ones.</summary>
    public IEnumerable<StartupItem> VisibleStartupItems =>
        StartupItems.Where(IsStartupItemVisible);

    private bool IsStartupItemVisible(StartupItem item)
    {
        // Windows' own components are hidden by default. There are several hundred of them, they
        // are replaced on every cumulative update, and asking about each would drown the handful of
        // third-party entries the user came here for. They are still scanned, so a file that stops
        // being Windows-signed stops being hidden.
        if (item.SignerKind == StartupSignerKind.Windows && !ShowWindowsStartupEntries)
            return false;

        if (item.SignerKind == StartupSignerKind.Microsoft && HideMicrosoftStartupEntries)
            return false;

        if (ShowOnlyPendingStartupItems && !item.IsPending)
            return false;

        if (!IsSearchActive)
            return true;

        return MatchesStartupSearch(item);
    }

    private bool MatchesStartupSearch(StartupItem item)
    {
        var terms = SearchText.Split(SearchTermSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
            return true;

        return terms.All(term =>
            item.DisplayName.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || item.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || item.Command.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || item.Location.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || (item.Publisher ?? "").Contains(term, StringComparison.CurrentCultureIgnoreCase)
        );
    }

    partial void OnSelectedStartupGroupNavItemChanged(GroupNavItem? value)
    {
        if (_suppressGroupNavSelection)
            return;

        _selectedStartupNavKey = value?.NameKey;
        if (_syncingNavFromScroll)
            return;

        if (value?.StartupGroup is not { } group)
            return;

        group.IsExpanded = true;
        _startupGroupExpanded[group.NameKey] = true;
        ScrollToGroupRequested?.Invoke(this, group);
    }

    private void OnStartupTabSelected()
    {
        AttachStartupHandlers();

        if (_startupScannedOnce || IsStartupActive)
            return;

        _startupScannedOnce = true;
        _ = ScanStartupCommand.ExecuteAsync(null);
    }

    private void AttachStartupHandlers()
    {
        if (_startupHandlersAttached)
            return;
        _startupHandlersAttached = true;

        // The ledger reloads itself when a sync tool replaces the file. Re-apply the merged result
        // to the rows already on screen instead of forcing a full rescan of the system.
        StartupDecisionStore.ExternallyChanged += OnStartupDecisionsExternallyChanged;
    }

    private void OnStartupDecisionsExternallyChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            ReapplyStartupDecisions();
            UpdateStartupSummary();
            RefreshDisplayedStartupGroups();
            StartupLog.ZLogInformation($"Startup decisions reloaded after an external change");
        });
    }

    /// <summary>Re-reads the ledger into the rows on screen without touching the system.</summary>
    private void ReapplyStartupDecisions()
    {
        var decisions = StartupDecisionStore.Snapshot();

        foreach (var item in StartupItems)
        {
            if (decisions.TryGetValue(item.Key, out var entry))
            {
                item.Decision = entry.Decision;
                item.DecidedOnMachine = entry.Machine;
                item.DecidedAtUtc = entry.DecidedAtUtc;
                item.DecisionSource = string.Equals(
                    entry.Machine,
                    Environment.MachineName,
                    StringComparison.OrdinalIgnoreCase
                )
                    ? StartupDecisionSource.ThisMachine
                    : StartupDecisionSource.OtherMachine;
            }
            else if (item.DecisionSource != StartupDecisionSource.LooseMatch)
            {
                item.Decision = null;
                item.DecisionSource = StartupDecisionSource.None;
            }
        }

        foreach (var group in AllStartupGroups)
            group.NotifyCountsChanged();
    }

    // ---------- Scan ----------

    private bool CanScanStartup => !IsStartupActive;

    [RelayCommand(CanExecute = nameof(CanScanStartup))]
    private async Task ScanStartup()
    {
        AttachStartupHandlers();

        IsStartupScanning = true;
        StatusMessage = Localizer.Get("StartupScanning");

        try
        {
            var result = await StartupItemManager.ScanAsync();
            RebuildStartupGroups(result.Items);
            IsStartupBaselinePending = result.BaselinePending;

            if (AutoEnforceStartupDecisions)
                await EnforceStartupDecisionsCore(silent: true);
        }
        catch (Exception ex)
        {
            StartupLog.ZLogError(ex, $"Startup scan failed");
            StartupWarningText = string.Format(Localizer.Get("StartupScanFailed"), ex.Message);
        }
        finally
        {
            IsStartupScanning = false;
            UpdateStartupSummary();
            // Count what the user can actually see, so the status line agrees with the summary
            // card instead of quoting the several hundred hidden Windows entries.
            var visible = VisibleStartupItems.ToList();
            StatusMessage = string.Format(
                Localizer.Get("StartupScanFinished"),
                visible.Count,
                visible.Count(item => item.IsPending)
            );
        }
    }

    private void RebuildStartupGroups(List<StartupItem> items)
    {
        CaptureStartupExpandedState();
        AllStartupGroups.Clear();

        foreach (var kind in StartupItemManager.KindOrder)
        {
            var groupItems = items.Where(item => item.Kind == kind).ToList();
            if (groupItems.Count == 0)
                continue;

            var group = new StartupGroup(kind, groupItems);
            if (_startupGroupExpanded.TryGetValue(group.NameKey, out var expanded))
                group.IsExpanded = expanded;
            AllStartupGroups.Add(group);
        }

        RefreshDisplayedStartupGroups();
    }

    private void CaptureStartupExpandedState()
    {
        foreach (var group in AllStartupGroups)
            _startupGroupExpanded[group.NameKey] = group.IsExpanded;
    }

    private void RefreshDisplayedStartupGroups()
    {
        CaptureStartupExpandedState();

        var displayed = new List<StartupGroup>();
        foreach (var group in AllStartupGroups)
        {
            var visible = group.Items.Where(IsStartupItemVisible).ToList();
            if (visible.Count == 0)
                continue;

            // Rebuild the group's visible collection in place so the nav entry, the expander state
            // and the scroll target all keep pointing at the same object.
            if (!visible.SequenceEqual(group.Items))
            {
                var filtered = new StartupGroup(group.Kind, visible)
                {
                    IsExpanded = group.IsExpanded,
                };
                displayed.Add(filtered);
            }
            else
            {
                displayed.Add(group);
            }
        }

        StartupGroups.Replace(displayed);
        StartupGroupNavItems.Replace(displayed.Select(GroupNavItem.FromStartupGroup));

        _suppressGroupNavSelection = true;
        SelectedStartupGroupNavItem =
            StartupGroupNavItems.FirstOrDefault(nav => nav.NameKey == _selectedStartupNavKey)
            ?? StartupGroupNavItems.FirstOrDefault();
        _suppressGroupNavSelection = false;

        OnPropertyChanged(nameof(IsNoSearchResultsVisible));
    }

    private void UpdateStartupSummary()
    {
        var visible = VisibleStartupItems.ToList();
        var pending = visible.Count(item => item.IsPending);
        var notApplied = visible.Count(item => item.NeedsEnforcement);

        StartupSummaryText = string.Format(
            Localizer.Get("StartupSummary"),
            visible.Count,
            pending,
            notApplied
        );

        StartupBaselinePromptText = string.Format(
            Localizer.Get("StartupBaselinePrompt"),
            visible.Count(item => item.IsPending && !item.IsWindowsEntry)
        );

        StartupWarningText = StartupDecisionStore.IsPoisoned
            ? string.Format(
                Localizer.Get("StartupLedgerUnreadable"),
                StartupDecisionStore.PoisonReason ?? ""
            )
            : "";

        foreach (var group in AllStartupGroups)
            group.NotifyCountsChanged();
        foreach (var nav in StartupGroupNavItems)
            nav.NotifyDisplayChanged();

        EnforceStartupDecisionsCommand.NotifyCanExecuteChanged();
        AcceptStartupBaselineCommand.NotifyCanExecuteChanged();
    }

    // ---------- Decisions ----------

    [RelayCommand]
    private async Task AllowStartupItem(StartupItem? item) =>
        await DecideStartupItem(item, StartupDecision.Allow);

    [RelayCommand]
    private async Task DenyStartupItem(StartupItem? item) =>
        await DecideStartupItem(item, StartupDecision.Deny);

    /// <summary>
    ///     Records a decision and, because the user made it here and now, puts it into effect
    ///     immediately. A decision that merely arrived over sync is not applied this way; it waits
    ///     for the Apply command.
    /// </summary>
    private async Task DecideStartupItem(StartupItem? item, StartupDecision decision)
    {
        if (item is null || IsStartupBusy)
            return;

        if (!StartupDecisionStore.Decide(item.Key, decision, item.ToSnapshot()))
        {
            item.ErrorMessage = Localizer.Get("StartupLedgerBlocked");
            return;
        }

        item.Decision = decision;
        item.DecisionSource = StartupDecisionSource.ThisMachine;
        item.DecidedOnMachine = Environment.MachineName;
        item.DecidedAtUtc = DateTime.UtcNow;
        item.ErrorMessage = null;

        var wanted = decision == StartupDecision.Allow;
        if (item.CanToggle && item.IsEnabled != wanted)
            await ApplyStartupItem(item, wanted);

        UpdateStartupSummary();
    }

    private async Task<bool> ApplyStartupItem(StartupItem item, bool enabled)
    {
        var result = await Task.Run(() => StartupToggle.Apply(item, enabled));

        if (result.Succeeded)
        {
            item.IsEnabled = enabled;
            item.ErrorMessage = null;
            item.WarningMessage = result.Warning;
        }
        else
        {
            item.WarningMessage = null;
            item.ErrorMessage = result.Error;
            StartupLog.ZLogWarning(
                $"Failed to apply startup decision for {item.Name}: {result.Error}"
            );
        }

        if (result.RequiresExplorerRestart)
            _startupNeedsExplorerRestart = true;

        return result.Succeeded;
    }

    private bool _startupNeedsExplorerRestart;

    private bool CanEnforceStartupDecisions =>
        !IsStartupActive && StartupItems.Any(item => item.NeedsEnforcement);

    /// <summary>Brings the system in line with every recorded decision that is not in force.</summary>
    [RelayCommand(CanExecute = nameof(CanEnforceStartupDecisions))]
    private async Task EnforceStartupDecisions() => await EnforceStartupDecisionsCore(silent: false);

    private async Task EnforceStartupDecisionsCore(bool silent)
    {
        var pending = StartupItems.Where(item => item.NeedsEnforcement).ToList();
        if (pending.Count == 0)
            return;

        if (!silent)
        {
            var confirm = await ShowUpdateDialogAsync(
                Localizer.Get("Startup"),
                string.Format(Localizer.Get("StartupEnforceConfirm"), pending.Count),
                ButtonEnum.YesNo,
                MsBox.Avalonia.Enums.Icon.Question
            );
            if (confirm != ButtonResult.Yes)
                return;
        }

        IsStartupBusy = true;
        _startupNeedsExplorerRestart = false;

        try
        {
            var failed = 0;
            foreach (var item in pending)
                if (!await ApplyStartupItem(item, enabled: false))
                    failed++;

            UpdateStartupSummary();

            if (!silent)
                await ShowUpdateDialogAsync(
                    Localizer.Get("Startup"),
                    string.Format(
                        Localizer.Get("StartupEnforceResult"),
                        pending.Count - failed,
                        failed
                    ),
                    ButtonEnum.Ok,
                    failed > 0
                        ? MsBox.Avalonia.Enums.Icon.Warning
                        : MsBox.Avalonia.Enums.Icon.Success
                );

            await OfferExplorerRestart(silent);
        }
        finally
        {
            IsStartupBusy = false;
        }
    }

    private async Task OfferExplorerRestart(bool silent)
    {
        if (!_startupNeedsExplorerRestart)
            return;

        _startupNeedsExplorerRestart = false;
        if (silent)
            return;

        var answer = await ShowUpdateDialogAsync(
            Localizer.Get("Startup"),
            Localizer.Get("StartupRestartExplorerPrompt"),
            ButtonEnum.YesNo,
            MsBox.Avalonia.Enums.Icon.Question
        );

        if (answer == ButtonResult.Yes)
            await OptimizationItem.RestartExplorer();
    }

    private bool CanAcceptStartupBaseline => IsStartupBaselinePending && !IsStartupActive;

    /// <summary>
    ///     Turns the current state of this machine into decisions in one step, so an existing
    ///     install does not present hundreds of rows to confirm one at a time.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAcceptStartupBaseline))]
    private async Task AcceptStartupBaseline()
    {
        var candidates = StartupItems.Count(item => item.IsPending && !item.IsWindowsEntry);

        var confirm = await ShowUpdateDialogAsync(
            Localizer.Get("Startup"),
            string.Format(Localizer.Get("StartupBaselineConfirm"), candidates),
            ButtonEnum.YesNo,
            MsBox.Avalonia.Enums.Icon.Question
        );
        if (confirm != ButtonResult.Yes)
            return;

        IsStartupBusy = true;
        try
        {
            var recorded = await Task.Run(
                () => StartupItemManager.AcceptBaseline(StartupItems.ToList())
            );
            ReapplyStartupDecisions();
            IsStartupBaselinePending = false;
            UpdateStartupSummary();
            RefreshDisplayedStartupGroups();

            StartupLog.ZLogInformation($"Accepted the startup baseline with {recorded} decisions");
        }
        finally
        {
            IsStartupBusy = false;
        }
    }

    /// <summary>
    ///     Forgets every decision on every machine. This is an epoch bump rather than a delete, so
    ///     the reset survives sync instead of being undone by a machine that still holds the old
    ///     entries.
    /// </summary>
    [RelayCommand]
    private async Task ForgetAllStartupDecisions()
    {
        var confirm = await ShowUpdateDialogAsync(
            Localizer.Get("Startup"),
            string.Format(Localizer.Get("StartupForgetAllConfirm"), StartupDecisionStore.Count),
            ButtonEnum.YesNo,
            MsBox.Avalonia.Enums.Icon.Warning
        );
        if (confirm != ButtonResult.Yes)
            return;

        if (!StartupDecisionStore.ForgetAll())
        {
            StartupWarningText = Localizer.Get("StartupLedgerBlocked");
            return;
        }

        StartupDecisionStore.Flush();
        ReapplyStartupDecisions();
        IsStartupBaselinePending = !StartupLocalState.HasBaseline;
        UpdateStartupSummary();
        RefreshDisplayedStartupGroups();
    }

    [RelayCommand]
    private void OpenStartupItemLocation(StartupItem? item)
    {
        if (item is null)
            return;

        try
        {
            var target = item.ImagePath;
            if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
            {
                StartupLog.ZLogWarning($"Nothing to reveal for {item.Name}: '{target}' does not exist");
                return;
            }

            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{target}\"",
                    UseShellExecute = true,
                }
            );
        }
        catch (Exception ex)
        {
            StartupLog.ZLogWarning(ex, $"Failed to reveal {item.ImagePath}");
        }
    }

    public void NotifyStartupLanguageChanged()
    {
        StartupTabHeader = Localizer.Get("Startup");
        foreach (var group in AllStartupGroups)
            group.NotifyLanguageChanged();
        foreach (var nav in StartupGroupNavItems)
            nav.NotifyDisplayChanged();
        UpdateStartupSummary();
    }

    /// <summary>Enforces without the confirmation dialog. Debug surface only.</summary>
    public Task EnforceStartupDecisionsForDebugAsync() => EnforceStartupDecisionsCore(silent: true);

    /// <summary>Re-reads the ledger into the rows and refreshes the tab. Debug surface only.</summary>
    public void RefreshStartupAfterLedgerChangeForDebug()
    {
        ReapplyStartupDecisions();
        IsStartupBaselinePending = !StartupLocalState.HasBaseline;
        UpdateStartupSummary();
        RefreshDisplayedStartupGroups();
    }

    /// <summary>Builds the tab's state without showing it, so debug probes can inspect it.</summary>
    public async Task EnsureStartupItemsAsync()
    {
        AttachStartupHandlers();
        if (_startupScannedOnce)
            return;
        _startupScannedOnce = true;
        await ScanStartup();
    }
}
