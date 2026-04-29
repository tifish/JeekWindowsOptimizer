using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

public class ToolGroup(string nameKey, ToolItem[] items) : ObservableObject
{
    public string NameKey => nameKey;
    public string Name => Localizer.Get(NameKey);

    public ObservableCollection<ToolItem> Items { get; } = [.. items];

    public void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(Name));
    }
}
