namespace JeekWindowsOptimizer;

public class DisableThumbnailsItem : OptimizationItem
{
    public DisableThumbnailsItem()
    {
        Category = OptimizationItemCategory.Personal;
    }

    public override Task Initialize()
    {
        IsOptimized = Disabled;
        return Task.CompletedTask;
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        Disabled = value;
        return Task.FromResult(true);
    }

    public override string GroupNameKey => "Display";

    public override string NameKey => "DisableThumbnailsName";

    public override string DescriptionKey => "DisableThumbnailsDescription";

    public bool Disabled
    {
        get => WindowsVisualEffects.CustomSetting
               && !WindowsVisualEffects.ShowThumbnailsInsteadOfIcons;
        set
        {
            WindowsVisualEffects.CustomSetting = true;
            WindowsVisualEffects.ShowThumbnailsInsteadOfIcons = !value;
        }
    }
}
