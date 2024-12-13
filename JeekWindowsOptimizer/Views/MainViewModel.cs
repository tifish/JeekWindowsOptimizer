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

    private bool _showPersonal;

    partial void OnSelectedTabIndexChanged(int value)
    {
        _showPersonal = value == 1;
        Groups.Replace(_showPersonal ? PersonalGroups : OptimizingGroups);
    }

    public FastObservableCollection<OptimizationGroup> Groups { get; } = [];
    public List<OptimizationGroup> OptimizingGroups { get; } = [];
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
        };
    }

    private async Task InitializeItems()
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

        AddOptimizationItem(new VisualEffectsItem());
        AddOptimizationItem(new UseClassicalContextMenuItem());
        AddOptimizationItem(new UninstallOneDriveItem());
        AddOptimizationItem(new WindowsActivatorItem());
        AddOptimizationItem(new WindowsUpdateItem());
        if (!Battery.HasBattery())
            AddOptimizationItem(new BestPerformancePowerModeItem());

        await ServiceItemManager.Load();
        foreach (var item in ServiceItemManager.Items)
            AddOptimizationItem(item);

        await MicrosoftStore.Initialize();
        await MicrosoftStoreItemManager.Load();
        foreach (var item in MicrosoftStoreItemManager.Items)
            AddOptimizationItem(item);

        IsBusy = false;
        StatusMessage = Localizer.Get("InitializationFinished");
    }

    private void AddOptimizationItem(OptimizationItem item)
    {
        var groups = item.IsPersonal
            ? PersonalGroups
            : OptimizingGroups;

        foreach (var group in groups)
            if (group.NameKey == item.GroupNameKey)
            {
                group.Items.Add(item);
                return;
            }

        var newGroup = new OptimizationGroup(item.GroupNameKey, [item]);
        groups.Add(newGroup);
        if (!(item.IsPersonal ^ _showPersonal))
            Groups.Add(newGroup);
    }

    public async Task OptimizeCheckedItems()
    {
        OptimizationItem.InBatching = true;
        IsBusy = true;

        var groups = _showPersonal ? PersonalGroups : OptimizingGroups;

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
