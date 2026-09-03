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

    [ObservableProperty]
    public partial GroupNavItem? SelectedDiskSpaceGroupNavItem { get; set; }

    [ObservableProperty]
    public partial string DiskSpaceTabHeader { get; set; } = DiskSpaceTabTitle;

    private static string DiskSpaceTabTitle =>
        DiskSpaceItemManager.FormatWithSystemDrive(Localizer.Get("DiskSpace"));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScanDiskSpace))]
    [NotifyPropertyChangedFor(nameof(CanCleanDiskSpace))]
    [NotifyCanExecuteChangedFor(nameof(ScanDiskSpaceCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanCheckedDiskSpaceItemsCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveCheckedDiskSpaceItemsCommand))]
    public partial bool IsDiskSpaceBusy { get; set; }

    [ObservableProperty]
    public partial string DiskSpaceSummaryText { get; set; } = "";

    [ObservableProperty]
    public partial string SystemDriveUsageText { get; set; } = "";

    [ObservableProperty]
    public partial double SystemDriveUsedPercent { get; set; }

    public bool IsDiskSpaceTabSelected => SelectedTabIndex == DiskSpaceTabIndex;

    public bool CanScanDiskSpace => !IsDiskSpaceBusy;

    public bool CanCleanDiskSpace => !IsDiskSpaceBusy && _diskSpaceScannedOnce;

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

        foreach (var item in DiskSpaceItemManager.CreateItems())
        {
            var group = AllDiskSpaceGroups.FirstOrDefault(g => g.NameKey == item.GroupNameKey);
            if (group is null)
                AllDiskSpaceGroups.Add(new DiskSpaceGroup(item.GroupNameKey, [item]));
            else
                group.Items.Add(item);

            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(DiskSpaceCleanupItem.IsChecked))
                    UpdateDiskSpaceSummary();
            };
        }

        RefreshSystemDriveUsage();
        UpdateDiskSpaceSummary();
        RefreshDisplayedDiskSpaceGroups();
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
        IsDiskSpaceBusy = true;
        StatusMessage = Localizer.Get("DiskSpaceScanningAll");
        try
        {
            RefreshSystemDriveUsage();

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
            _diskSpaceScannedOnce = true;
            IsDiskSpaceBusy = false;
            UpdateDiskSpaceSummary();
            StatusMessage = string.Format(
                Localizer.Get("DiskSpaceScanFinished"),
                ByteSize.Format(TotalReclaimableBytes)
            );
        }
    }

    private long TotalReclaimableBytes => DiskSpaceCleanupItems.Sum(item => item.ReclaimableBytes);

    private long CheckedReclaimableBytes =>
        DiskSpaceCleanupItems.Where(item => item.IsChecked).Sum(item => item.ReclaimableBytes);

    private List<DiskSpaceRelocationItem> CheckedRelocationItems =>
        DiskSpaceRelocationItems.Where(item => item.IsChecked && item.CanMove).ToList();

    private long CheckedRelocationBytes => CheckedRelocationItems.Sum(item => item.SizeBytes ?? 0);

    [RelayCommand(CanExecute = nameof(CanCleanDiskSpace))]
    private Task CleanCheckedDiskSpaceItems()
    {
        return CleanDiskSpaceItemsAsync(
            DiskSpaceCleanupItems.Where(item => item.IsChecked).ToList(),
            confirm: true
        );
    }

    /// <summary>
    ///     Cleans the given items one after another. Items already known to hold nothing
    ///     are skipped. Returns bytes freed as measured by each item's re-scan.
    /// </summary>
    public async Task<long> CleanDiskSpaceItemsAsync(
        IReadOnlyList<DiskSpaceCleanupItem> items,
        bool confirm
    )
    {
        EnsureDiskSpaceItems();
        if (IsDiskSpaceBusy)
            return 0;

        var targets = items.Where(item => item.SizeBytes is null || item.ReclaimableBytes > 0).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = Localizer.Get("DiskSpaceNothingToClean");
            return 0;
        }

        if (confirm)
        {
            var estimate = targets.Sum(item => item.ReclaimableBytes);
            var result = await ShowUpdateDialogAsync(
                Localizer.Get("DiskSpaceCleanConfirmTitle"),
                string.Format(
                    Localizer.Get("DiskSpaceCleanConfirmMessage"),
                    targets.Count,
                    ByteSize.Format(estimate)
                ),
                ButtonEnum.YesNo,
                MsBox.Avalonia.Enums.Icon.Question
            );
            if (result != ButtonResult.Yes)
                return 0;
        }

        IsDiskSpaceBusy = true;
        long freed = 0;
        try
        {
            foreach (var item in targets)
            {
                StatusMessage = string.Format(Localizer.Get("DiskSpaceCleaningItem"), item.Name);
                try
                {
                    freed += await item.CleanAsync();
                }
                catch (Exception ex)
                {
                    Log.ZLogError(ex, $"Failed to clean {item.NameKey}");
                }
            }
        }
        finally
        {
            IsDiskSpaceBusy = false;
            RefreshSystemDriveUsage();
            UpdateDiskSpaceSummary();
            StatusMessage = string.Format(
                Localizer.Get("DiskSpaceCleanCompleted"),
                ByteSize.Format(freed)
            );
        }

        return freed;
    }

    [RelayCommand]
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
        if (drive is null || IsDiskSpaceBusy || !item.CanMove)
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

    [RelayCommand(CanExecute = nameof(CanCleanDiskSpace))]
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
        if (IsDiskSpaceBusy)
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

    private async Task<(int Succeeded, int Failed)> RunRelocationBatchAsync(
        IReadOnlyList<(DiskSpaceRelocationItem Item, DriveOption Drive)> moves
    )
    {
        IsDiskSpaceBusy = true;
        var succeeded = 0;
        var failed = 0;
        var rebootNeeded = false;
        string? lastError = null;
        try
        {
            foreach (var (item, drive) in moves)
            {
                StatusMessage = string.Format(Localizer.Get("DiskSpaceMovingItem"), item.Name);
                var ok = false;
                try
                {
                    ok = await item.MoveAsync(drive);
                }
                catch (Exception ex)
                {
                    Log.ZLogError(ex, $"Failed to move {item.NameKey}");
                }

                if (ok)
                {
                    succeeded++;
                    rebootNeeded |= item.RequiresReboot;
                }
                else
                {
                    failed++;
                    lastError = item.ErrorMessage;
                }
            }
        }
        finally
        {
            IsDiskSpaceBusy = false;
            RefreshSystemDriveUsage();
            UpdateDiskSpaceSummary();
            StatusMessage = moves.Count == 1
                ? succeeded == 1
                    ? string.Format(Localizer.Get("DiskSpaceMoveCompleted"), moves[0].Item.Name)
                    : string.Format(Localizer.Get("DiskSpaceMoveFailed"), lastError ?? "")
                : string.Format(Localizer.Get("DiskSpaceBatchMoveCompleted"), succeeded, failed);
        }

        if (rebootNeeded)
            await OptimizationItem.PromptReboot();

        return (succeeded, failed);
    }

    [RelayCommand]
    private Task RestoreDiskSpaceItemDefault(DiskSpaceRelocationItem? item)
    {
        return item is null ? Task.CompletedTask : RestoreDiskSpaceItemDefaultAsync(item, confirm: true);
    }

    public async Task<bool> RestoreDiskSpaceItemDefaultAsync(DiskSpaceRelocationItem item, bool confirm)
    {
        if (IsDiskSpaceBusy || !item.CanRestoreDefault)
            return false;

        if (confirm)
        {
            var result = await ShowUpdateDialogAsync(
                Localizer.Get("DiskSpaceRestoreDefaultConfirmTitle"),
                string.Format(
                    Localizer.Get("DiskSpaceRestoreDefaultConfirmMessage"),
                    item.Name,
                    item.CurrentLocation,
                    item.DefaultLocationText,
                    item.SizeText
                ),
                ButtonEnum.YesNo,
                MsBox.Avalonia.Enums.Icon.Question
            );
            if (result != ButtonResult.Yes)
                return false;
        }

        IsDiskSpaceBusy = true;
        StatusMessage = string.Format(Localizer.Get("DiskSpaceRestoringItem"), item.Name);
        var succeeded = false;
        try
        {
            succeeded = await item.RestoreDefaultAsync();
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Failed to restore {item.NameKey}");
        }
        finally
        {
            IsDiskSpaceBusy = false;
            RefreshSystemDriveUsage();
            UpdateDiskSpaceSummary();
            StatusMessage = succeeded
                ? string.Format(Localizer.Get("DiskSpaceRestoreCompleted"), item.Name)
                : string.Format(Localizer.Get("DiskSpaceRestoreFailed"), item.ErrorMessage ?? "");
        }

        if (succeeded && item.RequiresReboot)
            await OptimizationItem.PromptReboot();

        return succeeded;
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
            var total = ByteSize.Format(TotalReclaimableBytes);
            var summary = string.Format(
                Localizer.Get("DiskSpaceReclaimableSummary"),
                total,
                ByteSize.Format(CheckedReclaimableBytes)
            );
            var moveBytes = CheckedRelocationBytes;
            if (moveBytes > 0)
                summary += "  " + string.Format(Localizer.Get("DiskSpaceMoveSummary"), ByteSize.Format(moveBytes));
            DiskSpaceSummaryText = summary;
            DiskSpaceTabHeader = $"{DiskSpaceTabTitle} ({total})";
        }

        OnPropertyChanged(nameof(CanCleanDiskSpace));
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
