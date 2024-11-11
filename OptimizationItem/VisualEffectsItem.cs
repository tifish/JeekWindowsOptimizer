using System.Runtime.InteropServices;
using JeekTools;

namespace JeekWindowsOptimizer;

public class VisualEffectsItem : OptimizationItem
{
    public VisualEffectsItem()
    {
        HasOptimized = Disabled;

        IsInitializing = false;
    }

    public override Task<bool> OnHasOptimizedChanging(bool value)
    {
        Disabled = value;
        return Task.FromResult(true);
    }

    public override string GroupName => "显示";

    public override string Name => "禁用部分视觉效果";

    public override string Description => """
                                          Windows 开启了一些视觉效果和动画，会消耗一些性能，并拖慢操作速度，建议禁用。
                                          部分需要重新登录生效。
                                          """;

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
               !WindowsVisualEffects.ShowThumbnailsInsteadOfIcons &&
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
            WindowsVisualEffects.ShowThumbnailsInsteadOfIcons = !value;
            WindowsVisualEffects.ShowTranslucentSelectionRectangle = true;
            WindowsVisualEffects.ShowWindowContentWhileDragging = true;
            WindowsVisualEffects.SlideOpenComboBoxes = !value;
            WindowsVisualEffects.SmoothingFonts = true;
            WindowsVisualEffects.SmoothScrollListBoxes = !value;
            WindowsVisualEffects.UseDropShadowForIconLabels = true;
        }
    }
}

file static class WindowsVisualEffects
{
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    private const uint SPI_SETCLIENTAREAANIMATION = 0x1043;
    private const uint SPIF_UPDATEINIFILE = 0x01;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out int pvParam, uint fWinIni);

    public static bool ClientAreaAnimation
    {
        get
        {
            var result = SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out var isEnabled, 0);
            return result && isEnabled != 0;
        }
        set => SystemParametersInfo(SPI_SETCLIENTAREAANIMATION, 0, new IntPtr(value ? 1 : 0), SPIF_UPDATEINIFILE);
    }

    private const uint SPI_GETANIMATION = 0x0048;
    private const uint SPI_SETANIMATION = 0x0049;
    private const uint SPIF_SENDCHANGE = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct ANIMATIONINFO
    {
        public uint cbSize;
        public int iMinAnimate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref ANIMATIONINFO pvParam, uint fWinIni);

    private static readonly RegistryValue MinAnimateValue = new(
        @"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics",
        "MinAnimate");

    public static bool WindowAnimation
    {
        get => MinAnimateValue.GetValue("1") == "1";
        set
        {
            MinAnimateValue.SetValue(value ? "1" : "0");

            var ai = new ANIMATIONINFO
            {
                cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(),
                iMinAnimate = value ? 1 : 0,
            };
            SystemParametersInfo(SPI_SETANIMATION, 0, ref ai, SPIF_SENDCHANGE);
        }
    }

    private static readonly RegistryValue VisualFXSettingValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects",
        "VisualFXSetting");

    public static bool CustomSetting
    {
        get => VisualFXSettingValue.GetValue(0) == 3;
        set => VisualFXSettingValue.SetValue(value ? 3 : 0);
    }

    private static readonly RegistryValue TaskbarAnimationValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
        "TaskbarAnimations");

    public static bool TaskBarAnimation
    {
        get => TaskbarAnimationValue.GetValue(1) == 1;
        set => TaskbarAnimationValue.SetValue(value ? 1 : 0);
    }

    private static readonly RegistryValue AeroPeekValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM",
        "EnableAeroPeek");

    public static bool AeroPeek
    {
        get => AeroPeekValue.GetValue(1) == 1;
        set => AeroPeekValue.SetValue(value ? 1 : 0);
    }

    private static readonly RegistryValue SaveTaskbarThumbnailValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM",
        "AlwaysHibernateThumbnails");

    public static bool SaveTaskbarThumbnail
    {
        get => SaveTaskbarThumbnailValue.GetValue(1) == 1;
        set => SaveTaskbarThumbnailValue.SetValue(value ? 1 : 0);
    }

    private static readonly RegistryValue IconOnlyValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
        "IconsOnly");

    public static bool ShowThumbnailsInsteadOfIcons
    {
        get => IconOnlyValue.GetValue(0) == 0;
        set => IconOnlyValue.SetValue(value ? 0 : 1);
    }

    private static readonly RegistryValue ListviewAlphaSelectValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
        "ListviewAlphaSelect");

    public static bool ShowTranslucentSelectionRectangle
    {
        get => ListviewAlphaSelectValue.GetValue(1) == 1;
        set => ListviewAlphaSelectValue.SetValue(value ? 1 : 0);
    }

    private static readonly RegistryValue DragFullWindowsValue = new(
        @"HKEY_CURRENT_USER\Control Panel\Desktop",
        "DragFullWindows");

    public static bool ShowWindowContentWhileDragging
    {
        get => DragFullWindowsValue.GetValue("1") == "1";
        set => DragFullWindowsValue.SetValue(value ? "1" : "0");
    }

    private static readonly RegistryValue FontSmoothingValue = new(
        @"HKEY_CURRENT_USER\Control Panel\Desktop",
        "FontSmoothing");

    public static bool SmoothingFonts
    {
        get => FontSmoothingValue.GetValue("2") == "2";
        set => FontSmoothingValue.SetValue(value ? "2" : "0");
    }

    private static readonly RegistryValue ListviewShadowValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
        "ListviewShadow");

    public static bool UseDropShadowForIconLabels
    {
        get => ListviewShadowValue.GetValue(1) == 1;
        set => ListviewShadowValue.SetValue(value ? 1 : 0);
    }

    private static readonly RegistryValue UserPreferencesMaskValue = new(
        @"HKEY_CURRENT_USER\Control Panel\Desktop",
        "UserPreferencesMask");

    public static bool FadeOrSlideMenusIntoView
    {
        get => UserPreferencesMaskValue.GetBitFromBinary(1, true);
        set => UserPreferencesMaskValue.SetBitToBinary(1, value);
    }

    public static bool FadeOrSlideToolTipsIntoView
    {
        get => UserPreferencesMaskValue.GetBitFromBinary(11, true);
        set => UserPreferencesMaskValue.SetBitToBinary(11, value);
    }

    public static bool FadeOutMenuItemsAfterClicking
    {
        get => UserPreferencesMaskValue.GetBitFromBinary(10, true);
        set => UserPreferencesMaskValue.SetBitToBinary(10, value);
    }

    public static bool ShowShadowsUnderMousePointer
    {
        get => UserPreferencesMaskValue.GetBitFromBinary(13, true);
        set => UserPreferencesMaskValue.SetBitToBinary(13, value);
    }

    public static bool ShowShadowsUnderWindows
    {
        get => UserPreferencesMaskValue.GetBitFromBinary(18, true);
        set => UserPreferencesMaskValue.SetBitToBinary(18, value);
    }

    public static bool SlideOpenComboBoxes
    {
        get => UserPreferencesMaskValue.GetBitFromBinary(2, true);
        set => UserPreferencesMaskValue.SetBitToBinary(2, value);
    }

    public static bool SmoothScrollListBoxes
    {
        get => UserPreferencesMaskValue.GetBitFromBinary(3, true);
        set => UserPreferencesMaskValue.SetBitToBinary(3, value);
    }
}
