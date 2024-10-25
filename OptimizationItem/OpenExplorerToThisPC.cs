using JeekTools;

namespace JeekWindowsOptimizer;

public class OpenExplorerToThisPC : OptimizationItem
{
    public OpenExplorerToThisPC()
    {
        HasOptimized = Enabled;

        IsInitializing = false;
    }

    public override void HasOptimizedChanged(bool value)
    {
        Enabled = value;
    }

    public override string Name => "打开资源管理器时显示我的电脑";

    public override string Description => """
                                          打开资源管理器通常就是要找文件，直接显示我的电脑更快捷。
                                          立即生效。
                                          """;

    private static readonly RegistryValue LaunchToValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
        "LaunchTo");

    public bool Enabled
    {
        get => LaunchToValue.GetValue(0) == 1;
        set
        {
            if (value)
                LaunchToValue.SetValue(1);
            else
                LaunchToValue.DeleteValue();
        }

    }
}
