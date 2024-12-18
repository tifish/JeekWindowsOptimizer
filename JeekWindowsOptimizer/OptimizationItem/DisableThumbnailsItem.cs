namespace JeekWindowsOptimizer;

public class DisableThumbnailsItem : OptimizationItem
{
    public DisableThumbnailsItem()
    {
        IsOptimized = Disabled;
        IsPersonal = true;
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
