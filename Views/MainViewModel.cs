using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using JeekTools;
using Microsoft.Extensions.Logging;

namespace JeekWindowsOptimizer;

public partial class MainViewModel : ObservableObject
{
    private static readonly ILogger Log = LogManager.CreateLogger<MainViewModel>();

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private ObservableCollection<OptimizationGroup> _optimizationGroups = [];

    [ObservableProperty]
    private bool _isOptimizing;

    public MainViewModel()
    {
        if (Design.IsDesignMode)
        {
            _optimizationGroups.Add(new OptimizationGroup("测试1", [new TestItem(), new TestItem()]));
            _optimizationGroups.Add(new OptimizationGroup("测试2", [new TestItem(), new TestItem()]));
            return;
        }

        RegistryItemManager.Load();
        foreach (var item in RegistryItemManager.Items)
            AddOptimizationItem(item);

        AddOptimizationItem(new VisualEffectsItem());
        AddOptimizationItem(new UseClassicalContextMenuItem());
        AddOptimizationItem(new UninstallOneDriveItem());

        ServiceItemManager.Load().ContinueWith(_ =>
        {
            foreach (var item in ServiceItemManager.Items)
                AddOptimizationItem(item);
        });

        MicrosoftStoreItemManager.Load().ContinueWith(_ =>
        {
            foreach (var item in MicrosoftStoreItemManager.Items)
                AddOptimizationItem(item);
        });
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
        IsOptimizing = true;

        try
        {
            var shouldTurnOffTamperProtection = false;

            foreach (var group in OptimizationGroups)
                foreach (var item in group.Items)
                {
                    if (!item.IsChecked || item.IsOptimized)
                        continue;

                    shouldTurnOffTamperProtection |= item.ShouldTurnOffTamperProtection;
                }

            if (shouldTurnOffTamperProtection)
                if (!await OptimizationItem.TurnOffTamperProtection())
                    return;

            var shouldUpdateGroupPolicy = false;
            var shouldReboot = false;
            var shouldRestartExplorer = false;

            foreach (var group in OptimizationGroups)
                foreach (var item in group.Items)
                {
                    if (!item.IsChecked || item.IsOptimized)
                        continue;

                    item.IsOptimized = true;

                    shouldUpdateGroupPolicy |= item.ShouldUpdateGroupPolicy;
                    shouldReboot |= item.ShouldReboot;
                    shouldRestartExplorer |= item.ShouldRestartExplorer;
                }

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
            IsOptimizing = false;
        }
    }
}
