namespace JeekWindowsOptimizer;

public class MicrosoftStoreItem : OptimizationItem
{
    public override string GroupName { get; }
    public override string Name { get; }
    public override string Description { get; }
    private string PackageName { get; }

    public MicrosoftStoreItem(string groupName, string name, string description, string packageName)
    {
        GroupName = groupName;
        Name = name;
        Description = description;
        PackageName = packageName;

        HasOptimized = !MicrosoftStore.HasPackage(PackageName);

        IsInitializing = false;
    }

    public override async Task<bool> OnHasOptimizedChanging(bool value)
    {
        if (!value)
            return false;

        await MicrosoftStore.UninstallPackage(PackageName);
        return !MicrosoftStore.HasPackage(PackageName);
    }
}