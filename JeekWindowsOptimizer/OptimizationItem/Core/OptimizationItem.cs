using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using DotNetRun;
using Jeek.Avalonia.Localization;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;

namespace JeekWindowsOptimizer;

public enum OptimizationItemCategory
{
    Default,
    Antivirus,
    Personal,
}

public abstract partial class OptimizationItem : ObservableObject
{
    public string GroupName => Localizer.Get(GroupNameKey);
    public string Name => Localizer.Get(NameKey);
    public string Description => Localizer.Get(DescriptionKey);

    public abstract string GroupNameKey { get; }
    public abstract string NameKey { get; }
    public abstract string DescriptionKey { get; }

    public OptimizationItemCategory Category { get; set; }

    [ObservableProperty]
    public partial bool IsOptimized { get; protected set; }

    [ObservableProperty]
    public partial bool IsPrerequisiteBlocked { get; protected set; }

    public bool ShowNeutralNotOptimizedPill => !IsOptimized && !IsPrerequisiteBlocked;

    public virtual string PrerequisiteBadgeText => "";
    public virtual string PrerequisiteWarningText => "";
    public virtual string PrerequisiteActionText => "";

    partial void OnIsOptimizedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNeutralNotOptimizedPill));
    }

    partial void OnIsPrerequisiteBlockedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNeutralNotOptimizedPill));
    }

    public virtual Task OpenPrerequisiteSettingsAsync() => Task.CompletedTask;

    public virtual Task OnTamperProtectionCheckFailedAsync() => Task.CompletedTask;

    public static bool InBatching { get; set; }

    internal static Func<string, bool, Task<ButtonResult>>? TamperProtectionPromptOverrideForTesting { get; set; }
    internal static Func<Task>? UpdateGroupPolicyOverrideForTesting { get; set; }

    public abstract Task Initialize();

    /// <summary>How long <see cref="Initialize"/> took at startup (manual refreshes leave it unchanged); for diagnostics.</summary>
    public long InitializeMilliseconds { get; set; }

    public async Task<bool> SetIsOptimized(bool value)
    {
        if (value == IsOptimized)
            return false;

        if (!InBatching)
        {
            if (value && ShouldTurnOffTamperProtection)
            {
                if (!await TurnOffTamperProtection())
                {
                    await OnTamperProtectionCheckFailedAsync();
                    IsOptimized = !value;
                    return false;
                }
            }

            if (value && ShouldTurnOffOnAccessProtection)
            {
                if (!await TurnOffOnAccessProtection())
                {
                    IsOptimized = !value;
                    return false;
                }
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
                await RestartExplorer();

            if (ShouldReboot)
                await PromptReboot();
        }

        return true;
    }

    protected abstract Task<bool> IsOptimizedChanging(bool value);

    public static async Task<bool> TurnOffTamperProtection()
    {
        var status = await DefenderProtection.GetTamperProtectionStatus();
        if (!status.IsBlockingDefenderPolicies)
            return true;

        var firstAttempt = true;
        while (status.IsBlockingDefenderPolicies)
        {
            await OptimizationExecutionScheduler.RunAsync(
                OptimizationExecutionAffinity.Background,
                OpenDefenderSettings
            );

            var messageKey = firstAttempt
                ? "TamperProtectionPromptMessage"
                : "TamperProtectionStillEnabledPromptMessage";
            var isRetry = !firstAttempt;
            firstAttempt = false;

            var msgResult = await ShowTamperProtectionPromptAsync(messageKey, isRetry);
            if (msgResult == ButtonResult.Cancel)
                return false;

            status = await DefenderProtection.GetTamperProtectionStatus();
        }

        return true;
    }

    private static Task<ButtonResult> ShowTamperProtectionPromptAsync(
        string messageKey,
        bool isRetry
    )
    {
        if (TamperProtectionPromptOverrideForTesting is { } promptOverride)
            return promptOverride(messageKey, isRetry);

        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Ui,
            () =>
            {
                var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                    ?.MainWindow;
                var box = MessageBoxManager.GetMessageBoxStandard(
                    new MessageBoxStandardParams
                    {
                        ContentTitle = MessageTitle,
                        ContentMessage = Localizer.Get(messageKey),
                        ButtonDefinitions = ButtonEnum.OkCancel,
                        Icon = Icon.Warning,
                        WindowStartupLocation = owner is not null
                            ? WindowStartupLocation.CenterOwner
                            : WindowStartupLocation.CenterScreen,
                        // Keep non-topmost so Windows Security stays unobstructed while the user toggles Tamper Protection.
                        Topmost = false,
                        FontFamily = Localizer.Get("DefaultFontName"),
                    }
                );

                return owner is not null
                    ? box.ShowWindowDialogAsync(owner)
                    : box.ShowAsync();
            }
        );
    }

    private static readonly RegistryValue DisableOnAccessProtectionRegistryValue = new(
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection",
        "DisableOnAccessProtection"
    );

    public static async Task<bool> TurnOffOnAccessProtection()
    {
        if (
            await OptimizationExecutionScheduler.RunAsync(
                OptimizationExecutionAffinity.Background,
                () => DisableOnAccessProtectionRegistryValue.GetValue(0) is 1
            )
        )
            return true;

        if (!await TurnOffTamperProtection())
            return false;

        return await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () =>
            {
                DisableOnAccessProtectionRegistryValue.SetValue(1);
                return DisableOnAccessProtectionRegistryValue.GetValue(0) is 1;
            }
        );
    }

    public static void OpenDefenderSettings()
    {
        DefenderProtection.OpenTamperProtectionSettings();
    }

    public static async Task UpdateGroupPolicy()
    {
        if (UpdateGroupPolicyOverrideForTesting is { } gpOverride)
        {
            await gpOverride();
            return;
        }

        using var proc = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () =>
                Process.Start(
                    new ProcessStartInfo { FileName = "gpupdate.exe", Arguments = "/force" }
                )
        );
        await proc!.WaitForExitAsync();
    }

    public static Task RestartExplorer()
    {
        return BuiltInToolActions.RestartExplorer();
    }

    public static async Task PromptReboot()
    {
        await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Ui,
            async () =>
            {
                await MessageBoxManager
                    .GetMessageBoxStandard(
                        new MessageBoxStandardParams
                        {
                            ContentTitle = MessageTitle,
                            ContentMessage = Localizer.Get("RebootRequiredPromptMessage"),
                            ButtonDefinitions = ButtonEnum.Ok,
                            Icon = Icon.Info,
                            WindowStartupLocation = WindowStartupLocation.CenterScreen,
                            Topmost = true,
                            FontFamily = Localizer.Get("DefaultFontName"),
                        }
                    )
                    .ShowAsync();
            }
        );
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
        OnPropertyChanged(nameof(PrerequisiteBadgeText));
        OnPropertyChanged(nameof(PrerequisiteWarningText));
        OnPropertyChanged(nameof(PrerequisiteActionText));
    }
}
