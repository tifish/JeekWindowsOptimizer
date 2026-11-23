using CommunityToolkit.Mvvm.ComponentModel;
using Jeek.Avalonia.Localization;
using JeekWindowsOptimizer.Startup;

namespace JeekWindowsOptimizer;

/// <summary>
/// Left-rail navigation entry for a concrete optimization/tool/disk-space group.
/// </summary>
public partial class GroupNavItem : ObservableObject
{
    private GroupNavItem() { }

    public string NameKey { get; private init; } = "";

    public OptimizationGroup? OptimizationGroup { get; private init; }

    public ToolGroup? ToolGroup { get; private init; }

    public DiskSpaceGroup? DiskSpaceGroup { get; private init; }

    public StartupGroup? StartupGroup { get; private init; }

    public string DisplayName =>
        OptimizationGroup?.Name
        ?? ToolGroup?.Name
        ?? DiskSpaceGroup?.Name
        ?? StartupGroup?.Name
        ?? NameKey;

    public int ItemCount =>
        OptimizationGroup?.Items.Count
        ?? ToolGroup?.Items.Count
        ?? DiskSpaceGroup?.Items.Count
        ?? StartupGroup?.Items.Count
        ?? 0;

    public string DisplayCountText => ItemCount.ToString();

    public int PendingCount => StartupGroup?.PendingCount ?? 0;

    public bool HasPending => PendingCount > 0;

    public bool ShowNeutralCount => !HasPending;

    public string PendingBadgeText =>
        PendingCount < ItemCount ? $"{PendingCount}/{ItemCount}" : PendingCount.ToString();

    public string DisplayText
    {
        get
        {
            // The Startup group renders its own text: it also carries the count of items still
            // waiting for a decision, which is the number the user is actually navigating by.
            if (StartupGroup is { } startupGroup)
                return startupGroup.NavDisplayName;

            return $"{DisplayName} ({ItemCount})";
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

    public static GroupNavItem FromDiskSpaceGroup(DiskSpaceGroup group) =>
        new()
        {
            NameKey = group.NameKey,
            DiskSpaceGroup = group,
        };

    public static GroupNavItem FromStartupGroup(StartupGroup group) =>
        new()
        {
            NameKey = group.NameKey,
            StartupGroup = group,
        };

    public void NotifyDisplayChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(DisplayCountText));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(ShowNeutralCount));
        OnPropertyChanged(nameof(PendingBadgeText));
        OnPropertyChanged(nameof(DisplayText));
    }
}
