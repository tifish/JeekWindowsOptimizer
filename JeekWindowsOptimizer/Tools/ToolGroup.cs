using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

public partial class ToolGroup : ObservableObject
{
    public ToolGroup(string nameKey, ToolItem[] items)
    {
        NameKey = nameKey;
        Items = [.. items];
        IsExpanded = true;
    }

    public string NameKey { get; }
    public string Name => Localizer.Get(NameKey);

    public ObservableCollection<ToolItem> Items { get; }

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
