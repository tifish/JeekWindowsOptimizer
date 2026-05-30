using System.Runtime.InteropServices;
using DotNetRun;

namespace JeekWindowsOptimizer;

public static class WindowsVisualEffects
{
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;
    private const uint UpdateAndBroadcast = SPIF_UPDATEINIFILE | SPIF_SENDCHANGE;

    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    private const uint SPI_SETCLIENTAREAANIMATION = 0x1043;
    private const uint SPI_GETANIMATION = 0x0048;
    private const uint SPI_SETANIMATION = 0x0049;
    private const uint SPI_GETDRAGFULLWINDOWS = 0x0026;
    private const uint SPI_SETDRAGFULLWINDOWS = 0x0025;
    private const uint SPI_GETDROPSHADOW = 0x1024;
    private const uint SPI_SETDROPSHADOW = 0x1025;
    private const uint SPI_GETFONTSMOOTHING = 0x004A;
    private const uint SPI_SETFONTSMOOTHING = 0x004B;
    private const uint SPI_GETCOMBOBOXANIMATION = 0x1004;
    private const uint SPI_SETCOMBOBOXANIMATION = 0x1005;
    private const uint SPI_GETLISTBOXSMOOTHSCROLLING = 0x1006;
    private const uint SPI_SETLISTBOXSMOOTHSCROLLING = 0x1007;
    private const uint SPI_GETMENUANIMATION = 0x1002;
    private const uint SPI_SETMENUANIMATION = 0x1003;
    private const uint SPI_GETSELECTIONFADE = 0x1014;
    private const uint SPI_SETSELECTIONFADE = 0x1015;
    private const uint SPI_GETTOOLTIPANIMATION = 0x1016;
    private const uint SPI_SETTOOLTIPANIMATION = 0x1017;
    private const uint SPI_GETCURSORSHADOW = 0x101A;
    private const uint SPI_SETCURSORSHADOW = 0x101B;

    [StructLayout(LayoutKind.Sequential)]
    private struct ANIMATIONINFO
    {
        public uint cbSize;
        public int iMinAnimate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        IntPtr pvParam,
        uint fWinIni
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        out int pvParam,
        uint fWinIni
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        ref ANIMATIONINFO pvParam,
        uint fWinIni
    );

    private static readonly RegistryValue VisualFXSettingValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects",
        "VisualFXSetting"
    );

    public static bool CustomSetting
    {
        get => VisualFXSettingValue.GetValue(0) == 3;
        set => VisualFXSettingValue.SetValue(value ? 3 : 0);
    }

    public static bool ClientAreaAnimation
    {
        get => GetBoolParameter(SPI_GETCLIENTAREAANIMATION, nameof(ClientAreaAnimation));
        set => SetBoolParameterInPvParam(SPI_SETCLIENTAREAANIMATION, value, nameof(ClientAreaAnimation));
    }

    public static bool WindowAnimation
    {
        get
        {
            var ai = CreateAnimationInfo();
            CheckSystemParametersInfo(
                SystemParametersInfo(SPI_GETANIMATION, AnimationInfoSize, ref ai, 0),
                nameof(WindowAnimation)
            );
            return ai.iMinAnimate != 0;
        }
        set
        {
            var ai = CreateAnimationInfo();
            ai.iMinAnimate = value ? 1 : 0;
            CheckSystemParametersInfo(
                SystemParametersInfo(
                    SPI_SETANIMATION,
                    AnimationInfoSize,
                    ref ai,
                    UpdateAndBroadcast
                ),
                nameof(WindowAnimation)
            );
        }
    }

    private static readonly RegistryValue TaskbarAnimationValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
        "TaskbarAnimations"
    );

    public static bool TaskBarAnimation
    {
        get => TaskbarAnimationValue.GetValue(1) == 1;
        set => TaskbarAnimationValue.SetValue(value ? 1 : 0);
    }

    private static readonly RegistryValue AeroPeekValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM",
        "EnableAeroPeek"
    );

    public static bool AeroPeek
    {
        get => AeroPeekValue.GetValue(1) == 1;
        set => AeroPeekValue.SetValue(value ? 1 : 0);
    }

    private static readonly RegistryValue SaveTaskbarThumbnailValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM",
        "AlwaysHibernateThumbnails"
    );

    public static bool SaveTaskbarThumbnail
    {
        get => SaveTaskbarThumbnailValue.GetValue(1) == 1;
        set => SaveTaskbarThumbnailValue.SetValue(value ? 1 : 0);
    }

    private static readonly RegistryValue IconOnlyValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
        "IconsOnly"
    );

    public static bool ShowThumbnailsInsteadOfIcons
    {
        get => IconOnlyValue.GetValue(0) == 0;
        set => IconOnlyValue.SetValue(value ? 0 : 1);
    }

    public static bool FadeOrSlideMenusIntoView
    {
        get => GetBoolParameter(SPI_GETMENUANIMATION, nameof(FadeOrSlideMenusIntoView));
        set => SetBoolParameterInPvParam(SPI_SETMENUANIMATION, value, nameof(FadeOrSlideMenusIntoView));
    }

    public static bool FadeOrSlideToolTipsIntoView
    {
        get => GetBoolParameter(SPI_GETTOOLTIPANIMATION, nameof(FadeOrSlideToolTipsIntoView));
        set => SetBoolParameterInPvParam(
            SPI_SETTOOLTIPANIMATION,
            value,
            nameof(FadeOrSlideToolTipsIntoView)
        );
    }

    public static bool FadeOutMenuItemsAfterClicking
    {
        get => GetBoolParameter(SPI_GETSELECTIONFADE, nameof(FadeOutMenuItemsAfterClicking));
        set => SetBoolParameterInPvParam(
            SPI_SETSELECTIONFADE,
            value,
            nameof(FadeOutMenuItemsAfterClicking)
        );
    }

    public static bool ShowShadowsUnderMousePointer
    {
        get => GetBoolParameter(SPI_GETCURSORSHADOW, nameof(ShowShadowsUnderMousePointer));
        set => SetBoolParameterInPvParam(
            SPI_SETCURSORSHADOW,
            value,
            nameof(ShowShadowsUnderMousePointer)
        );
    }

    public static bool ShowShadowsUnderWindows
    {
        get => GetBoolParameter(SPI_GETDROPSHADOW, nameof(ShowShadowsUnderWindows));
        set => SetBoolParameterInPvParam(SPI_SETDROPSHADOW, value, nameof(ShowShadowsUnderWindows));
    }

    private static readonly RegistryValue ListviewAlphaSelectValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
        "ListviewAlphaSelect"
    );

    public static bool ShowTranslucentSelectionRectangle
    {
        get => ListviewAlphaSelectValue.GetValue(1) == 1;
        set => ListviewAlphaSelectValue.SetValue(value ? 1 : 0);
    }

    public static bool ShowWindowContentWhileDragging
    {
        get => GetBoolParameter(SPI_GETDRAGFULLWINDOWS, nameof(ShowWindowContentWhileDragging));
        set => SetBoolParameterInUiParam(
            SPI_SETDRAGFULLWINDOWS,
            value,
            nameof(ShowWindowContentWhileDragging)
        );
    }

    public static bool SlideOpenComboBoxes
    {
        get => GetBoolParameter(SPI_GETCOMBOBOXANIMATION, nameof(SlideOpenComboBoxes));
        set => SetBoolParameterInPvParam(SPI_SETCOMBOBOXANIMATION, value, nameof(SlideOpenComboBoxes));
    }

    public static bool SmoothingFonts
    {
        get => GetBoolParameter(SPI_GETFONTSMOOTHING, nameof(SmoothingFonts));
        set => SetBoolParameterInUiParam(SPI_SETFONTSMOOTHING, value, nameof(SmoothingFonts));
    }

    public static bool SmoothScrollListBoxes
    {
        get => GetBoolParameter(SPI_GETLISTBOXSMOOTHSCROLLING, nameof(SmoothScrollListBoxes));
        set => SetBoolParameterInPvParam(
            SPI_SETLISTBOXSMOOTHSCROLLING,
            value,
            nameof(SmoothScrollListBoxes)
        );
    }

    private static readonly RegistryValue ListviewShadowValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
        "ListviewShadow"
    );

    public static bool UseDropShadowForIconLabels
    {
        get => ListviewShadowValue.GetValue(1) == 1;
        set => ListviewShadowValue.SetValue(value ? 1 : 0);
    }

    public static void ApplyOptimizedPreset()
    {
        CustomSetting = true;
        ApplyPreset(item => item.OptimizedValue);
    }

    public static void ApplyDefaultPreset()
    {
        CustomSetting = true;
        ApplyPreset(item => item.DefaultValue);
    }

    public static bool IsOptimizedPresetApplied()
    {
        return CustomSetting && PresetItems.All(item => item.GetValue() == item.OptimizedValue);
    }

    private static uint AnimationInfoSize => (uint)Marshal.SizeOf<ANIMATIONINFO>();

    private static ANIMATIONINFO CreateAnimationInfo()
    {
        return new ANIMATIONINFO { cbSize = AnimationInfoSize };
    }

    private static bool GetBoolParameter(uint action, string effectName)
    {
        CheckSystemParametersInfo(SystemParametersInfo(action, 0, out var isEnabled, 0), effectName);
        return isEnabled != 0;
    }

    private static void SetBoolParameterInPvParam(uint action, bool value, string effectName)
    {
        CheckSystemParametersInfo(
            SystemParametersInfo(action, 0, new IntPtr(value ? 1 : 0), UpdateAndBroadcast),
            effectName
        );
    }

    private static void SetBoolParameterInUiParam(uint action, bool value, string effectName)
    {
        CheckSystemParametersInfo(
            SystemParametersInfo(action, value ? 1u : 0u, IntPtr.Zero, UpdateAndBroadcast),
            effectName
        );
    }

    private static void CheckSystemParametersInfo(bool succeeded, string effectName)
    {
        if (succeeded)
            return;

        var errorCode = Marshal.GetLastPInvokeError();
        throw new InvalidOperationException(
            $"Failed to update Windows visual effect '{effectName}'. Win32 error: {errorCode}."
        );
    }

    private static void ApplyPreset(Func<VisualEffectPresetItem, bool> getTargetValue)
    {
        foreach (var item in PresetItems)
        {
            try
            {
                item.SetValue(getTargetValue(item));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to set Windows visual effect '{item.Name}'.",
                    ex
                );
            }
        }
    }

    private readonly record struct VisualEffectPresetItem(
        string Name,
        Func<bool> GetValue,
        Action<bool> SetValue,
        bool OptimizedValue,
        bool DefaultValue
    );

    private static readonly VisualEffectPresetItem[] PresetItems =
    [
        new(
            nameof(ClientAreaAnimation),
            () => ClientAreaAnimation,
            value => ClientAreaAnimation = value,
            false,
            true
        ),
        new(
            nameof(WindowAnimation),
            () => WindowAnimation,
            value => WindowAnimation = value,
            false,
            true
        ),
        new(
            nameof(TaskBarAnimation),
            () => TaskBarAnimation,
            value => TaskBarAnimation = value,
            false,
            true
        ),
        new(nameof(AeroPeek), () => AeroPeek, value => AeroPeek = value, false, true),
        new(
            nameof(FadeOrSlideMenusIntoView),
            () => FadeOrSlideMenusIntoView,
            value => FadeOrSlideMenusIntoView = value,
            false,
            true
        ),
        new(
            nameof(FadeOrSlideToolTipsIntoView),
            () => FadeOrSlideToolTipsIntoView,
            value => FadeOrSlideToolTipsIntoView = value,
            false,
            true
        ),
        new(
            nameof(FadeOutMenuItemsAfterClicking),
            () => FadeOutMenuItemsAfterClicking,
            value => FadeOutMenuItemsAfterClicking = value,
            false,
            true
        ),
        new(
            "SaveTaskbarThumbnailPreviews",
            () => SaveTaskbarThumbnail,
            value => SaveTaskbarThumbnail = value,
            false,
            true
        ),
        new(
            nameof(ShowShadowsUnderMousePointer),
            () => ShowShadowsUnderMousePointer,
            value => ShowShadowsUnderMousePointer = value,
            true,
            true
        ),
        new(
            nameof(ShowShadowsUnderWindows),
            () => ShowShadowsUnderWindows,
            value => ShowShadowsUnderWindows = value,
            true,
            true
        ),
        new(
            nameof(ShowTranslucentSelectionRectangle),
            () => ShowTranslucentSelectionRectangle,
            value => ShowTranslucentSelectionRectangle = value,
            true,
            true
        ),
        new(
            nameof(ShowWindowContentWhileDragging),
            () => ShowWindowContentWhileDragging,
            value => ShowWindowContentWhileDragging = value,
            true,
            true
        ),
        new(
            nameof(SlideOpenComboBoxes),
            () => SlideOpenComboBoxes,
            value => SlideOpenComboBoxes = value,
            false,
            true
        ),
        new(
            nameof(SmoothingFonts),
            () => SmoothingFonts,
            value => SmoothingFonts = value,
            true,
            true
        ),
        new(
            nameof(SmoothScrollListBoxes),
            () => SmoothScrollListBoxes,
            value => SmoothScrollListBoxes = value,
            false,
            true
        ),
        new(
            nameof(UseDropShadowForIconLabels),
            () => UseDropShadowForIconLabels,
            value => UseDropShadowForIconLabels = value,
            true,
            true
        ),
    ];
}
