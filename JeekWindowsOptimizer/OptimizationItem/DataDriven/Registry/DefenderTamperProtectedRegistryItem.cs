using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

/// <summary>
/// Windows Defender policy optimization whose registry keys are ignored by Defender while
/// Tamper Protection is enabled (e.g. real-time scanning, automatic sample submission).
/// Both status detection and applying the optimization verify that Tamper Protection is off.
/// </summary>
public sealed class DefenderTamperProtectedRegistryItem(
    string groupNameKey,
    string nameKey,
    string descriptionKey
) : RegistryItem(groupNameKey, nameKey, descriptionKey)
{
    public const string DisableRealTimeAntivirusNameKey = "DisableWindowsRealTimeAntivirusName";
    public const string DisableSampleSubmissionNameKey = "DisableWindowsDefenderSampleSubmissionName";

    public static bool IsTamperProtectedNameKey(string nameKey) =>
        nameKey is DisableRealTimeAntivirusNameKey or DisableSampleSubmissionNameKey;

    public override string PrerequisiteBadgeText => Localizer.Get("TamperProtectionOnBadge");

    public override string PrerequisiteWarningText =>
        Localizer.Get("DefenderTamperProtectionWarning");

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
        IsPrerequisiteBlocked = tamperStatus.IsBlockingDefenderPolicies;
        IsOptimized = (await registryOptimizedTask) && !tamperStatus.IsBlockingDefenderPolicies;
    }

    protected override async Task<bool> IsOptimizedChanging(bool value)
    {
        if (value)
        {
            var tamperStatus = await DefenderProtection.GetTamperProtectionStatus();
            IsPrerequisiteBlocked = tamperStatus.IsBlockingDefenderPolicies;
            if (tamperStatus.IsBlockingDefenderPolicies)
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
        IsPrerequisiteBlocked = currentStatus.IsBlockingDefenderPolicies;
        return reverted;
    }

    public override async Task OnTamperProtectionCheckFailedAsync()
    {
        var tamperStatus = await DefenderProtection.GetTamperProtectionStatus();
        IsPrerequisiteBlocked = tamperStatus.IsBlockingDefenderPolicies;
    }

    public override Task OpenPrerequisiteSettingsAsync()
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            OpenDefenderSettings
        );
    }
}
