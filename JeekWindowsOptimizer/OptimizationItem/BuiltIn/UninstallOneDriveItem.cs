using System.Diagnostics;

namespace JeekWindowsOptimizer;

public class UninstallOneDriveItem : OptimizationItem
{
    public override string GroupNameKey => "Uninstall";
    public override string NameKey => "UninstallOneDriveName";

    public override string DescriptionKey => "UninstallOneDriveDescription";

    private readonly string _installerPath1 = Environment.ExpandEnvironmentVariables(
        @"%SystemRoot%\System32\OneDriveSetup.exe"
    );
    private readonly string _installerPath2 = Environment.ExpandEnvironmentVariables(
        @"%SystemRoot%\SysWOW64\OneDriveSetup.exe"
    );

    public override async Task Initialize()
    {
        IsOptimized = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () =>
                !File.Exists(
                    Environment.ExpandEnvironmentVariables(
                        @"%LOCALAPPDATA%\Microsoft\OneDrive\OneDrive.exe"
                    )
                )
        );
    }

    protected override async Task<bool> IsOptimizedChanging(bool value)
    {
        if (!value)
            return false;

        var installerPath = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () =>
            {
                var path = _installerPath1;
                if (!File.Exists(path))
                {
                    path = _installerPath2;
                    if (!File.Exists(path))
                        return null;
                }

                return path;
            }
        );

        if (installerPath is null)
            return false;

        using var proc = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () =>
                Process.Start(
                    new ProcessStartInfo(installerPath, "/uninstall") { UseShellExecute = true }
                )
        );

        if (proc is null)
            return false;

        await proc.WaitForExitAsync();
        return true;
    }
}
