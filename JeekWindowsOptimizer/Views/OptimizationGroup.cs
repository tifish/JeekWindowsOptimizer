using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

public class OptimizationGroup(string nameKey, OptimizationItem[] items) : INotifyPropertyChanged
{
    public string NameKey => nameKey;
    public string Name => Localizer.Get(NameKey);

    public ObservableCollection<OptimizationItem> Items { get; } = [.. items];

    public void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(Name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
