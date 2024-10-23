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
    public abstract string Name { get; }
    public abstract string Description { get; }

    [ObservableProperty]
    private bool _hasOptimized;

    protected bool IsInitializing = true;

    public static bool InBatching { get; set; }

    async partial void OnHasOptimizedChanged(bool value)
    {
        if (IsInitializing)
            return;

        if (ShouldTurnOffTamperProtection)
            if (!await TurnOffTamperProtection())
            {
                IsInitializing = true;
                HasOptimized = !value;
                IsInitializing = false;
                return;
            }

        HasOptimizedChanged(value);

        if (InBatching)
            return;

        if (ShouldUpdateGroupPolicy)
            await UpdateGroupPolicy();
    }

    private static readonly RegistryValue TamperProtectionRegistryValue = new(
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Features",
        "TamperProtection");

    private static async Task<bool> TurnOffTamperProtection()
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

    public abstract void HasOptimizedChanged(bool value);

    [ObservableProperty]
    private bool _isChecked = true;

    [RelayCommand]
    private void ToggleChecked() => IsChecked = !IsChecked;

    protected static string MessageTitle => "Jeek Windows Optimizer";

    public bool ShouldTurnOffTamperProtection { get; set; }
    public bool ShouldUpdateGroupPolicy { get; set; }
    public bool ShouldReboot { get; set; }
}
