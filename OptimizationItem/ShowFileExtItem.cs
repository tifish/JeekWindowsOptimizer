using JeekTools;

namespace JeekWindowsOptimizer;

public class ShowFileExtItem : OptimizationItem
{
    public ShowFileExtItem()
    {
        HasOptimized = Enabled;

        IsInitializing = false;
    }

    public override void HasOptimizedChanged(bool value)
    {
        Enabled = value;
    }

    public override string Name => "显示文件扩展名";

    public override string Description => """
                                          Windows 下文件扩展名决定了文件类型，建议打开以便更好地识别文件。
                                          立即生效。
                                          """;

    private static readonly RegistryValue HideExtValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
        "HideFileExt");

    public bool Enabled
    {
        get => HideExtValue.GetValue(1) == 0;
        set => HideExtValue.SetValue(value ? 0 : 1);
    }
}
