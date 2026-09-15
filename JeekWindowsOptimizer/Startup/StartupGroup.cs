using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer.Startup;

/// <summary>One collapsible section on the Startup tab: all items of a single kind.</summary>
public partial class StartupGroup : ObservableObject
{
    public StartupGroup(StartupItemKind kind, IEnumerable<StartupItem> items)
    {
        Kind = kind;
        NameKey = StartupItem.KindNameKeyOf(kind);
        Items = [.. items];
        IsExpanded = true;
    }

    public StartupItemKind Kind { get; }

    public string NameKey { get; }

    public string Name => Localizer.Get(NameKey);

    public ObservableCollection<StartupItem> Items { get; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public int ItemCount => Items.Count;

    public int PendingCount => Items.Count(item => item.IsPending);

    public string NavDisplayName =>
        PendingCount > 0 ? $"{Name} ({Items.Count}, {PendingCount}!)" : $"{Name} ({Items.Count})";

    public void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(NavDisplayName));
        foreach (var item in Items)
            item.NotifyLanguageChanged();
    }

    public void NotifyCountsChanged()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(NavDisplayName));
    }
}
