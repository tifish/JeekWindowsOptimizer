using System.Diagnostics;
using DotNetRun;
using Jeek.Avalonia.Localization;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using Avalonia.Controls;

namespace JeekWindowsOptimizer;

/// <summary>
/// Disables device search history under SearchSettings.
/// On recent Windows 11 builds, UCPD blocks third-party and denylisted
/// Microsoft tools (reg.exe, powershell.exe, …) from writing SearchSettings.
/// Direct writes are tried first; on failure we invoke a temporary copy of a
/// Microsoft-signed binary under a non-denylisted name (UCPD matches image name),
/// then fall back to Settings guidance if that still fails.
/// </summary>
public class DisableSearchHistoryItem : OptimizationItem
{
    private const string KeyPath =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\SearchSettings";
    private const string ValueName = "IsDeviceSearchHistoryEnabled";

    private readonly RegistryValue _value = new(KeyPath, ValueName);

    public override string GroupNameKey => "Privacy";
    public override string NameKey => "DisableSearchHistoryName";
    public override string DescriptionKey => "DisableSearchHistoryDescription";

    public override async Task Initialize()
    {
        IsOptimized = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            IsHistoryDisabled
        );
    }

    protected override async Task<bool> IsOptimizedChanging(bool value)
    {
        var targetDword = value ? 0 : 1;

        var applied = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () => TrySetHistoryEnabled(targetDword)
        );

        if (applied)
            return true;

        // Last resort: open Settings (Microsoft-signed host is allowlisted by UCPD).
        if (!value)
            return false;

        await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Ui,
            OpenSearchPermissionsSettings
        );

        await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Ui,
            async () =>
            {
                await MessageBoxManager
                    .GetMessageBoxStandard(
                        new MessageBoxStandardParams
                        {
                            ContentMessage = Localizer.Get("DisableSearchHistoryUcpdMessage"),
                            ButtonDefinitions = ButtonEnum.Ok,
                            Icon = Icon.Info,
                            WindowStartupLocation = WindowStartupLocation.CenterScreen,
                            Topmost = true,
                            FontFamily = "Microsoft YaHei",
                        }
                    )
                    .ShowAsync();
            }
        );

        return await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            IsHistoryDisabled
        );
    }

    private bool TrySetHistoryEnabled(int targetDword)
    {
        // 1) Direct write (works when UCPD is off or key is unprotected).
        try
        {
            _value.SetValue(targetDword);
            if (_value.GetValue(1) == targetDword)
                return true;
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            // Continue with host bypass.
        }

        // 2) Write via Microsoft-signed host under a non-denylisted image name.
        //    UCPD allows Microsoft-signed processes except a denylist (reg.exe,
        //    powershell.exe, regedit.exe, …). Renaming keeps the Authenticode
        //    signature valid while avoiding the denylist.
        if (TrySetViaMicrosoftHost(targetDword) && _value.GetValue(1) == targetDword)
            return true;

        return _value.GetValue(1) == targetDword;
    }

    private static bool TrySetViaMicrosoftHost(int targetDword)
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var regSource = Path.Combine(system32, "reg.exe");
        if (!File.Exists(regSource))
            return false;

        var workDir = Path.Combine(Path.GetTempPath(), "JeekWindowsOptimizer", "UcpdHost");
        Directory.CreateDirectory(workDir);

        var hostPath = Path.Combine(workDir, $"JeekRegHost-{Guid.NewGuid():N}.exe");
        try
        {
            File.Copy(regSource, hostPath, overwrite: true);

            // reg.exe add "HKCU\...\SearchSettings" /v IsDeviceSearchHistoryEnabled /t REG_DWORD /d N /f
            var relativeKey =
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\SearchSettings";
            var args =
                $"add \"{relativeKey}\" /v {ValueName} /t REG_DWORD /d {targetDword} /f";

            using var process = Process.Start(
                new ProcessStartInfo
                {
                    FileName = hostPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            );

            if (process is null)
                return false;

            process.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(hostPath))
                    File.Delete(hostPath);
            }
            catch
            {
                // Best-effort cleanup; temp files are harmless if locked briefly.
            }
        }
    }

    private bool IsHistoryDisabled()
    {
        // Missing value → Windows treats history as enabled.
        return _value.GetValue(1) == 0;
    }

    private static bool IsAccessDenied(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is UnauthorizedAccessException or System.Security.SecurityException)
                return true;
            if (
                e is IOException
                && e.HResult is unchecked((int)0x80070005) or unchecked((int)0x80004005)
            )
                return true;
        }

        return false;
    }

    private static void OpenSearchPermissionsSettings()
    {
        try
        {
            Process.Start(
                new ProcessStartInfo("ms-settings:searchpermissions")
                {
                    UseShellExecute = true,
                }
            );
        }
        catch
        {
            // Ignore launch failures; the message box still explains the manual steps.
        }
    }
}
