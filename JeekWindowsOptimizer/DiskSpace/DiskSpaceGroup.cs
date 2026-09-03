using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

public partial class DiskSpaceGroup : ObservableObject
{
    public DiskSpaceGroup(string nameKey, IEnumerable<DiskSpaceItem> items)
    {
        NameKey = nameKey;
        Items = [.. items];
        IsExpanded = true;
    }

    public string NameKey { get; }
    public string Name => DiskSpaceItemManager.FormatWithSystemDrive(Localizer.Get(NameKey));

    public ObservableCollection<DiskSpaceItem> Items { get; }

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
