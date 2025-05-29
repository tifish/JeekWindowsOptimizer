namespace JeekWindowsOptimizer;

public class MicrosoftStoreItem : OptimizationItem
{
    public override string GroupNameKey { get; }
    public override string NameKey { get; }
    public override string DescriptionKey { get; }
    private string PackageName { get; }

    public MicrosoftStoreItem(string groupNameKey, string nameKey, string descriptionKey, OptimizationItemCategory category, string packageName)
    {
        GroupNameKey = groupNameKey;
        NameKey = nameKey;
        DescriptionKey = descriptionKey;
        Category = category;
        PackageName = packageName;
    }

    public override async Task Initialize()
    {
        IsOptimized = !await MicrosoftStore.HasPackage(PackageName);
    }

    protected override async Task<bool> IsOptimizedChanging(bool value)
    {
        if (!value)
            return false;

        await MicrosoftStore.UninstallPackage(PackageName);
        return !await MicrosoftStore.HasPackage(PackageName);
    }
}
