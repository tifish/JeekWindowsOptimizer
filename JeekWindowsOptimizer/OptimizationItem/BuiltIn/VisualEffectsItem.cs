using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

public sealed class VisualEffectItem : OptimizationItem
{
    private static readonly ILogger Log = LogManager.CreateLogger<VisualEffectItem>();

    private const string DisableDescriptionKey = "DisableVisualEffectDescription";
    private const string EnableDescriptionKey = "EnableVisualEffectDescription";

    private readonly string _nameKey;
    private readonly string _descriptionKey;
    private readonly Func<bool> _getEnabled;
    private readonly Action<bool> _setEnabled;
    private readonly bool _optimizedValue;

    private VisualEffectItem(
        string nameKey,
        string descriptionKey,
        Func<bool> getEnabled,
        Action<bool> setEnabled,
        bool optimizedValue
    )
    {
        _nameKey = nameKey;
        _descriptionKey = descriptionKey;
        _getEnabled = getEnabled;
        _setEnabled = setEnabled;
        _optimizedValue = optimizedValue;
    }

    public static IEnumerable<OptimizationItem> CreateItems()
    {
        yield return CreateDisabled(
            "DisableClientAreaAnimationName",
            () => WindowsVisualEffects.ClientAreaAnimation,
            value => WindowsVisualEffects.ClientAreaAnimation = value
        );
        yield return CreateDisabled(
            "DisableWindowAnimationName",
            () => WindowsVisualEffects.WindowAnimation,
            value => WindowsVisualEffects.WindowAnimation = value
        );
        yield return CreateDisabled(
            "DisableTaskbarAnimationName",
            () => WindowsVisualEffects.TaskBarAnimation,
            value => WindowsVisualEffects.TaskBarAnimation = value
        );
        yield return CreateDisabled(
            "DisableAeroPeekName",
            () => WindowsVisualEffects.AeroPeek,
            value => WindowsVisualEffects.AeroPeek = value
        );
        yield return CreateDisabled(
            "DisableMenuAnimationName",
            () => WindowsVisualEffects.FadeOrSlideMenusIntoView,
            value => WindowsVisualEffects.FadeOrSlideMenusIntoView = value
        );
        yield return CreateDisabled(
            "DisableTooltipAnimationName",
            () => WindowsVisualEffects.FadeOrSlideToolTipsIntoView,
            value => WindowsVisualEffects.FadeOrSlideToolTipsIntoView = value
        );
        yield return CreateDisabled(
            "DisableMenuFadeOutName",
            () => WindowsVisualEffects.FadeOutMenuItemsAfterClicking,
            value => WindowsVisualEffects.FadeOutMenuItemsAfterClicking = value
        );
        yield return CreateDisabled(
            "DisableTaskbarThumbnailCacheName",
            () => WindowsVisualEffects.SaveTaskbarThumbnail,
            value => WindowsVisualEffects.SaveTaskbarThumbnail = value
        );
        yield return CreateEnabled(
            "EnableMousePointerShadowName",
            () => WindowsVisualEffects.ShowShadowsUnderMousePointer,
            value => WindowsVisualEffects.ShowShadowsUnderMousePointer = value
        );
        yield return CreateEnabled(
            "EnableWindowShadowName",
            () => WindowsVisualEffects.ShowShadowsUnderWindows,
            value => WindowsVisualEffects.ShowShadowsUnderWindows = value
        );
        yield return CreateEnabled(
            "EnableTranslucentSelectionRectangleName",
            () => WindowsVisualEffects.ShowTranslucentSelectionRectangle,
            value => WindowsVisualEffects.ShowTranslucentSelectionRectangle = value
        );
        yield return CreateEnabled(
            "EnableWindowContentWhileDraggingName",
            () => WindowsVisualEffects.ShowWindowContentWhileDragging,
            value => WindowsVisualEffects.ShowWindowContentWhileDragging = value
        );
        yield return CreateDisabled(
            "DisableComboBoxAnimationName",
            () => WindowsVisualEffects.SlideOpenComboBoxes,
            value => WindowsVisualEffects.SlideOpenComboBoxes = value
        );
        yield return CreateEnabled(
            "EnableFontSmoothingName",
            () => WindowsVisualEffects.SmoothingFonts,
            value => WindowsVisualEffects.SmoothingFonts = value
        );
        yield return CreateDisabled(
            "DisableListBoxSmoothScrollingName",
            () => WindowsVisualEffects.SmoothScrollListBoxes,
            value => WindowsVisualEffects.SmoothScrollListBoxes = value
        );
        yield return CreateEnabled(
            "EnableDesktopIconLabelShadowName",
            () => WindowsVisualEffects.UseDropShadowForIconLabels,
            value => WindowsVisualEffects.UseDropShadowForIconLabels = value
        );
    }

    private static VisualEffectItem CreateDisabled(
        string nameKey,
        Func<bool> getEnabled,
        Action<bool> setEnabled
    ) => new(nameKey, DisableDescriptionKey, getEnabled, setEnabled, false);

    private static VisualEffectItem CreateEnabled(
        string nameKey,
        Func<bool> getEnabled,
        Action<bool> setEnabled
    ) => new(nameKey, EnableDescriptionKey, getEnabled, setEnabled, true);

    public override Task Initialize()
    {
        IsOptimized = TargetStateApplied;
        return Task.CompletedTask;
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        try
        {
            TargetStateApplied = value;
            var isApplied = TargetStateApplied;
            return Task.FromResult(value ? isApplied : !isApplied);
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Failed to change visual effect '{_nameKey}'");
            return Task.FromResult(false);
        }
    }

    public override string GroupNameKey => "Display";

    public override string NameKey => _nameKey;

    public override string DescriptionKey => _descriptionKey;

    private bool TargetStateApplied
    {
        get => _getEnabled() == _optimizedValue;
        set
        {
            WindowsVisualEffects.CustomSetting = true;
            _setEnabled(value ? _optimizedValue : !_optimizedValue);
        }
    }
}
