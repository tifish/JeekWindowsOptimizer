using Avalonia.Controls;
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

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    private OptimizationItemCategory _selectedCategory;

    partial void OnSelectedTabIndexChanged(int value)
    {
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

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = Localizer.Get("Initializing");

    [ObservableProperty]
    public partial bool IsBusy { get; set; } = true;

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
                return;
            }

            await RegistryItemManager.Load();
            foreach (var item in RegistryItemManager.Items)
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
            if (group.NameKey == item.GroupNameKey)
            {
                group.Items.Add(item);
                isNewGroup = false;
                break;
            }

        if (isNewGroup)
        {
            var newGroup = new OptimizationGroup(item.GroupNameKey, [item]);
            groups.Add(newGroup);
            if (!(item.Category == _selectedCategory))
                Groups.Add(newGroup);
        }

        UpdateItemStat(item.Category);
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
            PersonalTabHeader = $"{Localizer.Get("Personal")} ({optimizedItemCount}/{totalItemsCount})";
        else if (category == OptimizationItemCategory.Antivirus)
            AntivirusTabHeader = $"{Localizer.Get("Antivirus")} ({optimizedItemCount}/{totalItemsCount})";
        else
            OptimizingTabHeader = $"{Localizer.Get("Optimizing")} ({optimizedItemCount}/{totalItemsCount})";
    }

    [ObservableProperty]
    public partial string OptimizingTabHeader { get; set; } = Localizer.Get("Optimizing");

    [ObservableProperty]
    public partial string AntivirusTabHeader { get; set; } = Localizer.Get("Antivirus");

    [ObservableProperty]
    public partial string PersonalTabHeader { get; set; } = Localizer.Get("Personal");

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

    public void SwitchToEnglish()
    {
        Localizer.Language = "en";
    }

    public void SwitchToChinese()
    {
        Localizer.Language = "zh";
    }
}
