using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

/// <summary>
/// Windows Defender real-time scanning policy optimization.
/// Defender ignores Real-Time Protection policy keys while Tamper Protection is enabled,
/// so both status detection and optimization verify that Tamper Protection is off.
/// </summary>
public sealed class DisableWindowsRealTimeAntivirusItem(
    string groupNameKey,
    string nameKey,
    string descriptionKey
) : RegistryItem(groupNameKey, nameKey, descriptionKey)
{
    public const string ItemNameKey = "DisableWindowsRealTimeAntivirusName";

    public override string PrerequisiteBadgeText => Localizer.Get("TamperProtectionOnBadge");

    public override string PrerequisiteWarningText =>
        Localizer.Get("DisableWindowsRealTimeAntivirusTamperWarning");

    public override string PrerequisiteActionText =>
        Localizer.Get("OpenTamperProtectionSettings");

    public override async Task Initialize()
    {
        var registryOptimizedTask = OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () => RegistryValues.All(value => value.IsOptimized)
        );
        var tamperStatusTask = DefenderProtection.GetTamperProtectionStatus();

        await Task.WhenAll(registryOptimizedTask, tamperStatusTask);

        var tamperStatus = await tamperStatusTask;
        IsPrerequisiteBlocked = tamperStatus.IsBlockingRealTimeDisable;
        IsOptimized = (await registryOptimizedTask) && !tamperStatus.IsBlockingRealTimeDisable;
    }

    protected override async Task<bool> IsOptimizedChanging(bool value)
    {
        if (value)
        {
            var tamperStatus = await DefenderProtection.GetTamperProtectionStatus();
            IsPrerequisiteBlocked = tamperStatus.IsBlockingRealTimeDisable;
            if (tamperStatus.IsBlockingRealTimeDisable)
                return false;

            if (!await base.IsOptimizedChanging(true))
                return false;

            return await OptimizationExecutionScheduler.RunAsync(
                OptimizationExecutionAffinity.Background,
                () => RegistryValues.All(entry => entry.IsOptimized)
            );
        }

        var reverted = await base.IsOptimizedChanging(false);
        var currentStatus = await DefenderProtection.GetTamperProtectionStatus();
        IsPrerequisiteBlocked = currentStatus.IsBlockingRealTimeDisable;
        return reverted;
    }

    public override async Task OnTamperProtectionCheckFailedAsync()
    {
        var tamperStatus = await DefenderProtection.GetTamperProtectionStatus();
        IsPrerequisiteBlocked = tamperStatus.IsBlockingRealTimeDisable;
    }

    public override Task OpenPrerequisiteSettingsAsync()
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            OpenDefenderSettings
        );
    }
}
