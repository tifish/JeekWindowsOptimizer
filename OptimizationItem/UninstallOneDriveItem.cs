using System.Diagnostics;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

public class UninstallOneDriveItem : OptimizationItem
{
    public override string GroupName => Localizer.Get("UninstallGroup");
    public override string Name => Localizer.Get("UninstallOneDrive");

    public override string Description => Localizer.Get("UninstallOneDriveDescription");

    private readonly string _installerPath1 = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\OneDriveSetup.exe");
    private readonly string _installerPath2 = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\SysWOW64\OneDriveSetup.exe");

    public UninstallOneDriveItem()
    {
        IsOptimized = !File.Exists(Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\OneDrive\OneDrive.exe"));
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
