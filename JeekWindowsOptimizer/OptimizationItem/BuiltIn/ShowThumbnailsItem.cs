namespace JeekWindowsOptimizer;

public class ShowThumbnailsItem : OptimizationItem
{
    public ShowThumbnailsItem()
    {
        Category = OptimizationItemCategory.Personal;
    }

    public override async Task Initialize()
    {
        IsOptimized = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () => Enabled
        );
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () =>
            {
                Enabled = value;
                return true;
            }
        );
    }

    public override string GroupNameKey => "Explorer";

    public override string NameKey => "ShowThumbnailsName";

    public override string DescriptionKey => "ShowThumbnailsDescription";

    public bool Enabled
    {
        get => WindowsVisualEffects.ShowThumbnailsInsteadOfIcons;
        set
        {
            WindowsVisualEffects.CustomSetting = true;
            WindowsVisualEffects.ShowThumbnailsInsteadOfIcons = value;
        }
    }
}
