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

    private readonly string _installerPath1 = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\OneDriveSetup.exe");
    private readonly string _installerPath2 = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\SysWOW64\OneDriveSetup.exe");

    public UninstallOneDriveItem()
    {
        IsOptimized = !File.Exists(Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\OneDrive\OneDrive.exe"));

        IsInitializing = false;
    }

    protected override async Task<bool> IsOptimizedChanging(bool value)
    {
        if (!value)
            return false;

        var installerPath = _installerPath1;
        if (!File.Exists(installerPath))
        {
            installerPath = _installerPath2;
            if (!File.Exists(installerPath))
                return false;
        }

        using var proc = Process.Start(new ProcessStartInfo(installerPath, "/uninstall")
        {
            UseShellExecute = true,
        });

        if (proc is null)
            return false;

        await proc.WaitForExitAsync();
        return true;
    }
}
