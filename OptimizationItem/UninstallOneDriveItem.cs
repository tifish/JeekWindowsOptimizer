using System.Diagnostics;

namespace JeekWindowsOptimizer;

public class UninstallOneDriveItem : OptimizationItem
{
    public override string GroupName => "卸载";
    public override string Name => "卸载 OneDrive";

    public override string Description => """
                                          OneDrive 是微软的云盘服务，占用资源，不使用可以卸载。
                                          立即生效。
                                          """;

    public UninstallOneDriveItem()
    {
        HasOptimized = !File.Exists(@"C:\Windows\SysWOW64\OneDriveSetup.exe");

        IsInitializing = false;
    }

    public override async void HasOptimizedChanged(bool value)
    {
        if (!value)
            return;

        using var proc = Process.Start(new ProcessStartInfo(@"C:\Windows\SysWOW64\OneDriveSetup.exe", "/uninstall")
        {
            UseShellExecute = true,
        });

        if (proc is null)
            return;

        await proc.WaitForExitAsync();
    }
}
