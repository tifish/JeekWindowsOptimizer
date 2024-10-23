using JeekTools;

namespace JeekWindowsOptimizer;

public class SmartScreenItem : OptimizationItem
{
    public SmartScreenItem()
    {
        ShouldTurnOffTamperProtection = true;

        HasOptimized = Disabled;

        IsInitializing = false;
    }

    public override void HasOptimizedChanged(bool value)
    {
        Disabled = value;
    }

    public override string Name => "禁用 SmartScreen";

    public override string Description => """
                                          SmartScreen 在打开应用和安装包时会检查文件，影响启动速度，还会阻止下载某些文件，建议禁用。
                                          立即生效。
                                          """;

    private static readonly RegistryValue ForFilesValue = new(
        @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Explorer",
        "SmartScreenEnabled");
    private static readonly RegistryValue ForEdgeValue = new(
        @"HKEY_CURRENT_USER\Software\Microsoft\Edge\SmartScreenEnabled",
        null);

    public bool Disabled
    {
        get => ForFilesValue.GetValue("Warn") == "Off" && ForEdgeValue.GetValue(1) == 0;
        set
        {
            if (value)
            {
                ForFilesValue.SetValue("Off");
                ForEdgeValue.SetValue(0);
            }
            else
            {
                ForFilesValue.SetValue("Warn");
                ForEdgeValue.SetValue(1);
            }
        }
    }
}
