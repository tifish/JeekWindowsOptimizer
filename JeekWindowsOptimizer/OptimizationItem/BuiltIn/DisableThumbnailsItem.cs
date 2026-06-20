namespace JeekWindowsOptimizer;

public class DisableThumbnailsItem : OptimizationItem
{
    public DisableThumbnailsItem()
    {
        Category = OptimizationItemCategory.Personal;
    }

    public override async Task Initialize()
    {
        IsOptimized = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () => Disabled
        );
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () =>
            {
                Disabled = value;
                return true;
            }
        );
    }

    public override string GroupNameKey => "Explorer";

    public override string NameKey => "DisableThumbnailsName";

    public override string DescriptionKey => "DisableThumbnailsDescription";

    public bool Disabled
    {
        get =>
            WindowsVisualEffects.CustomSetting
            && !WindowsVisualEffects.ShowThumbnailsInsteadOfIcons;
        set
        {
            WindowsVisualEffects.CustomSetting = true;
            WindowsVisualEffects.ShowThumbnailsInsteadOfIcons = !value;
        }
    }
}
