using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace JeekWindowsOptimizer;

public partial class OptimizationGroup(string name, OptimizationItem[] items) : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = name;

    [ObservableProperty]
    public partial ObservableCollection<OptimizationItem> Items { get; set; } = [.. items];
}
