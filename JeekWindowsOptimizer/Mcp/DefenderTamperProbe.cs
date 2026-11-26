using System.Text;
using Microsoft.Win32;
using MsBox.Avalonia.Enums;

namespace JeekWindowsOptimizer.Mcp;

internal static class DefenderTamperProbe
{
    private const string ScratchKeySubPath =
        @"Software\JeekWindowsOptimizer\DebugProbe\DefenderTamper";

    public static async Task<string> RunAsync(MainViewModel vm)
    {
        var sb = new StringBuilder();
        var failures = new List<string>();

        void Check(string name, bool condition, string? detail = null)
        {
            sb.AppendLine($"  [{(condition ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : $" ({detail})")}");
            if (!condition)
                failures.Add(detail is null ? name : $"{name}: {detail}");
        }

        sb.AppendLine("1. Status matrix checks");
        Check(
            "WMI runtime tamper ON (Normal, no 3rd-party AV) blocks",
            new DefenderTamperProtectionStatus(true, 5, true, "Normal", false).IsBlockingDefenderPolicies
        );
        Check(
            "WMI runtime tamper ON (Normal, stale 3rd-party AV entry) still blocks",
            new DefenderTamperProtectionStatus(true, 5, true, "Normal", true).IsBlockingDefenderPolicies
        );
        Check(
            "WMI runtime tamper OFF (Normal) does not block",
            !new DefenderTamperProtectionStatus(false, 4, false, "Normal", false).IsBlockingDefenderPolicies
        );
        Check(
            "WMI Defender Passive mode does not block",
            !new DefenderTamperProtectionStatus(true, 5, false, "Passive mode", true).IsBlockingDefenderPolicies
        );
        Check(
            "WMI Defender Not running does not block",
            !new DefenderTamperProtectionStatus(true, 5, false, "Not running", true).IsBlockingDefenderPolicies
        );
        Check(
            "Fallback registry=5 without 3rd-party AV blocks",
            new DefenderTamperProtectionStatus(null, 5, null, null, false).IsBlockingDefenderPolicies
        );
        Check(
            "Fallback registry=1 without 3rd-party AV blocks",
            new DefenderTamperProtectionStatus(null, 1, null, null, false).IsBlockingDefenderPolicies
        );
        Check(
            "Fallback registry=5 with 3rd-party AV does not block",
            !new DefenderTamperProtectionStatus(null, 5, null, null, true).IsBlockingDefenderPolicies
        );
        Check(
            "Fallback registry=4 does not block",
            !new DefenderTamperProtectionStatus(null, 4, null, null, false).IsBlockingDefenderPolicies
        );
        Check(
            "Fallback registry=0 does not block",
            !new DefenderTamperProtectionStatus(null, 0, null, null, false).IsBlockingDefenderPolicies
        );

        var runSubPath = $@"{ScratchKeySubPath}\{Guid.NewGuid():N}";
        var fullKeyPath = $@"HKEY_CURRENT_USER\{runSubPath}";

        var prevStatusOverride = DefenderProtection.StatusOverrideForTesting;
        var prevOpenOverride = DefenderProtection.OpenSettingsOverrideForTesting;
        var prevPromptOverride = OptimizationItem.TamperProtectionPromptOverrideForTesting;
        var prevGpOverride = OptimizationItem.UpdateGroupPolicyOverrideForTesting;
        var prevInBatching = OptimizationItem.InBatching;

        try
        {
            var simulatedStatus = new DefenderTamperProtectionStatus(true, 5, true, "Normal", false);
            var openSettingsCount = 0;
            var gpUpdateCount = 0;
            var prompts = new List<(string MessageKey, bool IsRetry)>();

            DefenderProtection.StatusOverrideForTesting = () => Task.FromResult(simulatedStatus);
            DefenderProtection.OpenSettingsOverrideForTesting = () => openSettingsCount++;
            OptimizationItem.UpdateGroupPolicyOverrideForTesting = () =>
            {
                gpUpdateCount++;
                return Task.CompletedTask;
            };

            DefenderTamperProtectedRegistryItem CreateScratchItem(
                string nameKey,
                string descriptionKey,
                params OptimizationRegistryValue[] values
            )
            {
                var item = new DefenderTamperProtectedRegistryItem("System", nameKey, descriptionKey)
                {
                    Category = OptimizationItemCategory.Antivirus,
                    ShouldTurnOffTamperProtection = true,
                    ShouldUpdateGroupPolicy = true,
                };
                item.RegistryValues.AddRange(values);
                return item;
            }

            sb.AppendLine("2. Detection checks against isolated HKCU scratch keys");
            var realTimeItem = CreateScratchItem(
                DefenderTamperProtectedRegistryItem.DisableRealTimeAntivirusNameKey,
                "DisableWindowsRealTimeAntivirusDescription",
                new OptimizationRegistryIntValue(fullKeyPath, "DisableRealtimeMonitoring", 0, 1, true),
                new OptimizationRegistryIntValue(fullKeyPath, "DisableBehaviorMonitoring", 0, 1, true)
            );
            var sampleItem = CreateScratchItem(
                DefenderTamperProtectedRegistryItem.DisableSampleSubmissionNameKey,
                "DisableWindowsDefenderSampleSubmissionDescription",
                new OptimizationRegistryIntValue(fullKeyPath, "SubmitSamplesConsent", 1, 2, true)
            );

            // Write optimized scratch values while Tamper Protection is ON.
            foreach (var v in realTimeItem.RegistryValues.Concat(sampleItem.RegistryValues))
                v.IsOptimized = true;

            simulatedStatus = new DefenderTamperProtectionStatus(true, 5, true, "Normal", false);
            await realTimeItem.Initialize();
            await sampleItem.Initialize();
            Check(
                "Real-time: registry optimized + Tamper ON => IsOptimized=false, IsPrerequisiteBlocked=true",
                !realTimeItem.IsOptimized && realTimeItem.IsPrerequisiteBlocked && !realTimeItem.ShowNeutralNotOptimizedPill,
                $"IsOptimized={realTimeItem.IsOptimized}, IsPrerequisiteBlocked={realTimeItem.IsPrerequisiteBlocked}"
            );
            Check(
                "Sample submission: registry optimized + Tamper ON => IsOptimized=false, IsPrerequisiteBlocked=true",
                !sampleItem.IsOptimized && sampleItem.IsPrerequisiteBlocked && !sampleItem.ShowNeutralNotOptimizedPill,
                $"IsOptimized={sampleItem.IsOptimized}, IsPrerequisiteBlocked={sampleItem.IsPrerequisiteBlocked}"
            );

            simulatedStatus = new DefenderTamperProtectionStatus(false, 4, false, "Normal", false);
            await realTimeItem.Initialize();
            await sampleItem.Initialize();
            Check(
                "Real-time: registry optimized + Tamper OFF => IsOptimized=true, IsPrerequisiteBlocked=false",
                realTimeItem.IsOptimized && !realTimeItem.IsPrerequisiteBlocked && !realTimeItem.ShowNeutralNotOptimizedPill,
                $"IsOptimized={realTimeItem.IsOptimized}, IsPrerequisiteBlocked={realTimeItem.IsPrerequisiteBlocked}"
            );
            Check(
                "Sample submission: registry optimized + Tamper OFF => IsOptimized=true, IsPrerequisiteBlocked=false",
                sampleItem.IsOptimized && !sampleItem.IsPrerequisiteBlocked && !sampleItem.ShowNeutralNotOptimizedPill,
                $"IsOptimized={sampleItem.IsOptimized}, IsPrerequisiteBlocked={sampleItem.IsPrerequisiteBlocked}"
            );

            foreach (var v in realTimeItem.RegistryValues.Concat(sampleItem.RegistryValues))
                v.IsOptimized = false;

            await realTimeItem.Initialize();
            await sampleItem.Initialize();
            Check(
                "Both items: registry default + Tamper OFF => IsOptimized=false, IsPrerequisiteBlocked=false",
                !realTimeItem.IsOptimized && !realTimeItem.IsPrerequisiteBlocked && realTimeItem.ShowNeutralNotOptimizedPill
                    && !sampleItem.IsOptimized && !sampleItem.IsPrerequisiteBlocked && sampleItem.ShowNeutralNotOptimizedPill
            );

            simulatedStatus = new DefenderTamperProtectionStatus(true, 5, true, "Normal", false);
            await realTimeItem.Initialize();
            await sampleItem.Initialize();
            Check(
                "Both items: registry default + Tamper ON => IsOptimized=false, IsPrerequisiteBlocked=true",
                !realTimeItem.IsOptimized && realTimeItem.IsPrerequisiteBlocked
                    && !sampleItem.IsOptimized && sampleItem.IsPrerequisiteBlocked
            );

            sb.AppendLine("3. Interactive Tamper Protection gating checks");
            // Case: User cancels when prompted on sample submission item.
            openSettingsCount = 0;
            gpUpdateCount = 0;
            prompts.Clear();
            simulatedStatus = new DefenderTamperProtectionStatus(true, 5, true, "Normal", false);
            OptimizationItem.TamperProtectionPromptOverrideForTesting = (key, isRetry) =>
            {
                prompts.Add((key, isRetry));
                return Task.FromResult(ButtonResult.Cancel);
            };

            var cancelResult = await sampleItem.SetIsOptimized(true);
            Check(
                "Sample submission SetIsOptimized(true) while Tamper ON + Cancel => fails cleanly without writing registry",
                !cancelResult
                    && !sampleItem.IsOptimized
                    && sampleItem.IsPrerequisiteBlocked
                    && openSettingsCount == 1
                    && prompts.Count == 1
                    && prompts[0] == ("TamperProtectionPromptMessage", false)
                    && gpUpdateCount == 0
                    && sampleItem.RegistryValues.All(v => !v.IsOptimized),
                $"result={cancelResult}, open={openSettingsCount}, prompts={prompts.Count}, gp={gpUpdateCount}"
            );

            // Case: User clicks OK once while Tamper is STILL ON, then turns it OFF and clicks OK.
            openSettingsCount = 0;
            gpUpdateCount = 0;
            prompts.Clear();
            simulatedStatus = new DefenderTamperProtectionStatus(true, 5, true, "Normal", false);
            OptimizationItem.TamperProtectionPromptOverrideForTesting = (key, isRetry) =>
            {
                prompts.Add((key, isRetry));
                if (prompts.Count == 2)
                    simulatedStatus = new DefenderTamperProtectionStatus(false, 4, false, "Normal", false);
                return Task.FromResult(ButtonResult.Ok);
            };

            var retrySuccess = await sampleItem.SetIsOptimized(true);
            var writtenSampleValue = Registry.CurrentUser.OpenSubKey(runSubPath)?.GetValue("SubmitSamplesConsent");
            Check(
                "Sample submission SetIsOptimized(true) retries while Tamper ON, writes SubmitSamplesConsent=2 once OFF",
                retrySuccess
                    && sampleItem.IsOptimized
                    && !sampleItem.IsPrerequisiteBlocked
                    && openSettingsCount == 2
                    && prompts.Count == 2
                    && prompts[0] == ("TamperProtectionPromptMessage", false)
                    && prompts[1] == ("TamperProtectionStillEnabledPromptMessage", true)
                    && gpUpdateCount == 1
                    && writtenSampleValue is 2,
                $"result={retrySuccess}, open={openSettingsCount}, prompts={prompts.Count}, gp={gpUpdateCount}, value={writtenSampleValue}"
            );

            // Case: Reverting (SetIsOptimized(false)) deletes policy value without prompting.
            openSettingsCount = 0;
            gpUpdateCount = 0;
            prompts.Clear();
            simulatedStatus = new DefenderTamperProtectionStatus(true, 5, true, "Normal", false);
            var revertResult = await sampleItem.SetIsOptimized(false);
            var revertedSampleValue = Registry.CurrentUser.OpenSubKey(runSubPath)?.GetValue("SubmitSamplesConsent");
            Check(
                "Sample submission SetIsOptimized(false) deletes SubmitSamplesConsent without prompting and refreshes blocked state",
                revertResult
                    && !sampleItem.IsOptimized
                    && sampleItem.IsPrerequisiteBlocked
                    && openSettingsCount == 0
                    && prompts.Count == 0
                    && gpUpdateCount == 1
                    && revertedSampleValue is null,
                $"result={revertResult}, blocked={sampleItem.IsPrerequisiteBlocked}, value={revertedSampleValue ?? "null"}"
            );

            // Case: Direct batching guard refuses to apply if Tamper Protection is still ON.
            openSettingsCount = 0;
            gpUpdateCount = 0;
            prompts.Clear();
            simulatedStatus = new DefenderTamperProtectionStatus(true, 5, true, "Normal", false);
            OptimizationItem.InBatching = true;
            var batchGuardResult = await realTimeItem.SetIsOptimized(true);
            OptimizationItem.InBatching = false;
            Check(
                "InBatching safety guard blocks IsOptimizedChanging(true) while Tamper ON",
                !batchGuardResult
                    && !realTimeItem.IsOptimized
                    && realTimeItem.IsPrerequisiteBlocked
                    && realTimeItem.RegistryValues.All(v => !v.IsOptimized),
                $"result={batchGuardResult}, IsOptimized={realTimeItem.IsOptimized}"
            );

            sb.AppendLine("4. Manual settings open + window reactivation recheck");
            foreach (var v in sampleItem.RegistryValues)
                v.IsOptimized = true;
            simulatedStatus = new DefenderTamperProtectionStatus(true, 5, true, "Normal", false);
            await sampleItem.Initialize();
            openSettingsCount = 0;

            await vm.OpenOptimizationPrerequisiteSettingsAsync(sampleItem);
            Check("OpenOptimizationPrerequisiteSettingsAsync launches Defender settings", openSettingsCount == 1);

            simulatedStatus = new DefenderTamperProtectionStatus(false, 4, false, "Normal", false);
            await vm.RecheckPendingPrerequisiteItemAsync();
            Check(
                "RecheckPendingPrerequisiteItemAsync clears warning and restores IsOptimized once Tamper OFF",
                sampleItem.IsOptimized && !sampleItem.IsPrerequisiteBlocked,
                $"IsOptimized={sampleItem.IsOptimized}, IsPrerequisiteBlocked={sampleItem.IsPrerequisiteBlocked}"
            );
        }
        finally
        {
            DefenderProtection.StatusOverrideForTesting = prevStatusOverride;
            DefenderProtection.OpenSettingsOverrideForTesting = prevOpenOverride;
            OptimizationItem.TamperProtectionPromptOverrideForTesting = prevPromptOverride;
            OptimizationItem.UpdateGroupPolicyOverrideForTesting = prevGpOverride;
            OptimizationItem.InBatching = prevInBatching;

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(runSubPath, throwOnMissingSubKey: false);
            }
            catch
            {
                // Best-effort cleanup of scratch key.
            }
        }

        sb.AppendLine("5. Live registered item checks");
        var liveStatus = await DefenderProtection.GetTamperProtectionStatus();
        foreach (var expectedKey in new[]
                 {
                     DefenderTamperProtectedRegistryItem.DisableRealTimeAntivirusNameKey,
                     DefenderTamperProtectedRegistryItem.DisableSampleSubmissionNameKey,
                 })
        {
            var liveItem = vm.AntivirusGroups
                .SelectMany(group => group.Items)
                .FirstOrDefault(item => item.NameKey == expectedKey);
            Check(
                $"Antivirus tab contains {expectedKey} as DefenderTamperProtectedRegistryItem",
                liveItem is DefenderTamperProtectedRegistryItem
            );
            if (liveItem is DefenderTamperProtectedRegistryItem defenderItem)
            {
                Check(
                    $"{expectedKey} flags ShouldTurnOffTamperProtection and ShouldUpdateGroupPolicy",
                    defenderItem.ShouldTurnOffTamperProtection && defenderItem.ShouldUpdateGroupPolicy
                );
                Check(
                    $"{expectedKey} has localized name, description, and warning copy",
                    !string.IsNullOrWhiteSpace(defenderItem.Name)
                        && defenderItem.Name != expectedKey
                        && !string.IsNullOrWhiteSpace(defenderItem.Description)
                        && !string.IsNullOrWhiteSpace(defenderItem.PrerequisiteWarningText)
                        && !string.IsNullOrWhiteSpace(defenderItem.PrerequisiteActionText)
                );
                Check(
                    $"{expectedKey} IsPrerequisiteBlocked matches DefenderProtection.IsBlockingDefenderPolicies",
                    defenderItem.IsPrerequisiteBlocked == liveStatus.IsBlockingDefenderPolicies,
                    $"itemBlocked={defenderItem.IsPrerequisiteBlocked}, liveBlocking={liveStatus.IsBlockingDefenderPolicies}"
                );
                Check(
                    $"{expectedKey} is never IsOptimized when Tamper Protection is blocking",
                    !liveStatus.IsBlockingDefenderPolicies || !defenderItem.IsOptimized,
                    $"itemOptimized={defenderItem.IsOptimized}, liveBlocking={liveStatus.IsBlockingDefenderPolicies}"
                );
            }
        }

        sb.AppendLine();
        sb.AppendLine(
            failures.Count == 0
                ? "RESULT: PASS (all Defender Tamper Protection checks passed)"
                : $"RESULT: FAIL ({failures.Count} failure(s): {string.Join("; ", failures)})"
        );
        return sb.ToString();
    }
}
