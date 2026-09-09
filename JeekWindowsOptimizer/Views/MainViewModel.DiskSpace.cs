using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jeek.Avalonia.Localization;
using JeekTools;
using Microsoft.Extensions.Logging;
using MsBox.Avalonia.Enums;
using ZLogger;

namespace JeekWindowsOptimizer;

/// <summary>
///     Disk Space tab: scan-then-clean reclaimable items plus move-to-other-drive
///     items. Items are created on first visit and scanned once automatically.
/// </summary>
public partial class MainViewModel
{
    public List<DiskSpaceGroup> AllDiskSpaceGroups { get; } = [];
    public FastObservableCollection<DiskSpaceGroup> DiskSpaceGroups { get; } = [];
    public FastObservableCollection<GroupNavItem> DiskSpaceGroupNavItems { get; } = [];

    private readonly Dictionary<string, bool> _diskSpaceGroupExpanded = new(StringComparer.Ordinal);
    private string? _selectedDiskSpaceNavKey;
    private bool _diskSpaceItemsCreated;
    private bool _diskSpaceScannedOnce;
    private readonly Dictionary<string, bool> _pendingCleanupSelections = new(StringComparer.Ordinal);

    public void SaveDiskSpaceCleanupSelectionsIfChanged()
    {
        if (_pendingCleanupSelections.Count == 0) return;
        AppSettingsStore.Roaming.DiskSpaceCleanupSelections ??= new(StringComparer.Ordinal);
        foreach (var (key, value) in _pendingCleanupSelections)
            AppSettingsStore.Roaming.DiskSpaceCleanupSelections[key] = value;
        AppSettingsStore.SaveRoaming();
        _pendingCleanupSelections.Clear();
    }
    public DiskSpaceOperationQueue OperationQueue { get; } = new();

    [ObservableProperty]
    public partial GroupNavItem? SelectedDiskSpaceGroupNavItem { get; set; }

    [ObservableProperty]
    public partial string DiskSpaceTabHeader { get; set; } = DiskSpaceTabTitle;

    private static string DiskSpaceTabTitle =>
        DiskSpaceItemManager.FormatWithSystemDrive(Localizer.Get("DiskSpace"));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDiskSpaceActive))]
    [NotifyPropertyChangedFor(nameof(CanScanDiskSpace))]
    [NotifyPropertyChangedFor(nameof(CanCleanDiskSpace))]
    [NotifyPropertyChangedFor(nameof(CanQueueDiskSpace))]
    [NotifyCanExecuteChangedFor(nameof(CleanDiskSpaceItemCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanDiskSpaceCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanCheckedDiskSpaceItemsCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveCheckedDiskSpaceItemsCommand))]
    public partial bool IsDiskSpaceBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDiskSpaceActive))]
    [NotifyPropertyChangedFor(nameof(CanScanDiskSpace))]
    [NotifyCanExecuteChangedFor(nameof(ScanDiskSpaceCommand))]
    public partial bool IsDiskSpaceScanning { get; private set; }

    public bool IsDiskSpaceActive => IsDiskSpaceBusy || IsDiskSpaceScanning;

    [ObservableProperty]
    public partial string DiskSpaceSummaryText { get; set; } = "";

    [ObservableProperty]
    public partial string SystemDriveUsageText { get; set; } = "";

    [ObservableProperty]
    public partial double SystemDriveUsedPercent { get; set; }

    public bool IsDiskSpaceTabSelected => SelectedTabIndex == DiskSpaceTabIndex;

    public bool CanScanDiskSpace => !IsDiskSpaceActive;

    public bool CanCleanDiskSpace => CanQueueDiskSpace;

    public bool CanQueueDiskSpace => (!IsDiskSpaceBusy || OperationQueue.IsRunning)
        && DiskSpaceCleanupItems.Any(CanCleanDiskSpaceItem);

    private bool CanCleanDiskSpaceItem(DiskSpaceCleanupItem? item) => item is not null
        && (!IsDiskSpaceBusy || OperationQueue.IsRunning) && item.ReclaimableBytes > 0 && OperationQueue.CanEnqueue(item);

    public bool CanQueueMoves => (!IsDiskSpaceBusy || OperationQueue.IsRunning)
        && DiskSpaceRelocationItems.Any(CanMoveDiskSpaceItem);

    private bool CanMoveDiskSpaceItem(DiskSpaceRelocationItem? item) => item is not null
        && (!IsDiskSpaceBusy || OperationQueue.IsRunning) && item.CanMove && OperationQueue.CanEnqueue(item);

    private bool CanRestoreDiskSpaceItem(DiskSpaceRelocationItem? item) => item is not null
        && (!IsDiskSpaceBusy || OperationQueue.IsRunning) && item.CanRestoreDefault && OperationQueue.CanEnqueue(item);

    public IEnumerable<DiskSpaceItem> DiskSpaceItems =>
        AllDiskSpaceGroups.SelectMany(group => group.Items);

    public IEnumerable<DiskSpaceCleanupItem> DiskSpaceCleanupItems =>
        DiskSpaceItems.OfType<DiskSpaceCleanupItem>();

    public IEnumerable<DiskSpaceRelocationItem> DiskSpaceRelocationItems =>
        DiskSpaceItems.OfType<DiskSpaceRelocationItem>();

    partial void OnSelectedDiskSpaceGroupNavItemChanged(GroupNavItem? value)
    {
        if (_suppressGroupNavSelection)
            return;

        _selectedDiskSpaceNavKey = value?.NameKey;
        if (value?.DiskSpaceGroup is not { } group)
            return;

        group.IsExpanded = true;
        _diskSpaceGroupExpanded[group.NameKey] = true;
        ScrollToGroupRequested?.Invoke(this, group);
    }

    /// <summary>Builds the items (idempotent) so MCP probes can inspect them before the tab is shown.</summary>
    public void EnsureDiskSpaceItems()
    {
        if (_diskSpaceItemsCreated)
            return;
        _diskSpaceItemsCreated = true;
        OperationQueue.Changed += OnOperationQueueChanged;

        foreach (var item in DiskSpaceItemManager.CreateItems())
            AddDiskSpaceItem(item);

        RefreshSystemDriveUsage();
        UpdateDiskSpaceSummary();
        RefreshDisplayedDiskSpaceGroups();
    }

    private void AddDiskSpaceItem(DiskSpaceItem item)
    {
        if (item is DiskSpaceCleanupItem cleanupItem
            && AppSettingsStore.Roaming.DiskSpaceCleanupSelections?.TryGetValue(item.NameKey, out var saved) == true)
            cleanupItem.SetCheckedExplicitly(saved);
        var group = AllDiskSpaceGroups.FirstOrDefault(g => g.NameKey == item.GroupNameKey);
        if (group is null)
            AllDiskSpaceGroups.Add(new DiskSpaceGroup(item.GroupNameKey, [item]));
        else
            group.Items.Add(item);

        item.PropertyChanged += (_, args) =>
        {
            // A choice the scan derives on its own is recomputed next run; only remember
            // what the user decided.
            if (item is DiskSpaceCleanupItem cleanup && args.PropertyName == nameof(DiskSpaceCleanupItem.IsChecked)
                && cleanup.HasExplicitCheckedChoice)
                _pendingCleanupSelections[item.NameKey] = cleanup.IsChecked;
            if (args.PropertyName is nameof(DiskSpaceCleanupItem.IsChecked)
                or nameof(DiskSpaceItem.State) or nameof(DiskSpaceItem.SizeBytes)
                or nameof(DiskSpaceItem.QueuePosition) or nameof(DiskSpaceRelocationItem.SelectedTargetDrive))
                UpdateDiskSpaceSummary();
        };
    }

    private void OnDiskSpaceTabSelected()
    {
        EnsureDiskSpaceItems();
        if (!_diskSpaceScannedOnce && !IsDiskSpaceBusy)
            _ = ScanDiskSpaceAsync();
    }

    [RelayCommand(CanExecute = nameof(CanScanDiskSpace))]
    private Task ScanDiskSpace()
    {
        return ScanDiskSpaceAsync();
    }

    private Task? _diskSpaceScanTask;

    /// <summary>Starts a scan, or returns the one already running so callers can await it.</summary>
    public Task ScanDiskSpaceAsync()
    {
        EnsureDiskSpaceItems();
        if (_diskSpaceScanTask is { IsCompleted: false } running)
            return running;
        if (IsDiskSpaceBusy)
            return Task.CompletedTask;

        _diskSpaceScanTask = ScanDiskSpaceCoreAsync();
        return _diskSpaceScanTask;
    }

    private async Task ScanDiskSpaceCoreAsync()
    {
        IsDiskSpaceScanning = true;
        _diskSpaceScannedOnce = true;
        StatusMessage = Localizer.Get("DiskSpaceScanningAll");
        try
        {
            RefreshSystemDriveUsage();

            var distributions = await Task.Run(WslStorage.Discover);
            var discovered = distributions
                .Where(d => !d.Name.Equals("docker-desktop", StringComparison.OrdinalIgnoreCase))
                .Select(d => new WslRelocationItem(d)).ToList();
            var keys = discovered.Select(item => item.NameKey).ToHashSet(StringComparer.Ordinal);
            foreach (var group in AllDiskSpaceGroups)
                foreach (var stale in group.Items.OfType<WslRelocationItem>().Where(item => !keys.Contains(item.NameKey)).ToList())
                    group.Items.Remove(stale);
            foreach (var item in discovered)
                if (!DiskSpaceItems.Any(existing => existing.NameKey == item.NameKey))
                    AddDiskSpaceItem(item);
            RefreshDisplayedDiskSpaceGroups();

            var drives = await Task.Run(DiskSpaceItemManager.GetTargetDrives);
            foreach (var item in DiskSpaceRelocationItems)
                item.SetTargetDrives(drives);

            // Items are independent; DISM analysis alone takes a while, so run them together.
            await Task.WhenAll(DiskSpaceItems.Select(item => item.RefreshAsync()));
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Disk space scan failed");
        }
        finally
        {
            IsDiskSpaceScanning = false;
            UpdateDiskSpaceSummary();
            if (!IsDiskSpaceBusy)
            {
                var scanned = DiskSpaceCleanupItems.ToList();
                StatusMessage = string.Format(
                    ReclaimableTemplate("DiskSpaceScanFinished", scanned),
                    ReclaimableSize(scanned)
                );
            }
        }
    }

    private long TotalReclaimableBytes => DiskSpaceCleanupItems.Sum(item => item.ReclaimableBytes);

    /// <summary>
    ///     Items whose size is only an upper bound (shadow storage keeps the newest snapshot, a
    ///     pnpm prune keeps referenced packages) must not turn a sum into a promise.
    /// </summary>
    private static bool HasUpperBoundReclaimable(IReadOnlyCollection<DiskSpaceCleanupItem> items) =>
        items.Any(item => item.IsReclaimableUpperBound && item.ReclaimableBytes > 0);

    private static string ReclaimableSize(IReadOnlyCollection<DiskSpaceCleanupItem> items) =>
        ByteSize.Format(items.Sum(item => item.ReclaimableBytes));

    /// <summary>Sizes a total on its own, as a maximum when an upper-bound item takes part.</summary>
    private static string FormatReclaimable(IReadOnlyCollection<DiskSpaceCleanupItem> items) =>
        HasUpperBoundReclaimable(items)
            ? string.Format(Localizer.Get("DiskSpaceUpToSize"), ReclaimableSize(items))
            : ReclaimableSize(items);

    /// <summary>Picks the plain or the "up to" wording of a sentence that states a total.</summary>
    private static string ReclaimableTemplate(string key, IReadOnlyCollection<DiskSpaceCleanupItem> items) =>
        Localizer.Get(HasUpperBoundReclaimable(items) ? key + "UpTo" : key);

    private List<DiskSpaceRelocationItem> CheckedRelocationItems =>
        DiskSpaceRelocationItems.Where(item => item.IsChecked && item.CanMove).ToList();

    private long CheckedRelocationBytes => CheckedRelocationItems.Sum(item => item.SizeBytes ?? 0);

    [RelayCommand(CanExecute = nameof(CanQueueDiskSpace), AllowConcurrentExecutions = true)]
    private Task CleanCheckedDiskSpaceItems() => CleanDiskSpaceItemsAsync(
        DiskSpaceCleanupItems.Where(item => item.IsChecked).ToList(), confirm: true);

    [RelayCommand(CanExecute = nameof(CanCleanDiskSpaceItem), AllowConcurrentExecutions = true)]
    private Task CleanDiskSpaceItem(DiskSpaceCleanupItem? item) => item is null
        ? Task.CompletedTask : CleanDiskSpaceItemsAsync([item], confirm: true);

    /// <summary>Confirm once, enqueue in request order, and await only this request's items.</summary>
    public async Task<long> CleanDiskSpaceItemsAsync(
        IReadOnlyList<DiskSpaceCleanupItem> items,
        bool confirm
    )
    {
        EnsureDiskSpaceItems();
        if (IsDiskSpaceBusy && !OperationQueue.IsRunning)
            return 0;

        var targets = items.Distinct().Where(CanCleanDiskSpaceItem).ToList();
        if (targets.Count == 0)
        {
            if (!OperationQueue.IsRunning)
                StatusMessage = Localizer.Get("DiskSpaceNothingToClean");
            return 0;
        }

        if (confirm)
        {
            var result = await ShowUpdateDialogAsync(
                Localizer.Get("DiskSpaceCleanConfirmTitle"),
                string.Format(ReclaimableTemplate("DiskSpaceCleanConfirmMessage", targets), targets.Count,
                    ReclaimableSize(targets))
                    + "\n\n" + string.Join("\n", targets.Select(item => item.GroupNameKey == DeveloperCacheCleanupItem.DeveloperGroup ? item.Name + "\n" + item.Description : item.Name)),
                ButtonEnum.YesNo,
                MsBox.Avalonia.Enums.Icon.Question
            );
            if (result != ButtonResult.Yes)
                return 0;
        }

        // Another confirmation or a relocation may have completed while the dialog was open.
        if (IsDiskSpaceBusy && !OperationQueue.IsRunning)
            return 0;
        var completions = new List<Task<DiskSpaceOperationResult>>();
        foreach (var item in targets)
            if (CanCleanDiskSpaceItem(item) && OperationQueue.Enqueue(item, async () =>
                {
                    var freed = await item.CleanAsync();
                    return new(item.State == DiskSpaceItemState.Done, freed);
                }) is { } completion)
                completions.Add(completion);
        var results = await Task.WhenAll(completions);
        return results.Sum(result => result.FreedBytes);
    }

    private async void OnOperationQueueChanged()
    {
        IsDiskSpaceBusy = OperationQueue.IsRunning;
        if (OperationQueue.IsRunning)
            StatusMessage = string.Format(Localizer.Get("DiskSpaceOperationQueueRunning"),
                OperationQueue.CurrentItem?.Name ?? Localizer.Get("DiskSpaceQueuedButton"), OperationQueue.PendingCount);
        else
        {
            RefreshSystemDriveUsage();
            StatusMessage = string.Format(Localizer.Get("DiskSpaceOperationQueueCompleted"),
                OperationQueue.CompletedCount - OperationQueue.FailedCount, OperationQueue.FailedCount,
                ByteSize.Format(OperationQueue.FreedBytes));
        }
        UpdateDiskSpaceSummary();
        if (!OperationQueue.IsRunning && OperationQueue.RequiresReboot)
        {
            try { await OptimizationItem.PromptReboot(); }
            catch (Exception ex) { Log.ZLogError(ex, $"Failed to show reboot prompt"); }
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveDiskSpaceItem), AllowConcurrentExecutions = true)]
    private Task MoveDiskSpaceItem(DiskSpaceRelocationItem? item)
    {
        return item is null
            ? Task.CompletedTask
            : MoveDiskSpaceItemAsync(item, item.SelectedTargetDrive, confirm: true);
    }

    public async Task<bool> MoveDiskSpaceItemAsync(
        DiskSpaceRelocationItem item,
        DriveOption? drive,
        bool confirm
    )
    {
        if (drive is null || !CanMoveDiskSpaceItem(item))
            return false;

        var target = item.GetTargetPath(drive);
        if (confirm)
        {
            var message = string.Format(
                Localizer.Get("DiskSpaceMoveConfirmMessage"),
                item.Name,
                item.CurrentLocation,
                target,
                item.SizeText
            );
            if (item.MoveNotice.Length > 0)
                message += "\n\n" + item.MoveNotice;
            if (item.TargetHasContent(drive))
                message += "\n" + Localizer.Get("DiskSpaceMoveTargetExistsNote");

            var result = await ShowUpdateDialogAsync(
                Localizer.Get("DiskSpaceMoveConfirmTitle"),
                message,
                ButtonEnum.YesNo,
                MsBox.Avalonia.Enums.Icon.Question
            );
            if (result != ButtonResult.Yes)
                return false;
        }

        var (succeeded, _) = await RunRelocationBatchAsync([(item, drive)]);
        return succeeded == 1;
    }

    [RelayCommand(CanExecute = nameof(CanQueueMoves), AllowConcurrentExecutions = true)]
    private Task MoveCheckedDiskSpaceItems()
    {
        return MoveCheckedDiskSpaceItemsAsync(confirm: true);
    }

    /// <summary>
    ///     Moves every checked relocation item to its own selected drive, user folders
    ///     first and the paging file last so the reboot prompt comes once at the end.
    ///     Refuses up front when a target drive lacks room for everything headed there.
    /// </summary>
    public async Task<(int Succeeded, int Failed)> MoveCheckedDiskSpaceItemsAsync(bool confirm)
    {
        EnsureDiskSpaceItems();
        if (IsDiskSpaceBusy && !OperationQueue.IsRunning)
            return (0, 0);

        var moves = CheckedRelocationItems
            .Where(item => item.SelectedTargetDrive is not null)
            .OrderBy(item => item.RequiresReboot ? 1 : 0)
            .Select(item => (Item: item, Drive: item.SelectedTargetDrive!))
            .ToList();

        if (moves.Count == 0)
        {
            StatusMessage = Localizer.Get("DiskSpaceNoItemsCheckedToMove");
            return (0, 0);
        }

        // Free-space check per target drive, against a fresh reading rather than the
        // number captured at scan time.
        foreach (var group in moves.GroupBy(m => m.Drive.Root, StringComparer.OrdinalIgnoreCase))
        {
            var needed = group.Sum(m => m.Item.SizeBytes ?? 0);
            long available;
            try
            {
                available = new DriveInfo(group.Key).AvailableFreeSpace;
            }
            catch
            {
                available = group.First().Drive.FreeBytes;
            }

            if (needed > available)
            {
                var text = string.Format(
                    Localizer.Get("DiskSpaceBatchMoveInsufficientSpace"),
                    group.First().Drive.Letter,
                    ByteSize.Format(needed),
                    ByteSize.Format(available)
                );
                StatusMessage = text;
                if (confirm)
                    await ShowUpdateDialogAsync(
                        Localizer.Get("DiskSpaceBatchMoveConfirmTitle"),
                        text,
                        ButtonEnum.Ok,
                        MsBox.Avalonia.Enums.Icon.Warning
                    );
                return (0, 0);
            }
        }

        if (confirm)
        {
            var lines = string.Join(
                "\n",
                moves.Select(m =>
                    string.Format(
                        Localizer.Get("DiskSpaceBatchMoveLine"),
                        m.Item.Name,
                        m.Item.CurrentLocation,
                        m.Item.GetTargetPath(m.Drive),
                        m.Item.SizeText
                    )
                )
            );
            var message = string.Format(
                Localizer.Get("DiskSpaceBatchMoveConfirmMessage"),
                moves.Count,
                ByteSize.Format(moves.Sum(m => m.Item.SizeBytes ?? 0)),
                lines
            );
            if (moves.Any(m => m.Item.TargetHasContent(m.Drive)))
                message += "\n" + Localizer.Get("DiskSpaceMoveTargetExistsNote");
            message += "\n\n" + string.Join("\n", moves.Select(m => m.Item.MoveNotice).Where(n => n.Length > 0).Distinct());

            var result = await ShowUpdateDialogAsync(
                Localizer.Get("DiskSpaceBatchMoveConfirmTitle"),
                message,
                ButtonEnum.YesNo,
                MsBox.Avalonia.Enums.Icon.Question
            );
            if (result != ButtonResult.Yes)
                return (0, 0);
        }

        return await RunRelocationBatchAsync(moves);
    }

    internal async Task<(int Succeeded, int Failed)> RunRelocationBatchAsync(
        IReadOnlyList<(DiskSpaceRelocationItem Item, DriveOption Drive)> moves
    )
    {
        EnsureDiskSpaceItems();
        if (IsDiskSpaceBusy && !OperationQueue.IsRunning)
            return (0, 0);
        var completions = new List<Task<DiskSpaceOperationResult>>();
        foreach (var (item, drive) in moves)
        {
            if (!CanMoveDiskSpaceItem(item))
                continue;
            var source = item.CurrentLocation;
            if (OperationQueue.Enqueue(item, () => ExecuteQueuedMoveAsync(item, drive, source)) is { } completion)
                completions.Add(completion);
        }
        var results = await Task.WhenAll(completions);
        return (results.Count(result => result.Succeeded), results.Count(result => !result.Succeeded));
    }

    private static async Task<DiskSpaceOperationResult> ExecuteQueuedMoveAsync(
        DiskSpaceRelocationItem item, DriveOption? drive, string expectedSource)
    {
        await item.RefreshAsync();
        if (item.State != DiskSpaceItemState.Scanned)
            return new(false);
        if (!string.Equals(item.CurrentLocation, expectedSource, StringComparison.OrdinalIgnoreCase))
        {
            item.ReportOperationFailure(Localizer.Get("DiskSpaceQueuedSourceChanged"));
            return new(false);
        }
        if (drive is not null)
        {
            var check = await item.CheckAsync(drive);
            if (!check.Succeeded)
            {
                item.ReportOperationFailure(check.Error ?? Localizer.Get("DiskSpaceQueuedTargetUnavailable"));
                return new(false);
            }
        }
        var targetRoot = drive?.Root ?? (item is UserFolderRelocationItem folder
            ? Path.GetPathRoot(folder.DefaultLocation) : null);
        if (targetRoot is not null)
        {
            // Read at execution time; preceding moves may have consumed the free space.
            var available = new DriveInfo(targetRoot).AvailableFreeSpace;
            if (item.SizeBytes is { } needed && needed > available)
            {
                item.ReportOperationFailure(string.Format(Localizer.Get("DiskSpaceBatchMoveInsufficientSpace"),
                    targetRoot.TrimEnd('\\'), ByteSize.Format(needed), ByteSize.Format(available)));
                return new(false);
            }
        }
        var ok = drive is null ? await item.RestoreDefaultAsync() : await item.MoveAsync(drive);
        return new(ok, RequiresReboot: ok && item.RequiresReboot);
    }

    [RelayCommand(CanExecute = nameof(CanRestoreDiskSpaceItem), AllowConcurrentExecutions = true)]
    private Task RestoreDiskSpaceItemDefault(DiskSpaceRelocationItem? item) => item is null
        ? Task.CompletedTask : RestoreDiskSpaceItemDefaultAsync(item, confirm: true);

    public async Task<bool> RestoreDiskSpaceItemDefaultAsync(DiskSpaceRelocationItem item, bool confirm)
    {
        EnsureDiskSpaceItems();
        if (!CanRestoreDiskSpaceItem(item))
            return false;
        if (confirm)
        {
            var result = await ShowUpdateDialogAsync(
                Localizer.Get("DiskSpaceRestoreDefaultConfirmTitle"),
                string.Format(Localizer.Get("DiskSpaceRestoreDefaultConfirmMessage"),
                    item.Name, item.CurrentLocation, item.DefaultLocationText, item.SizeText) + "\n\n" + item.MoveNotice,
                ButtonEnum.YesNo, MsBox.Avalonia.Enums.Icon.Question);
            if (result != ButtonResult.Yes)
                return false;
        }
        if (!CanRestoreDiskSpaceItem(item))
            return false;
        var source = item.CurrentLocation;
        var completion = OperationQueue.Enqueue(item, () => ExecuteQueuedMoveAsync(item, null, source));
        return completion is not null && (await completion).Succeeded;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SetHibernationMode(string mode)
    {
        EnsureDiskSpaceItems();
        var item = DiskSpaceItems.OfType<HibernationDiskSpaceItem>().Single();
        if (!OperationQueue.CanEnqueue(item) || (IsDiskSpaceBusy && !OperationQueue.IsRunning)) return;
        HibernationDiskSpaceItem.Commands(mode);
        var result = await ShowUpdateDialogAsync(Localizer.Get("HibernationDiskSpaceName"),
            Localizer.Get("HibernationConfirm" + mode), ButtonEnum.YesNo, MsBox.Avalonia.Enums.Icon.Question);
        if (result != ButtonResult.Yes || (IsDiskSpaceBusy && !OperationQueue.IsRunning)) return;
        if (OperationQueue.Enqueue(item, () => item.SetModeAsync(mode)) is { } completion)
            await completion;
    }

    private void RefreshSystemDriveUsage()
    {
        if (DiskSpaceItemManager.GetSystemDriveUsage() is not { } usage)
        {
            SystemDriveUsageText = "";
            SystemDriveUsedPercent = 0;
            return;
        }

        SystemDriveUsageText = string.Format(
            Localizer.Get("SystemDriveUsage"),
            usage.Letter,
            ByteSize.Format(usage.UsedBytes),
            ByteSize.Format(usage.TotalBytes),
            ByteSize.Format(usage.FreeBytes)
        );
        SystemDriveUsedPercent = usage.UsedPercent;
    }

    private void UpdateDiskSpaceSummary()
    {
        if (!_diskSpaceScannedOnce)
        {
            DiskSpaceSummaryText = Localizer.Get("DiskSpaceNotScanned");
            DiskSpaceTabHeader = DiskSpaceTabTitle;
        }
        else
        {
            var items = DiskSpaceCleanupItems.ToList();
            var total = FormatReclaimable(items);
            var summary = string.Format(
                ReclaimableTemplate("DiskSpaceReclaimableSummary", items),
                ReclaimableSize(items),
                FormatReclaimable(items.Where(item => item.IsChecked).ToList())
            );
            var moveBytes = CheckedRelocationBytes;
            if (moveBytes > 0)
                summary += "  " + string.Format(Localizer.Get("DiskSpaceMoveSummary"), ByteSize.Format(moveBytes));
            if (IsDiskSpaceScanning)
                summary += "  " + Localizer.Get("DiskSpaceScanInProgressHint");
            DiskSpaceSummaryText = summary;
            DiskSpaceTabHeader = $"{DiskSpaceTabTitle} ({total})";
        }

        OnPropertyChanged(nameof(CanCleanDiskSpace));
        OnPropertyChanged(nameof(CanQueueDiskSpace));
        OnPropertyChanged(nameof(CanQueueMoves));
        MoveDiskSpaceItemCommand.NotifyCanExecuteChanged();
        RestoreDiskSpaceItemDefaultCommand.NotifyCanExecuteChanged();
        CleanDiskSpaceItemCommand.NotifyCanExecuteChanged();
        CleanCheckedDiskSpaceItemsCommand.NotifyCanExecuteChanged();
        MoveCheckedDiskSpaceItemsCommand.NotifyCanExecuteChanged();
    }

    private void RefreshDisplayedDiskSpaceGroups()
    {
        foreach (var group in DiskSpaceGroups)
            _diskSpaceGroupExpanded[group.NameKey] = group.IsExpanded;

        IEnumerable<DiskSpaceGroup> displayed;
        if (!IsSearchActive)
        {
            displayed = AllDiskSpaceGroups;
        }
        else
        {
            var terms = GetSearchTerms();
            displayed = AllDiskSpaceGroups
                .Select(group =>
                {
                    var items = group
                        .Items.Where(item =>
                            MatchesSearch(terms, group.Name, item.Name, item.Description)
                        )
                        .ToArray();
                    return new DiskSpaceGroup(group.NameKey, items);
                })
                .Where(group => group.Items.Count > 0);
        }

        var list = displayed.ToList();
        foreach (var group in list)
            group.IsExpanded =
                !_diskSpaceGroupExpanded.TryGetValue(group.NameKey, out var expanded) || expanded;

        DiskSpaceGroups.Replace(list);

        var navItems = list.Select(GroupNavItem.FromDiskSpaceGroup).ToList();
        _suppressGroupNavSelection = true;
        DiskSpaceGroupNavItems.Replace(navItems);
        SelectedDiskSpaceGroupNavItem = ResolveNavSelection(navItems, _selectedDiskSpaceNavKey);
        _suppressGroupNavSelection = false;

        OnPropertyChanged(nameof(IsNoSearchResultsVisible));
    }

    [RelayCommand]
    private void SelectAllDiskSpaceGroup(DiskSpaceGroup? group) => SetDiskSpaceGroupChecked(group, true);

    [RelayCommand]
    private void SelectNoneDiskSpaceGroup(DiskSpaceGroup? group) => SetDiskSpaceGroupChecked(group, false);

    private void SetDiskSpaceGroupChecked(DiskSpaceGroup? group, bool selected)
    {
        if (group is null) return;
        // The displayed group contains only search matches. Mirror each row's checkbox rules.
        foreach (var item in group.Items)
        {
            if (item.IsBusy) continue;
            if (item is DiskSpaceCleanupItem cleanup)
            {
                // Remember the choice even for rows already in that state: the click is the
                // user's decision, and it changes no property to react to.
                cleanup.SetCheckedExplicitly(selected);
                _pendingCleanupSelections[item.NameKey] = selected;
            }
            else if (item is DiskSpaceRelocationItem relocation && (!selected || relocation.CanCheck))
                relocation.IsChecked = selected;
        }
    }

    private void SetDiskSpaceGroupsExpanded(bool expanded)
    {
        foreach (var group in DiskSpaceGroups)
        {
            group.IsExpanded = expanded;
            _diskSpaceGroupExpanded[group.NameKey] = expanded;
        }
    }

    private void NotifyDiskSpaceLanguageChanged()
    {
        foreach (var group in AllDiskSpaceGroups)
        {
            group.NotifyLanguageChanged();
            foreach (var item in group.Items)
                item.NotifyLanguageChanged();
        }

        RefreshSystemDriveUsage();
        UpdateDiskSpaceSummary();
        foreach (var nav in DiskSpaceGroupNavItems)
            nav.NotifyDisplayChanged();
    }
}
