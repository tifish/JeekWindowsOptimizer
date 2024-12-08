using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using JeekTools;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Jeek.Avalonia.Localization;
using ZLogger;

namespace JeekWindowsOptimizer;

public partial class MainViewModel : ObservableObject
{
    private static readonly ILogger Log = LogManager.CreateLogger<MainViewModel>();

    [ObservableProperty]
    public partial ObservableCollection<OptimizationGroup> OptimizationGroups { get; set; } = [];

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "正在初始化...";

    [ObservableProperty]
    public partial bool IsBusy { get; set; } = true;

    [RelayCommand]
    private async Task InitializeItems()
    {
        if (Design.IsDesignMode)
        {
            OptimizationGroups.Add(new OptimizationGroup("测试1", [new TestItem(), new TestItem()]));
            OptimizationGroups.Add(new OptimizationGroup("测试2", [new TestItem(), new TestItem()]));
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
        foreach (var group in OptimizationGroups)
            if (group.Name == item.GroupName)
            {
                group.Items.Add(item);
                return;
            }

        OptimizationGroups.Add(new OptimizationGroup(item.GroupName, [item]));
    }

    public async Task OptimizeCheckedItems()
    {
        OptimizationItem.InBatching = true;
        IsBusy = true;

        try
        {
            StatusMessage = "优化前准备工作...";

            var shouldTurnOffTamperProtection = false;
            var shouldTurnOffOnAccessProtection = false;

            foreach (var group in OptimizationGroups)
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

            foreach (var group in OptimizationGroups)
                foreach (var item in group.Items)
                {
                    if (!item.IsChecked || item.IsOptimized)
                        continue;

                    StatusMessage = $"正在优化：{item.Name}...";
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

            StatusMessage = "优化后处理...";

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
            StatusMessage = "优化已完成！";
        }
    }
}
