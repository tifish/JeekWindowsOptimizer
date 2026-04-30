using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jeek.Avalonia.Localization;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

public partial class MainViewModel : ObservableObject
{
    private static readonly ILogger Log = LogManager.CreateLogger<MainViewModel>();
    private const int ToolsTabIndex = 3;

    public MainViewModel()
    {
        Localizer.LanguageChanged += (_, _) => RefreshLanguageMenuCheckState();
        RefreshLanguageMenuCheckState();
        RefreshThemeMenuCheckState();
    }

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

    private void RefreshLanguageMenuCheckState()
    {
        IsEnglishMenuChecked = string.Equals(
            Localizer.Language,
            "en",
            StringComparison.OrdinalIgnoreCase
        );
        IsChineseMenuChecked = string.Equals(
            Localizer.Language,
            "zh",
            StringComparison.OrdinalIgnoreCase
        );
    }

    private void RefreshThemeMenuCheckState()
    {
        var v = Application.Current?.RequestedThemeVariant ?? ThemeVariant.Default;
        IsLightThemeMenuChecked = v == ThemeVariant.Light;
        IsDarkThemeMenuChecked = v == ThemeVariant.Dark;
        IsSystemThemeMenuChecked = v == ThemeVariant.Default;
    }

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    private OptimizationItemCategory _selectedCategory;

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsOptimizationTabSelected));
        OnPropertyChanged(nameof(IsToolsTabSelected));
        OnPropertyChanged(nameof(CanShowOptimizeButton));

        if (value == ToolsTabIndex)
            return;

        _selectedCategory = (OptimizationItemCategory)value;
        var selectedGroup = _selectedCategory switch
        {
            OptimizationItemCategory.Default => OptimizingGroups,
            OptimizationItemCategory.Antivirus => AntivirusGroups,
            OptimizationItemCategory.Personal => PersonalGroups,
            _ => throw new Exception("Invalid optimization item category"),
        };
        Groups.Replace(selectedGroup);
    }

    public FastObservableCollection<OptimizationGroup> Groups { get; } = [];
    public List<OptimizationGroup> OptimizingGroups { get; } = [];
    public List<OptimizationGroup> AntivirusGroups { get; } = [];
    public List<OptimizationGroup> PersonalGroups { get; } = [];
    public FastObservableCollection<ToolGroup> ToolGroups { get; } = [];

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = Localizer.Get("Initializing");

    [ObservableProperty]
    public partial bool IsBusy { get; set; } = true;

    public bool IsOptimizationTabSelected => SelectedTabIndex != ToolsTabIndex;
    public bool IsToolsTabSelected => SelectedTabIndex == ToolsTabIndex;
    public bool CanShowOptimizeButton => !IsBusy && IsOptimizationTabSelected;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanShowOptimizeButton));
    }

    [RelayCommand]
    private async Task Loaded()
    {
        await InitializeItems();

        Localizer.LanguageChanged += (_, _) =>
        {
            foreach (var group in Groups)
            {
                group.NotifyLanguageChanged();

                foreach (var item in group.Items)
                    item.NotifyLanguageChanged();
            }

            UpdateItemStat(OptimizationItemCategory.Default);
            UpdateItemStat(OptimizationItemCategory.Antivirus);
            UpdateItemStat(OptimizationItemCategory.Personal);
            ToolsTabHeader = Localizer.Get("Tools");

            foreach (var group in ToolGroups)
            {
                group.NotifyLanguageChanged();

                foreach (var item in group.Items)
                    item.NotifyLanguageChanged();
            }
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
                                @"CrystalDiskInfo\DiskInfo64.exe",
                                "",
                                false,
                                false
                            ),
                        ]
                    )
                );
                return;
            }

            await RegistryItemManager.Load();
            foreach (var item in RegistryItemManager.Items)
                AddOptimizationItem(item);

            await DriverItemManager.Load();
            foreach (var item in DriverItemManager.Items)
                AddOptimizationItem(item);

            AddOptimizationItem(new DisableWindowsDefenderPUAProtectionItem());
            AddOptimizationItem(new VisualEffectsItem());
            AddOptimizationItem(new DisableThumbnailsItem());
            AddOptimizationItem(new UseClassicalContextMenuItem());
            AddOptimizationItem(new UninstallOneDriveItem());
            AddOptimizationItem(new WindowsActivatorItem());
            AddOptimizationItem(new WindowsUpdateItem());
            if (!Battery.HasBattery())
                AddOptimizationItem(new BestPerformancePowerModeItem());
            AddOptimizationItem(new SetIdleTimeItem());
            AddOptimizationItem(new DisableSystemSounds());

            await ServiceItemManager.Load();
            foreach (var item in ServiceItemManager.Items)
                AddOptimizationItem(item);

            await MicrosoftStore.Initialize();
            await MicrosoftStoreItemManager.Load();
            foreach (var item in MicrosoftStoreItemManager.Items)
                AddOptimizationItem(item);
            AddOptimizationItem(new WindowsTerminalUseNewWindow());

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

                    UpdateItemStat(item.Category);
                }
            }

            IsBusy = false;
            StatusMessage = Localizer.Get("InitializationFinished");
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Failed to initialize items");
            IsBusy = false;
            StatusMessage = Localizer.Get("InitializationFailed");
        }
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

        UpdateItemStat(item.Category);
    }

    private void AddToolItem(ToolItem item)
    {
        foreach (var group in ToolGroups)
        {
            if (group.NameKey == item.GroupNameKey)
            {
                group.Items.Add(item);
                return;
            }
        }

        ToolGroups.Add(new ToolGroup(item.GroupNameKey, [item]));
    }

    public void UpdateItemStat(OptimizationItemCategory category)
    {
        var groups = category switch
        {
            OptimizationItemCategory.Default => OptimizingGroups,
            OptimizationItemCategory.Antivirus => AntivirusGroups,
            OptimizationItemCategory.Personal => PersonalGroups,
            _ => throw new Exception("Invalid optimization item category"),
        };

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

    [ObservableProperty]
    public partial string OptimizingTabHeader { get; set; } = Localizer.Get("Optimizing");

    [ObservableProperty]
    public partial string AntivirusTabHeader { get; set; } = Localizer.Get("Antivirus");

    [ObservableProperty]
    public partial string PersonalTabHeader { get; set; } = Localizer.Get("Personal");

    [ObservableProperty]
    public partial string ToolsTabHeader { get; set; } = Localizer.Get("Tools");

    public async Task OptimizeCheckedItems()
    {
        OptimizationItem.InBatching = true;
        IsBusy = true;

        var groups = _selectedCategory switch
        {
            OptimizationItemCategory.Default => OptimizingGroups,
            OptimizationItemCategory.Personal => PersonalGroups,
            OptimizationItemCategory.Antivirus => AntivirusGroups,
            _ => throw new Exception("Invalid optimization item category"),
        };

        try
        {
            StatusMessage = Localizer.Get("PreOptimizationPreparations");

            var shouldTurnOffTamperProtection = false;
            var shouldTurnOffOnAccessProtection = false;

            foreach (var group in groups)
            foreach (var item in group.Items)
            {
                if (!item.IsChecked || item.IsOptimized)
                    continue;

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

            foreach (var group in groups)
            foreach (var item in group.Items)
            {
                if (!item.IsChecked || item.IsOptimized)
                    continue;

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
                OptimizationItem.RestartExplorer();

            if (shouldReboot)
                await OptimizationItem.PromptReboot();
        }
        finally
        {
            OptimizationItem.InBatching = false;
            IsBusy = false;
            StatusMessage = Localizer.Get("OptimizationCompleted");
        }
    }

    [RelayCommand]
    private void SwitchToEnglish()
    {
        Localizer.Language = "en";
    }

    [RelayCommand]
    private void SwitchToChinese()
    {
        Localizer.Language = "zh";
    }

    [RelayCommand]
    private void SwitchToLightTheme()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = ThemeVariant.Light;
        RefreshThemeMenuCheckState();
    }

    [RelayCommand]
    private void SwitchToDarkTheme()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = ThemeVariant.Dark;
        RefreshThemeMenuCheckState();
    }

    [RelayCommand]
    private void SwitchToSystemTheme()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = ThemeVariant.Default;
        RefreshThemeMenuCheckState();
    }
}
