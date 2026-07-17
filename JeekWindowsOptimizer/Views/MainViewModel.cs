using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jeek.Avalonia.Localization;
using JeekTools;
using Microsoft.Extensions.Logging;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using ZLogger;

namespace JeekWindowsOptimizer;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly ILogger Log = LogManager.CreateLogger<MainViewModel>();
    private const string ApplicationTitle = "Jeek Windows Optimizer";
    private const int ToolsTabIndex = 3;
    private bool _uncheckedOptimizationItemsDirty;
    private static readonly char[] SearchTermSeparators = [' ', '\t', '\r', '\n'];
    private static readonly TimeSpan UpdateInitialDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UpdatePollInterval = TimeSpan.FromMinutes(15);
    private DateTime _lastUpdateCheckUtc = DateTime.MinValue;
    private readonly CancellationTokenSource _autoUpdateCancellation = new();
    private bool _autoUpdateLoopStarted;
    private bool _isLoadingAutoUpdateSetting;
    private bool _updateInProgress;
    private bool _suppressOptimizationRefresh;

    // Segoe Fluent Icons (Segoe MDL2 Assets fall-back on Win10): Brightness / QuietHours / Contrast.
    private const string ThemeLightGlyph = "\uE706";
    private const string ThemeDarkGlyph = "\uE708";
    private const string ThemeSystemGlyph = "\uE7A1";

    public MainViewModel()
    {
        Localizer.LanguageChanged += OnLanguageChanged;
        RefreshLanguageMenuCheckState();
        RefreshThemeMenuCheckState();
        RefreshUpdateIntervalMenuCheckState();
        RefreshStorageModeMenuCheckState();
        _isLoadingAutoUpdateSetting = true;
        IsAutoUpdateEnabled = AppSettingsStore.Roaming.AutoUpdate;
        _isLoadingAutoUpdateSetting = false;
        ShowOnlyNotOptimized = AppSettingsStore.Roaming.ShowOnlyNotOptimized;
    }

    public string WindowTitle
    {
        get
        {
            var localCommitCount = AutoUpdate.GetLocalCommitCount();
            return localCommitCount > 0
                ? $"{ApplicationTitle} - {string.Format(Localizer.Get("BuildVersion"), localCommitCount)}"
                : ApplicationTitle;
        }
    }

    public string CurrentVersionMenuHeader =>
        string.Format(Localizer.Get("CurrentVersionHeader"), VersionDisplay);

    private static string VersionDisplay
    {
        get
        {
            var localCommitCount = AutoUpdate.GetLocalCommitCount();
            return localCommitCount > 0
                ? string.Format(Localizer.Get("BuildVersion"), localCommitCount)
                : Localizer.Get("DevelopmentBuild");
        }
    }

    [ObservableProperty]
    public partial bool IsSystemLanguageMenuChecked { get; set; }

    [ObservableProperty]
    public partial bool IsEnglishMenuChecked { get; set; }

    [ObservableProperty]
    public partial bool IsChineseMenuChecked { get; set; }

    [ObservableProperty]
    public partial bool IsLightThemeMenuChecked { get; set; }

    [ObservableProperty]
    public partial bool IsDarkThemeMenuChecked { get; set; }

    [ObservableProperty]
    public partial bool IsSystemThemeMenuChecked { get; set; }

    [ObservableProperty]
    public partial string CurrentThemeGlyph { get; set; } = ThemeSystemGlyph;

    [ObservableProperty]
    public partial bool IsAutoUpdateEnabled { get; set; }

    partial void OnIsAutoUpdateEnabledChanged(bool value)
    {
        if (!_isLoadingAutoUpdateSetting)
            AppSettingsStore.SetAutoUpdate(value);
    }

    [ObservableProperty]
    public partial bool IsUpdateInterval6HoursChecked { get; set; }

    [ObservableProperty]
    public partial bool IsUpdateIntervalDailyChecked { get; set; }

    [ObservableProperty]
    public partial bool IsUpdateIntervalWeeklyChecked { get; set; }

    [ObservableProperty]
    public partial bool IsUpdateIntervalNeverChecked { get; set; }

    [ObservableProperty]
    public partial bool IsStorageModeDefaultChecked { get; set; }

    [ObservableProperty]
    public partial bool IsStorageModePortableChecked { get; set; }

    [ObservableProperty]
    public partial bool IsStorageModeCustomChecked { get; set; }

    private void RefreshLanguageMenuCheckState()
    {
        var followSystem = AppSettingsStore.IsFollowSystemLanguage;
        IsSystemLanguageMenuChecked = followSystem;
        IsEnglishMenuChecked =
            !followSystem
            && string.Equals(Localizer.Language, "en", StringComparison.OrdinalIgnoreCase);
        IsChineseMenuChecked =
            !followSystem
            && string.Equals(Localizer.Language, "zh", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshUpdateIntervalMenuCheckState()
    {
        var interval = AppSettingsStore.Roaming.AutoUpdateInterval;
        IsUpdateInterval6HoursChecked = interval == AutoUpdateInterval.Every6Hours;
        IsUpdateIntervalDailyChecked = interval == AutoUpdateInterval.Daily;
        IsUpdateIntervalWeeklyChecked = interval == AutoUpdateInterval.Weekly;
        IsUpdateIntervalNeverChecked = interval == AutoUpdateInterval.Never;
    }

    public void RefreshStorageModeMenuCheckState()
    {
        var mode = AppSettingsStore.EffectiveStorageMode;
        IsStorageModeDefaultChecked = mode == StorageMode.Default;
        IsStorageModePortableChecked = mode == StorageMode.Portable;
        IsStorageModeCustomChecked = mode == StorageMode.Custom;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLanguageMenuCheckState();
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(CurrentVersionMenuHeader));
    }

    private void RefreshThemeMenuCheckState()
    {
        var v = Application.Current?.RequestedThemeVariant ?? ThemeVariant.Default;
        IsLightThemeMenuChecked = v == ThemeVariant.Light;
        IsDarkThemeMenuChecked = v == ThemeVariant.Dark;
        IsSystemThemeMenuChecked = v == ThemeVariant.Default;
        CurrentThemeGlyph =
            v == ThemeVariant.Light ? ThemeLightGlyph
            : v == ThemeVariant.Dark ? ThemeDarkGlyph
            : ThemeSystemGlyph;
    }

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    private OptimizationItemCategory _selectedCategory;

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsOptimizationTabSelected));
        OnPropertyChanged(nameof(IsToolsTabSelected));
        OnPropertyChanged(nameof(CanShowOptimizeButton));
        OnPropertyChanged(nameof(IsNoSearchResultsVisible));
        OnPropertyChanged(nameof(SelectedTabDescription));

        if (value != ToolsTabIndex)
            _selectedCategory = (OptimizationItemCategory)value;

        RefreshDisplayedGroups();
        UpdateOptimizeButtonText();
    }

    public string SelectedTabDescription =>
        SelectedTabIndex switch
        {
            0 => Localizer.Get("OptimizingDescription"),
            1 => Localizer.Get("AntivirusDescription"),
            2 => Localizer.Get("PersonalDescription"),
            ToolsTabIndex => Localizer.Get("ToolsDescription"),
            _ => "",
        };

    public FastObservableCollection<OptimizationGroup> Groups { get; } = [];
    public List<OptimizationGroup> OptimizingGroups { get; } = [];
    public List<OptimizationGroup> AntivirusGroups { get; } = [];
    public List<OptimizationGroup> PersonalGroups { get; } = [];
    public List<ToolGroup> AllToolGroups { get; } = [];
    public FastObservableCollection<ToolGroup> ToolGroups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchActive))]
    [NotifyPropertyChangedFor(nameof(IsNoSearchResultsVisible))]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoSearchResultsVisible))]
    public partial bool IsSearchVisible { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoSearchResultsVisible))]
    public partial bool ShowOnlyNotOptimized { get; set; }

    public bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchText);

    public bool IsNoSearchResultsVisible =>
        !IsBusy
        && (
            IsOptimizationTabSelected
                && Groups.Count == 0
                && (IsSearchActive || ShowOnlyNotOptimized)
            || IsToolsTabSelected && ToolGroups.Count == 0 && IsSearchActive
        );

    public string NoResultsMessage =>
        IsSearchActive
            ? Localizer.Get("NoSearchResultsInCurrentTab")
            : Localizer.Get("NoUnoptimizedItemsInCurrentTab");

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = Localizer.Get("Initializing");

    [ObservableProperty]
    public partial bool IsBusy { get; set; } = true;

    public bool IsOptimizationTabSelected => SelectedTabIndex != ToolsTabIndex;
    public bool IsToolsTabSelected => SelectedTabIndex == ToolsTabIndex;
    public bool CanShowOptimizeButton => !IsBusy && IsOptimizationTabSelected;

    public bool AreOptimizationItemControlsEnabled => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanShowOptimizeButton));
        OnPropertyChanged(nameof(AreOptimizationItemControlsEnabled));
        OnPropertyChanged(nameof(IsNoSearchResultsVisible));
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
    }

    partial void OnSearchTextChanged(string value)
    {
        RefreshDisplayedGroups();
        UpdateOptimizationTabHeaders();
        ClearSearchCommand.NotifyCanExecuteChanged();
        UpdateOptimizeButtonText();
        OnPropertyChanged(nameof(NoResultsMessage));
    }

    partial void OnShowOnlyNotOptimizedChanged(bool value)
    {
        AppSettingsStore.SetShowOnlyNotOptimized(value);
        if (IsOptimizationTabSelected)
        {
            RefreshDisplayedOptimizationGroups();
            UpdateOptimizeButtonText();
        }
        OnPropertyChanged(nameof(IsNoSearchResultsVisible));
        OnPropertyChanged(nameof(NoResultsMessage));
    }

    [RelayCommand]
    private void ToggleShowOnlyNotOptimized()
    {
        ShowOnlyNotOptimized = !ShowOnlyNotOptimized;
    }

    [RelayCommand]
    private async Task Loaded()
    {
        await InitializeItems();
        StartAutoUpdateLoop();

        Localizer.LanguageChanged += (_, _) =>
        {
            foreach (var group in GetAllOptimizationGroups())
            {
                group.NotifyLanguageChanged();

                foreach (var item in group.Items)
                    item.NotifyLanguageChanged();
            }

            foreach (var group in AllToolGroups)
            {
                group.NotifyLanguageChanged();

                foreach (var item in group.Items)
                    item.NotifyLanguageChanged();
            }

            RefreshDisplayedGroups();
            UpdateOptimizationTabHeaders();
            UpdateOptimizeButtonText();
            ToolsTabHeader = Localizer.Get("Tools");
            OnPropertyChanged(nameof(NoResultsMessage));
            OnPropertyChanged(nameof(SelectedTabDescription));
        };
    }

    private async Task InitializeItems()
    {
        try
        {
            if (Design.IsDesignMode)
            {
                Groups.Add(new OptimizationGroup("System", [new TestItem(), new TestItem()]));
                Groups.Add(new OptimizationGroup("System", [new TestItem(), new TestItem()]));
                ToolGroups.Add(
                    new ToolGroup(
                        "Tools",
                        [
                            new ToolItem(
                                "Tools",
                                "CrystalDiskInfoName",
                                "CrystalDiskInfoDescription",
                                ToolExecutionKind.PackagedExecutable,
                                @"CrystalDiskInfo\DiskInfo64.exe",
                                "",
                                false,
                                false,
                                false,
                                false
                            ),
                        ]
                    )
                );
                return;
            }

            _suppressOptimizationRefresh = true;

            await RegistryItemManager.Load();
            foreach (var item in RegistryItemManager.Items)
                AddOptimizationItem(item);

            await DriverItemManager.Load();
            foreach (var item in DriverItemManager.Items)
                AddOptimizationItem(item);

            AddOptimizationItem(new DisableWindowsDefenderPUAProtectionItem());
            foreach (var item in VisualEffectItem.CreateItems())
                AddOptimizationItem(item);
            AddOptimizationItem(new ShowThumbnailsItem());
            AddOptimizationItem(new UseClassicalContextMenuItem());
            AddOptimizationItem(new UninstallOneDriveItem());
            AddOptimizationItem(new WindowsActivatorItem());
            AddOptimizationItem(new WindowsUpdateItem());
            if (!await Battery.HasBatteryAsync())
                AddOptimizationItem(new BestPerformancePowerModeItem());
            AddOptimizationItem(new SetIdleTimeItem());
            AddOptimizationItem(new DisableSystemSounds());
            AddOptimizationItem(new DisableHibernationItem());
            AddOptimizationItem(new DisableNtfsLastAccessUpdateItem());
            AddOptimizationItem(new DisableSearchHistoryItem());

            await ServiceItemManager.Load();
            foreach (var item in ServiceItemManager.Items)
                AddOptimizationItem(item);

            await ScheduledTaskItemManager.Load();
            foreach (var item in ScheduledTaskItemManager.Items)
                AddOptimizationItem(item);

            await MicrosoftStore.Initialize();
            AddOptimizationItem(new PreventStartMenuFromSearchingMicrosoftStoreItem());
            await MicrosoftStoreItemManager.Load();
            foreach (var item in MicrosoftStoreItemManager.Items)
                AddOptimizationItem(item);
            AddOptimizationItem(new WindowsTerminalUseNewWindow());

            RestoreUncheckedOptimizationItems();

            await ToolItemManager.Load();
            foreach (var item in ToolItemManager.Items)
                AddToolItem(item);

            foreach (var group in OptimizingGroups.Concat(AntivirusGroups).Concat(PersonalGroups))
            {
                foreach (var item in group.Items)
                {
                    try
                    {
                        await item.Initialize();
                    }
                    catch (Exception ex)
                    {
                        Log.ZLogError(ex, $"Failed to initialize {item.Name}");
                    }
                }
            }

            _suppressOptimizationRefresh = false;
            RefreshDisplayedOptimizationGroups();
            UpdateOptimizationTabHeaders();
            UpdateOptimizeButtonText();

            IsBusy = false;
            StatusMessage = Localizer.Get("InitializationFinished");
        }
        catch (Exception ex)
        {
            _suppressOptimizationRefresh = false;
            Log.ZLogError(ex, $"Failed to initialize items");
            IsBusy = false;
            StatusMessage = Localizer.Get("InitializationFailed");
        }
    }

    private IEnumerable<OptimizationItem> GetOptimizationItems()
    {
        return GetAllOptimizationGroups().SelectMany(group => group.Items);
    }

    private IEnumerable<OptimizationGroup> GetAllOptimizationGroups()
    {
        return OptimizingGroups.Concat(AntivirusGroups).Concat(PersonalGroups);
    }

    private void RestoreUncheckedOptimizationItems()
    {
        var uncheckedNameKeys = AppSettingsStore.GetUncheckedOptimizationItemNameKeys();

        foreach (var item in GetOptimizationItems())
        {
            item.IsChecked = !uncheckedNameKeys.Contains(item.NameKey);
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(OptimizationItem.IsChecked))
                {
                    _uncheckedOptimizationItemsDirty = true;
                    UpdateOptimizeButtonText();
                }
            };
        }

        _uncheckedOptimizationItemsDirty = false;
        UpdateOptimizeButtonText();
    }

    public void SaveUncheckedOptimizationItemsIfChanged()
    {
        if (!_uncheckedOptimizationItemsDirty)
            return;

        var uncheckedNameKeys = GetOptimizationItems()
            .Where(item => !item.IsChecked)
            .Select(item => item.NameKey);
        AppSettingsStore.SetUncheckedOptimizationItemNameKeys(uncheckedNameKeys);
        _uncheckedOptimizationItemsDirty = false;
    }

    private void AddOptimizationItem(OptimizationItem item)
    {
        var groups = item.Category switch
        {
            OptimizationItemCategory.Default => OptimizingGroups,
            OptimizationItemCategory.Antivirus => AntivirusGroups,
            OptimizationItemCategory.Personal => PersonalGroups,
            _ => throw new Exception("Invalid optimization item category"),
        };

        var isNewGroup = true;
        foreach (var group in groups)
        {
            if (group.NameKey == item.GroupNameKey)
            {
                group.Items.Add(item);
                isNewGroup = false;
                break;
            }
        }

        if (isNewGroup)
        {
            var newGroup = new OptimizationGroup(item.GroupNameKey, [item]);
            groups.Add(newGroup);

            if (item.Category == _selectedCategory)
                Groups.Add(newGroup);
        }

        if (_suppressOptimizationRefresh)
            return;

        UpdateItemStat(item.Category);
        if (item.Category == _selectedCategory)
        {
            RefreshDisplayedOptimizationGroups();
            UpdateOptimizeButtonText();
        }
    }

    private void AddToolItem(ToolItem item)
    {
        foreach (var group in AllToolGroups)
        {
            if (group.NameKey == item.GroupNameKey)
            {
                group.Items.Add(item);
                if (SelectedTabIndex == ToolsTabIndex)
                    RefreshDisplayedToolGroups();
                return;
            }
        }

        AllToolGroups.Add(new ToolGroup(item.GroupNameKey, [item]));
        if (SelectedTabIndex == ToolsTabIndex)
            RefreshDisplayedToolGroups();
    }

    private IEnumerable<OptimizationGroup> GetSourceOptimizationGroups(
        OptimizationItemCategory category
    )
    {
        return category switch
        {
            OptimizationItemCategory.Default => OptimizingGroups,
            OptimizationItemCategory.Antivirus => AntivirusGroups,
            OptimizationItemCategory.Personal => PersonalGroups,
            _ => throw new Exception("Invalid optimization item category"),
        };
    }

    private void RefreshDisplayedGroups()
    {
        if (SelectedTabIndex == ToolsTabIndex)
            RefreshDisplayedToolGroups();
        else
            RefreshDisplayedOptimizationGroups();
    }

    private void RefreshDisplayedOptimizationGroups()
    {
        Groups.Replace(GetDisplayedOptimizationGroups(_selectedCategory));
        OnPropertyChanged(nameof(IsNoSearchResultsVisible));
    }

    private void RefreshDisplayedToolGroups()
    {
        if (!IsSearchActive)
        {
            ToolGroups.Replace(AllToolGroups);
            OnPropertyChanged(nameof(IsNoSearchResultsVisible));
            return;
        }

        var terms = GetSearchTerms();
        var filteredGroups = AllToolGroups
            .Select(group =>
            {
                var items = group
                    .Items.Where(item =>
                        MatchesSearch(terms, group.Name, item.Name, item.Description)
                    )
                    .ToArray();
                return new ToolGroup(group.NameKey, items);
            })
            .Where(group => group.Items.Count > 0);

        ToolGroups.Replace(filteredGroups);
        OnPropertyChanged(nameof(IsNoSearchResultsVisible));
    }

    private IEnumerable<OptimizationGroup> GetDisplayedOptimizationGroups(
        OptimizationItemCategory category
    )
    {
        return GetFilteredOptimizationGroups(
            category,
            applySearch: true,
            applyNotOptimizedFilter: true
        );
    }

    /// <summary>
    /// Tab headers count optimized/total without the "only not optimized" view filter,
    /// so progress still reflects the full (search-filtered) category.
    /// </summary>
    private IEnumerable<OptimizationGroup> GetStatOptimizationGroups(
        OptimizationItemCategory category
    )
    {
        return GetFilteredOptimizationGroups(
            category,
            applySearch: true,
            applyNotOptimizedFilter: false
        );
    }

    private IEnumerable<OptimizationGroup> GetFilteredOptimizationGroups(
        OptimizationItemCategory category,
        bool applySearch,
        bool applyNotOptimizedFilter
    )
    {
        var sourceGroups = GetSourceOptimizationGroups(category).ToList();
        var terms = applySearch && IsSearchActive ? GetSearchTerms() : null;
        var onlyNotOptimized = applyNotOptimizedFilter && ShowOnlyNotOptimized;

        if (terms is null && !onlyNotOptimized)
            return sourceGroups;

        return sourceGroups
            .Select(group =>
            {
                var items = group
                    .Items.Where(item =>
                    {
                        if (onlyNotOptimized && item.IsOptimized)
                            return false;
                        if (
                            terms is not null
                            && !MatchesSearch(terms, group.Name, item.Name, item.Description)
                        )
                            return false;
                        return true;
                    })
                    .ToArray();
                return new OptimizationGroup(group.NameKey, items);
            })
            .Where(group => group.Items.Count > 0);
    }

    private string[] GetSearchTerms()
    {
        return SearchText.Trim().Split(SearchTermSeparators, StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool MatchesSearch(IEnumerable<string> terms, params string[] fields)
    {
        return terms.All(term =>
            fields.Any(field => field.Contains(term, StringComparison.CurrentCultureIgnoreCase))
        );
    }

    public void UpdateItemStat(OptimizationItemCategory category)
    {
        var groups = GetStatOptimizationGroups(category).ToList();

        var totalItemsCount = groups.Sum(group => group.Items.Count);
        var optimizedItemCount = groups.Sum(group => group.Items.Count(it => it.IsOptimized));

        // Update the tab header
        if (category == OptimizationItemCategory.Personal)
            PersonalTabHeader =
                $"{Localizer.Get("Personal")} ({optimizedItemCount}/{totalItemsCount})";
        else if (category == OptimizationItemCategory.Antivirus)
            AntivirusTabHeader =
                $"{Localizer.Get("Antivirus")} ({optimizedItemCount}/{totalItemsCount})";
        else
            OptimizingTabHeader =
                $"{Localizer.Get("Optimizing")} ({optimizedItemCount}/{totalItemsCount})";
    }

    private void UpdateOptimizationTabHeaders()
    {
        UpdateItemStat(OptimizationItemCategory.Default);
        UpdateItemStat(OptimizationItemCategory.Antivirus);
        UpdateItemStat(OptimizationItemCategory.Personal);
    }

    [ObservableProperty]
    public partial string OptimizingTabHeader { get; set; } = Localizer.Get("Optimizing");

    [ObservableProperty]
    public partial string AntivirusTabHeader { get; set; } = Localizer.Get("Antivirus");

    [ObservableProperty]
    public partial string PersonalTabHeader { get; set; } = Localizer.Get("Personal");

    [ObservableProperty]
    public partial string ToolsTabHeader { get; set; } = Localizer.Get("Tools");

    [ObservableProperty]
    public partial string OptimizeButtonText { get; set; } = Localizer.Get("OptimizeSelectedItems");

    private void UpdateOptimizeButtonText()
    {
        if (SelectedTabIndex == ToolsTabIndex)
            return;

        var uncheckedItemCount = Groups.Sum(group => group.Items.Count(it => !it.IsChecked));
        OptimizeButtonText =
            uncheckedItemCount == 0
                ? Localizer.Get("OptimizeSelectedItems")
                : string.Format(
                    Localizer.Get("OptimizeSelectedItemsWithExcludedCount"),
                    uncheckedItemCount
                );
    }

    public async Task OptimizeCheckedItems()
    {
        OptimizationItem.InBatching = true;
        IsBusy = true;
        var itemsToOptimize = Groups
            .SelectMany(group => group.Items)
            .Where(item => item.IsChecked && !item.IsOptimized)
            .ToList();

        try
        {
            StatusMessage = Localizer.Get("PreOptimizationPreparations");

            var shouldTurnOffTamperProtection = false;
            var shouldTurnOffOnAccessProtection = false;

            foreach (var item in itemsToOptimize)
            {
                shouldTurnOffTamperProtection |= item.ShouldTurnOffTamperProtection;
                shouldTurnOffOnAccessProtection |= item.ShouldTurnOffOnAccessProtection;
            }

            if (shouldTurnOffTamperProtection)
                if (!await OptimizationItem.TurnOffTamperProtection())
                    return;

            if (shouldTurnOffOnAccessProtection)
                if (!await OptimizationItem.TurnOffOnAccessProtection())
                    return;

            var shouldUpdateGroupPolicy = false;
            var shouldReboot = false;
            var shouldRestartExplorer = false;

            foreach (var item in itemsToOptimize)
            {
                StatusMessage = string.Format(Localizer.Get("OptimizingItem"), item.Name);
                try
                {
                    await item.SetIsOptimized(true);
                    UpdateItemStat(item.Category);
                }
                catch (Exception ex)
                {
                    Log.ZLogError(ex, $"Failed to optimize: {item.Name}");
                    continue;
                }

                shouldUpdateGroupPolicy |= item.ShouldUpdateGroupPolicy;
                shouldReboot |= item.ShouldReboot;
                shouldRestartExplorer |= item.ShouldRestartExplorer;
            }

            StatusMessage = Localizer.Get("PostOptimizationProcessing");

            if (shouldUpdateGroupPolicy)
                await OptimizationItem.UpdateGroupPolicy();

            if (shouldRestartExplorer)
                await OptimizationItem.RestartExplorer();

            if (shouldReboot)
                await OptimizationItem.PromptReboot();
        }
        finally
        {
            OptimizationItem.InBatching = false;
            IsBusy = false;
            StatusMessage = Localizer.Get("OptimizationCompleted");
            if (ShowOnlyNotOptimized)
                RefreshDisplayedOptimizationGroups();
            UpdateOptimizeButtonText();
        }
    }

    /// <summary>
    /// Call after an item's <see cref="OptimizationItem.IsOptimized"/> changes so the
    /// "show only not optimized" filter can hide newly optimized rows.
    /// </summary>
    public void OnOptimizationItemStatusChanged(OptimizationItemCategory category)
    {
        UpdateItemStat(category);
        if (ShowOnlyNotOptimized && category == _selectedCategory)
        {
            RefreshDisplayedOptimizationGroups();
            UpdateOptimizeButtonText();
        }
    }

    private void StartAutoUpdateLoop()
    {
        if (_autoUpdateLoopStarted || Design.IsDesignMode)
            return;

        _autoUpdateLoopStarted = true;
        _ = RunAutoUpdateLoopAsync(_autoUpdateCancellation.Token);
    }

    private async Task RunAutoUpdateLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(UpdateInitialDelay, cancellationToken);

            // Always check once at startup when auto-update is enabled.
            if (IsAutoUpdateEnabled && !IsBusy)
            {
                await CheckForUpdatesAsync(manual: false);
                _lastUpdateCheckUtc = DateTime.UtcNow;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(UpdatePollInterval, cancellationToken);

                if (!IsAutoUpdateEnabled || IsBusy)
                    continue;

                var interval = AppSettingsStore.Roaming.AutoUpdateInterval;
                if (interval == AutoUpdateInterval.Never)
                    continue;

                if (DateTime.UtcNow - _lastUpdateCheckUtc < IntervalToTimeSpan(interval))
                    continue;

                await CheckForUpdatesAsync(manual: false);
                _lastUpdateCheckUtc = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException) { }
    }

    private static TimeSpan IntervalToTimeSpan(AutoUpdateInterval interval)
    {
        return interval switch
        {
            AutoUpdateInterval.Every6Hours => TimeSpan.FromHours(6),
            AutoUpdateInterval.Daily => TimeSpan.FromDays(1),
            AutoUpdateInterval.Weekly => TimeSpan.FromDays(7),
            _ => TimeSpan.FromDays(1),
        };
    }

    private bool CanCheckForUpdates()
    {
        return !_updateInProgress && !IsBusy;
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdates()
    {
        await CheckForUpdatesAsync(manual: true);
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateInProgress)
            return;

        _updateInProgress = true;
        CheckForUpdatesCommand.NotifyCanExecuteChanged();

        try
        {
            if (manual)
                StatusMessage = Localizer.Get("UpdateChecking");

            Log.ZLogInformation($"AutoUpdate check started (manual={manual})");
            var outcome = await AutoUpdate.HasUpdateAsync(
                AppSettingsStore.Roaming.DisableMirrorDownload
            );

            switch (outcome)
            {
                case UpdateCheckOutcome.Available:
                    await ConfirmAndLaunchUpdateAsync();
                    break;

                case UpdateCheckOutcome.UpToDate when manual:
                    await ShowUpdateDialogAsync(
                        Localizer.Get("UpdateNoneTitle"),
                        Localizer.Get("UpdateNoneMessage"),
                        ButtonEnum.Ok,
                        MsBox.Avalonia.Enums.Icon.Info
                    );
                    break;

                case UpdateCheckOutcome.Failed when manual:
                    await ShowUpdateDialogAsync(
                        Localizer.Get("UpdateFailedTitle"),
                        string.Format(
                            Localizer.Get("UpdateFailedMessage"),
                            string.IsNullOrEmpty(AutoUpdate.FailureReason)
                                ? "unknown"
                                : AutoUpdate.FailureReason
                        ),
                        ButtonEnum.Ok,
                        MsBox.Avalonia.Enums.Icon.Warning
                    );
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "AutoUpdate check threw");
            if (manual)
            {
                await ShowUpdateDialogAsync(
                    Localizer.Get("UpdateFailedTitle"),
                    string.Format(Localizer.Get("UpdateFailedMessage"), ex.Message),
                    ButtonEnum.Ok,
                    MsBox.Avalonia.Enums.Icon.Warning
                );
            }
        }
        finally
        {
            _updateInProgress = false;
            CheckForUpdatesCommand.NotifyCanExecuteChanged();

            if (manual)
                StatusMessage = Localizer.Get("UpdateCheckFinished");
        }
    }

    private async Task ConfirmAndLaunchUpdateAsync()
    {
        var remoteVersion =
            AutoUpdate.RemoteCommitCount > 0 ? AutoUpdate.RemoteCommitCount.ToString() : "?";
        var result = await ShowUpdateDialogAsync(
            Localizer.Get("UpdateAvailableTitle"),
            string.Format(Localizer.Get("UpdateAvailableMessage"), remoteVersion),
            ButtonEnum.OkCancel,
            MsBox.Avalonia.Enums.Icon.Info
        );

        if (result != ButtonResult.Ok)
            return;

        if (!AutoUpdate.LaunchUpdate())
        {
            await ShowUpdateDialogAsync(
                Localizer.Get("UpdateFailedTitle"),
                Localizer.Get("UpdateLaunchFailedMessage"),
                ButtonEnum.Ok,
                MsBox.Avalonia.Enums.Icon.Warning
            );
            return;
        }

        ShutdownApplication();
    }

    private static async Task<ButtonResult> ShowUpdateDialogAsync(
        string title,
        string message,
        ButtonEnum buttons,
        MsBox.Avalonia.Enums.Icon icon
    )
    {
        return await MessageBoxManager
            .GetMessageBoxStandard(
                new MessageBoxStandardParams
                {
                    ContentTitle = title,
                    ContentMessage = message,
                    ButtonDefinitions = buttons,
                    Icon = icon,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Topmost = true,
                    FontFamily = Localizer.Get("DefaultFontName"),
                }
            )
            .ShowAsync();
    }

    private static void ShutdownApplication()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Environment.Exit(0);
    }

    [RelayCommand]
    private void SwitchToSystemLanguage()
    {
        AppSettingsStore.SetLanguage(null);
        Localizer.Language = AppSettingsStore.ResolveEffectiveLanguage(Localizer.Languages);
        RefreshLanguageMenuCheckState();
    }

    [RelayCommand]
    private void SwitchToEnglish()
    {
        Localizer.Language = "en";
        AppSettingsStore.SetLanguage("en");
    }

    [RelayCommand]
    private void SwitchToChinese()
    {
        Localizer.Language = "zh";
        AppSettingsStore.SetLanguage("zh");
    }

    [RelayCommand]
    private void SwitchToLightTheme()
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
            AppSettingsStore.SetThemeVariant(ThemeVariant.Light);
        }
        RefreshThemeMenuCheckState();
    }

    [RelayCommand]
    private void SwitchToDarkTheme()
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = ThemeVariant.Dark;
            AppSettingsStore.SetThemeVariant(ThemeVariant.Dark);
        }
        RefreshThemeMenuCheckState();
    }

    [RelayCommand]
    private void SwitchToSystemTheme()
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = ThemeVariant.Default;
            AppSettingsStore.SetThemeVariant(ThemeVariant.Default);
        }
        RefreshThemeMenuCheckState();
    }

    private void ApplyUpdateInterval(AutoUpdateInterval interval)
    {
        AppSettingsStore.SetAutoUpdateInterval(interval);
        RefreshUpdateIntervalMenuCheckState();
    }

    [RelayCommand]
    private void SetUpdateIntervalTo6Hours()
    {
        ApplyUpdateInterval(AutoUpdateInterval.Every6Hours);
    }

    [RelayCommand]
    private void SetUpdateIntervalToDaily()
    {
        ApplyUpdateInterval(AutoUpdateInterval.Daily);
    }

    [RelayCommand]
    private void SetUpdateIntervalToWeekly()
    {
        ApplyUpdateInterval(AutoUpdateInterval.Weekly);
    }

    [RelayCommand]
    private void SetUpdateIntervalToNever()
    {
        ApplyUpdateInterval(AutoUpdateInterval.Never);
    }

    [RelayCommand]
    private void SwitchToDefaultStorage()
    {
        AppSettingsStore.SwitchStorageMode(StorageMode.Default);
        RefreshStorageModeMenuCheckState();
    }

    [RelayCommand]
    private void SwitchToPortableStorage()
    {
        AppSettingsStore.SwitchStorageMode(StorageMode.Portable);
        RefreshStorageModeMenuCheckState();
    }

    private bool CanClearSearch()
    {
        return IsSearchActive;
    }

    [RelayCommand(CanExecute = nameof(CanClearSearch))]
    private void ClearSearch()
    {
        SearchText = "";
    }

    [RelayCommand]
    private void ShowSearch()
    {
        IsSearchVisible = true;
    }

    [RelayCommand]
    private void ToggleSearch()
    {
        if (IsSearchVisible)
            ExitSearch();
        else
            ShowSearch();
    }

    [RelayCommand]
    private void ExitSearch()
    {
        SearchText = "";
        IsSearchVisible = false;
    }

    public void Dispose()
    {
        Localizer.LanguageChanged -= OnLanguageChanged;
        _autoUpdateCancellation.Cancel();
        _autoUpdateCancellation.Dispose();
    }
}
