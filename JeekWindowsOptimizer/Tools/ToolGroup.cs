using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

public partial class ToolGroup(string nameKey, ToolItem[] items) : ObservableObject
{
    public string NameKey => nameKey;
    public string Name => Localizer.Get(NameKey);

    public ObservableCollection<ToolItem> Items { get; } = [.. items];

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
