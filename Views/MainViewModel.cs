using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using JeekTools;
using Microsoft.Extensions.Logging;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;

namespace JeekWindowsOptimizer;

public partial class MainViewModel : ObservableObject
{
    private static readonly ILogger Log = LogManager.CreateLogger<MainViewModel>();

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private ObservableCollection<OptimizationGroup> _optimizationGroups =
    [
        new("内核",
        [
            new VisualEffectsItem(),
        ]),
    ];

    [ObservableProperty]
    private bool _isOptimizing;

    public MainViewModel()
    {
        RegistryItemManager.Load();
        foreach (var item in RegistryItemManager.Items)
        {
            var group = _optimizationGroups.FirstOrDefault(group => group.Name == item.GroupName);
            if (group == null)
            {
                group = new OptimizationGroup(item.GroupName, [item]);
                _optimizationGroups.Add(group);
            }

            group.Items.Add(item);
        }
    }

    public async Task OptimizeCheckedItems()
    {
        OptimizationItem.InBatching = true;
        IsOptimizing = true;

        try
        {
            var shouldUpdateGroupPolicy = false;
            var shouldReboot = false;

            foreach(var group in OptimizationGroups)
                foreach (var item in group.Items)
                {
                    if (!item.IsChecked || item.HasOptimized)
                        continue;

                    item.HasOptimized = true;

                    shouldUpdateGroupPolicy |= item.ShouldUpdateGroupPolicy;
                    shouldReboot |= item.ShouldReboot;
                }

            if (shouldUpdateGroupPolicy)
                await OptimizationItem.UpdateGroupPolicy();

            if (shouldReboot)
                await MessageBoxManager.GetMessageBoxStandard(new MessageBoxStandardParams
                {
                    ContentMessage = "需要重启生效。",
                    ButtonDefinitions = ButtonEnum.Ok,
                    Icon = Icon.Info,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Topmost = true,
                    FontFamily = "Microsoft YaHei",
                }).ShowAsync();
        }
        finally
        {
            OptimizationItem.InBatching = false;
            IsOptimizing = false;
        }
    }
}
