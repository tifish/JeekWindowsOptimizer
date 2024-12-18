namespace JeekWindowsOptimizer;

public class VisualEffectsItem : OptimizationItem
{
    public VisualEffectsItem()
    {
        IsOptimized = Disabled;
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        Disabled = value;
        return Task.FromResult(true);
    }

    public override string GroupNameKey => "Display";

    public override string NameKey => "DisableVisualEffectsName";

    public override string DescriptionKey => "DisableVisualEffectsDescription";

    public bool Disabled
    {
        get => WindowsVisualEffects.CustomSetting &&
               !WindowsVisualEffects.ClientAreaAnimation &&
               !WindowsVisualEffects.WindowAnimation &&
               !WindowsVisualEffects.TaskBarAnimation &&
               !WindowsVisualEffects.AeroPeek &&
               !WindowsVisualEffects.FadeOrSlideMenusIntoView &&
               !WindowsVisualEffects.FadeOrSlideToolTipsIntoView &&
               !WindowsVisualEffects.FadeOutMenuItemsAfterClicking &&
               !WindowsVisualEffects.SaveTaskbarThumbnail &&
               WindowsVisualEffects.ShowShadowsUnderMousePointer &&
               WindowsVisualEffects.ShowShadowsUnderWindows &&
               WindowsVisualEffects.ShowTranslucentSelectionRectangle &&
               WindowsVisualEffects.ShowWindowContentWhileDragging &&
               !WindowsVisualEffects.SlideOpenComboBoxes &&
               WindowsVisualEffects.SmoothingFonts &&
               !WindowsVisualEffects.SmoothScrollListBoxes &&
               WindowsVisualEffects.UseDropShadowForIconLabels;
        set
        {
            WindowsVisualEffects.CustomSetting = true;
            WindowsVisualEffects.ClientAreaAnimation = !value;
            WindowsVisualEffects.WindowAnimation = !value;
            WindowsVisualEffects.TaskBarAnimation = !value;
            WindowsVisualEffects.AeroPeek = !value;
            WindowsVisualEffects.FadeOrSlideMenusIntoView = !value;
            WindowsVisualEffects.FadeOrSlideToolTipsIntoView = !value;
            WindowsVisualEffects.FadeOutMenuItemsAfterClicking = !value;
            WindowsVisualEffects.SaveTaskbarThumbnail = !value;
            WindowsVisualEffects.ShowShadowsUnderMousePointer = true;
            WindowsVisualEffects.ShowShadowsUnderWindows = true;
            WindowsVisualEffects.ShowTranslucentSelectionRectangle = true;
            WindowsVisualEffects.ShowWindowContentWhileDragging = true;
            WindowsVisualEffects.SlideOpenComboBoxes = !value;
            WindowsVisualEffects.SmoothingFonts = true;
            WindowsVisualEffects.SmoothScrollListBoxes = !value;
            WindowsVisualEffects.UseDropShadowForIconLabels = true;
        }
    }
}
