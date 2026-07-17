using CommunityToolkit.Mvvm.ComponentModel;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

/// <summary>
/// Left-rail navigation entry for a concrete optimization/tool group.
/// </summary>
public partial class GroupNavItem : ObservableObject
{
    private GroupNavItem() { }

    public string NameKey { get; private init; } = "";

    public OptimizationGroup? OptimizationGroup { get; private init; }

    public ToolGroup? ToolGroup { get; private init; }

    public string DisplayText
    {
        get
        {
            var name = OptimizationGroup?.Name ?? ToolGroup?.Name ?? NameKey;
            var count = OptimizationGroup?.Items.Count ?? ToolGroup?.Items.Count ?? 0;
            return $"{name} ({count})";
        }
    }

    public static GroupNavItem FromOptimizationGroup(OptimizationGroup group) =>
        new()
        {
            NameKey = group.NameKey,
            OptimizationGroup = group,
        };

    public static GroupNavItem FromToolGroup(ToolGroup group) =>
        new()
        {
            NameKey = group.NameKey,
            ToolGroup = group,
        };

    public void NotifyDisplayChanged()
    {
        OnPropertyChanged(nameof(DisplayText));
    }
}
