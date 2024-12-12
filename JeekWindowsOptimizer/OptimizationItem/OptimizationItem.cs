using System.Diagnostics;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Jeek.Avalonia.Localization;
using JeekTools;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;

namespace JeekWindowsOptimizer;

public abstract partial class OptimizationItem : ObservableObject
{
    public string GroupName => Localizer.Get(GroupNameKey);
    public string Name => Localizer.Get(NameKey);
    public string Description => Localizer.Get(DescriptionKey);

    public abstract string GroupNameKey { get; }
    public abstract string NameKey { get; }
    public abstract string DescriptionKey { get; }

    public bool IsPersonal { get; set; }

    [ObservableProperty]
    public partial bool IsOptimized { get; protected set; }

    public static bool InBatching { get; set; }

    public async Task<bool> SetIsOptimized(bool value)
    {
        if (value == IsOptimized)
            return false;

        if (!InBatching)
        {
            if (ShouldTurnOffTamperProtection)
                if (!await TurnOffTamperProtection())
                {
                    IsOptimized = !value;
                    return false;
                }

            if (ShouldTurnOffOnAccessProtection)
                if (!await TurnOffOnAccessProtection())
                {
                    IsOptimized = !value;
                    return false;
                }
        }

        if (!await IsOptimizedChanging(value))
            return false;

        IsOptimized = value;

        if (!InBatching)
        {
            if (ShouldUpdateGroupPolicy)
                await UpdateGroupPolicy();

            if (ShouldRestartExplorer)
                RestartExplorer();

            if (ShouldReboot)
                await PromptReboot();
        }

        return true;
    }

    protected abstract Task<bool> IsOptimizedChanging(bool value);

    private static readonly RegistryValue TamperProtectionRegistryValue = new(
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Features",
        "TamperProtection");

    public static async Task<bool> TurnOffTamperProtection()
    {
        if (TamperProtectionRegistryValue.GetValue(0) is 0 or 4)
            return true;

        OpenDefenderSettings();

        while (TamperProtectionRegistryValue.GetValue(0) is not (0 or 4))
        {
            var msgResult = await MessageBoxManager.GetMessageBoxStandard(new MessageBoxStandardParams
            {
                ContentMessage = "请关闭防病毒设置中的篡改防护，然后按确定继续。",
                ButtonDefinitions = ButtonEnum.OkCancel,
                Icon = Icon.Info,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true,
                FontFamily = "Microsoft YaHei",
            }).ShowAsync();

            if (msgResult == ButtonResult.Cancel)
                return false;
        }

        return true;
    }

    private static readonly RegistryValue DisableOnAccessProtectionRegistryValue = new(
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection",
        "DisableOnAccessProtection");

    public static async Task<bool> TurnOffOnAccessProtection()
    {
        if (DisableOnAccessProtectionRegistryValue.GetValue(0) is 1)
            return true;

        await TurnOffTamperProtection();

        DisableOnAccessProtectionRegistryValue.SetValue(1);

        return true;
    }

    private static void OpenDefenderSettings()

    {
        var openDefenderCommand = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Windows Defender\MSASCui.exe");
        if (!File.Exists(openDefenderCommand))
            openDefenderCommand = "windowsdefender://Threatsettings";
        Process.Start(new ProcessStartInfo(openDefenderCommand)
        {
            UseShellExecute = true,
        });
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
    public partial bool IsChecked { get; set; } = true;

    public void ToggleChecked()
    {
        IsChecked = !IsChecked;
    }

    protected static string MessageTitle => "Jeek Windows Optimizer";

    public bool ShouldTurnOffTamperProtection { get; set; }
    public bool ShouldTurnOffOnAccessProtection { get; set; }
    public bool ShouldUpdateGroupPolicy { get; set; }
    public bool ShouldReboot { get; set; }
    public bool ShouldRestartExplorer { get; set; }

    public void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(GroupName));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
    }
}
