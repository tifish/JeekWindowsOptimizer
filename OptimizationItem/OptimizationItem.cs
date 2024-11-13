using System.Diagnostics;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JeekTools;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;

namespace JeekWindowsOptimizer;

public abstract partial class OptimizationItem : ObservableObject
{
    public abstract string GroupName { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }

    [ObservableProperty]
    private bool _isOptimized;

    protected bool IsInitializing = true;

    public static bool InBatching { get; set; }

    public async Task<bool> CallIsOptimizedChangingEvent(bool value)
    {
        if (IsInitializing)
            return true;

        if (ShouldTurnOffTamperProtection)
            if (!await TurnOffTamperProtection())
            {
                IsInitializing = true;
                IsOptimized = !value;
                IsInitializing = false;
                return true;
            }

        if (!await IsOptimizedChanging(value))
            return false;

        if (InBatching)
            return true;

        if (ShouldUpdateGroupPolicy)
            await UpdateGroupPolicy();

        if (ShouldRestartExplorer)
            RestartExplorer();

        if (ShouldReboot)
            await PromptReboot();

        return true;
    }

    protected abstract Task<bool> IsOptimizedChanging(bool value);

    private static readonly RegistryValue TamperProtectionRegistryValue = new(
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Features",
        "TamperProtection");

    public static async Task<bool> TurnOffTamperProtection()
    {
        if (TamperProtectionRegistryValue.GetValue(5) != 5)
            return true;

        var openDefenderCommand = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Windows Defender\MSASCui.exe");
        if (!File.Exists(openDefenderCommand))
            openDefenderCommand = "windowsdefender://Threatsettings";
        Process.Start(new ProcessStartInfo(openDefenderCommand)
        {
            UseShellExecute = true,
        });

        var msgResult = await MessageBoxManager.GetMessageBoxStandard(new MessageBoxStandardParams
        {
            ContentMessage = "请关闭防病毒设置中的篡改防护，然后按确定继续。",
            ButtonDefinitions = ButtonEnum.OkCancel,
            Icon = Icon.Info,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            FontFamily = "Microsoft YaHei",
        }).ShowAsync();
        if (msgResult != ButtonResult.Ok)
            return false;

        return TamperProtectionRegistryValue.GetValue(5) != 5;
    }

    public static async Task UpdateGroupPolicy()
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "gpupdate.exe",
            Arguments = "/force",
        });
        await proc!.WaitForExitAsync();
    }

    public static void RestartExplorer()
    {
        // Kill all explorer.exe processes
        foreach (var process in Process.GetProcessesByName("explorer"))
            process.Kill();
    }

    public static async Task PromptReboot()
    {
        await MessageBoxManager.GetMessageBoxStandard(new MessageBoxStandardParams
        {
            ContentMessage = "需要重启生效。",
            ButtonDefinitions = ButtonEnum.Ok,
            Icon = Icon.Info,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            FontFamily = "Microsoft YaHei",
        }).ShowAsync();
    }

    [ObservableProperty]
    private bool _isChecked = true;

    [RelayCommand]
    private void ToggleChecked()
    {
        IsChecked = !IsChecked;
    }

    protected static string MessageTitle => "Jeek Windows Optimizer";

    public bool ShouldTurnOffTamperProtection { get; set; }
    public bool ShouldUpdateGroupPolicy { get; set; }
    public bool ShouldReboot { get; set; }

    public bool ShouldRestartExplorer { get; set; }
}
