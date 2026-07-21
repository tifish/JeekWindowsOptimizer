using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

public partial class OptimizationGroup : ObservableObject
{
    public OptimizationGroup(string nameKey, OptimizationItem[] items)
    {
        NameKey = nameKey;
        Items = [.. items];
        IsExpanded = true;
    }

    public string NameKey { get; }
    public string Name => Localizer.Get(NameKey);

    public ObservableCollection<OptimizationItem> Items { get; }

    /// <summary>
    /// Whether this group's expander is open in the content pane.
    /// </summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public int ItemCount => Items.Count;

    public string NavDisplayName => $"{Name} ({Items.Count})";

    public void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(NavDisplayName));
    }
}
