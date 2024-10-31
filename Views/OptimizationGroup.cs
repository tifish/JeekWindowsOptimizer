using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JeekWindowsOptimizer;

public partial class OptimizationGroup(string name, OptimizationItem[] items) : ObservableObject
{
    [ObservableProperty]
    private string _name = name;

    [ObservableProperty]
    private ObservableCollection<OptimizationItem> _items = new(items);
}
