using JeekTools;

namespace JeekWindowsOptimizer;

public class NoShortcutTextWhenCreatingShortcut : OptimizationItem
{
    public NoShortcutTextWhenCreatingShortcut()
    {
        HasOptimized = Disabled;

        IsInitializing = false;
    }

    public override void HasOptimizedChanged(bool value)
    {
        Disabled = value;
    }

    public override string Name => "创建快捷方式时不添加“快捷方式”文字";

    public override string Description => """
                                          快捷方式的名称已经有箭头标识，不需要额外的“快捷方式”文字，每次都要手工修改一下很麻烦，建议关闭。
                                          立即生效。
                                          """;

    private static readonly RegistryValue LinkValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer",
        "Link");

    private static readonly byte[] DisableValue = [0, 0, 0, 0];

    public bool Disabled
    {
        get => LinkValue.GetBinaryValue(null) == DisableValue;
        set
        {
            if (value)
                LinkValue.SetBinaryValue(DisableValue);
            else
                LinkValue.DeleteValue();
        }
    }
}
