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
            new DefenderTamperProtectionStatus(true, 5, true, "Normal", false).IsBlockingRealTimeDisable
        );
        Check(
            "WMI runtime tamper ON (Normal, stale 3rd-party AV entry) still blocks",
            new DefenderTamperProtectionStatus(true, 5, true, "Normal", true).IsBlockingRealTimeDisable
        );
        Check(
            "WMI runtime tamper OFF (Normal) does not block",
            !new DefenderTamperProtectionStatus(false, 4, false, "Normal", false).IsBlockingRealTimeDisable
        );
        Check(
            "WMI Defender Passive mode does not block",
            !new DefenderTamperProtectionStatus(true, 5, false, "Passive mode", true).IsBlockingRealTimeDisable
        );
        Check(
            "WMI Defender Not running does not block",
            !new DefenderTamperProtectionStatus(true, 5, false, "Not running", true).IsBlockingRealTimeDisable
        );
        Check(
            "Fallback registry=5 without 3rd-party AV blocks",
            new DefenderTamperProtectionStatus(null, 5, null, null, false).IsBlockingRealTimeDisable
        );
        Check(
            "Fallback registry=1 without 3rd-party AV blocks",
            new DefenderTamperProtectionStatus(null, 1, null, null, false).IsBlockingRealTimeDisable
        );
        Check(
            "Fallback registry=5 with 3rd-party AV does not block",
            !new DefenderTamperProtectionStatus(null, 5, null, null, true).IsBlockingRealTimeDisable
        );
        Check(
            "Fallback registry=4 does not block",
            !new DefenderTamperProtectionStatus(null, 4, null, null, false).IsBlockingRealTimeDisable
        );
        Check(
            "Fallback registry=0 does not block",
            !new DefenderTamperProtectionStatus(null, 0, null, null, false).IsBlockingRealTimeDisable
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

            DisableWindowsRealTimeAntivirusItem CreateScratchItem()
            {
                var item = new DisableWindowsRealTimeAntivirusItem(
                    "System",
                    DisableWindowsRealTimeAntivirusItem.ItemNameKey,
                    "DisableWindowsRealTimeAntivirusDescription"
                )
                {
                    Category = OptimizationItemCategory.Antivirus,
                    ShouldTurnOffTamperProtection = true,
                    ShouldUpdateGroupPolicy = true,
                };
                item.RegistryValues.Add(
                    new OptimizationRegistryIntValue(fullKeyPath, "DisableRealtimeMonitoring", 0, 1, true)
                );
                item.RegistryValues.Add(
                    new OptimizationRegistryIntValue(fullKeyPath, "DisableBehaviorMonitoring", 0, 1, true)
                );
                return item;
            }

            sb.AppendLine("2. Detection checks against isolated HKCU scratch keys");
            var item = CreateScratchItem();

            // Write optimized scratch values while Tamper Protection is ON.
            foreach (var v in item.RegistryValues)
                v.IsOptimized = true;

            simulatedStatus = new DefenderTamperProtectionStatus(true, 5, true, "Normal", false);
            await item.Initialize();
            Check(
                "Registry optimized + Tamper ON => IsOptimized=false, IsPrerequisiteBlocked=true",
                !item.IsOptimized && item.IsPrerequisiteBlocked && !item.ShowNeutralNotOptimizedPill,
                $"IsOptimized={item.IsOptimized}, IsPrerequisiteBlocked={item.IsPrerequisiteBlocked}"
            );

            simulatedStatus = new DefenderTamperProtectionStatus(false, 4, false, "Normal", false);
            await item.Initialize();
            Check(
                "Registry optimized + Tamper OFF => IsOptimized=true, IsPrerequisiteBlocked=false",
                item.IsOptimized && !item.IsPrerequisiteBlocked && !item.ShowNeutralNotOptimizedPill,
                $"IsOptimized={item.IsOptimized}, IsPrerequisiteBlocked={item.IsPrerequisiteBlocked}"
            );

            foreach (var v in item.RegistryValues)
                v.IsOptimized = false;

            await item.Initialize();
            Check(
                "Registry default + Tamper OFF => IsOptimized=false, IsPrerequisiteBlocked=false",
                !item.IsOptimized && !item.IsPrerequisiteBlocked && item.ShowNeutralNotOptimizedPill,
                $"IsOptimized={item.IsOptimized}, IsPrerequisiteBlocked={item.IsPrerequisiteBlocked}"
            );

            simulatedStatus = new DefenderTamperProtectionStatus(true, 5, true, "Normal", false);
            await item.Initialize();
            Check(
                "Registry default + Tamper ON => IsOptimized=false, IsPrerequisiteBlocked=true",
                !item.IsOptimized && item.IsPrerequisiteBlocked && !item.ShowNeutralNotOptimizedPill,
                $"IsOptimized={item.IsOptimized}, IsPrerequisiteBlocked={item.IsPrerequisiteBlocked}"
            );

            sb.AppendLine("3. Interactive Tamper Protection gating checks");
            // Case: User cancels when prompted.
            openSettingsCount = 0;
            gpUpdateCount = 0;
            prompts.Clear();
            simulatedStatus = new DefenderTamperProtectionStatus(true, 5, true, "Normal", false);
            OptimizationItem.TamperProtectionPromptOverrideForTesting = (key, isRetry) =>
            {
                prompts.Add((key, isRetry));
                return Task.FromResult(ButtonResult.Cancel);
            };

            var cancelResult = await item.SetIsOptimized(true);
            Check(
                "SetIsOptimized(true) while Tamper ON + Cancel => fails cleanly without writing registry",
                !cancelResult
                    && !item.IsOptimized
                    && item.IsPrerequisiteBlocked
                    && openSettingsCount == 1
                    && prompts.Count == 1
                    && prompts[0] == ("TamperProtectionPromptMessage", false)
                    && gpUpdateCount == 0
                    && item.RegistryValues.All(v => !v.IsOptimized),
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

            var retrySuccess = await item.SetIsOptimized(true);
            Check(
                "SetIsOptimized(true) retries while Tamper ON, succeeds once OFF",
                retrySuccess
                    && item.IsOptimized
                    && !item.IsPrerequisiteBlocked
                    && openSettingsCount == 2
                    && prompts.Count == 2
                    && prompts[0] == ("TamperProtectionPromptMessage", false)
                    && prompts[1] == ("TamperProtectionStillEnabledPromptMessage", true)
                    && gpUpdateCount == 1
                    && item.RegistryValues.All(v => v.IsOptimized),
                $"result={retrySuccess}, open={openSettingsCount}, prompts={prompts.Count}, gp={gpUpdateCount}"
            );

            // Case: Reverting (SetIsOptimized(false)) does not prompt to turn off Tamper Protection.
            openSettingsCount = 0;
            gpUpdateCount = 0;
            prompts.Clear();
            simulatedStatus = new DefenderTamperProtectionStatus(true, 5, true, "Normal", false);
            var revertResult = await item.SetIsOptimized(false);
            Check(
                "SetIsOptimized(false) reverts without prompting and refreshes blocked state",
                revertResult
                    && !item.IsOptimized
                    && item.IsPrerequisiteBlocked
                    && openSettingsCount == 0
                    && prompts.Count == 0
                    && gpUpdateCount == 1
                    && item.RegistryValues.All(v => !v.IsOptimized),
                $"result={revertResult}, blocked={item.IsPrerequisiteBlocked}, open={openSettingsCount}, prompts={prompts.Count}"
            );

            // Case: Direct batching guard refuses to apply if Tamper Protection is still ON.
            openSettingsCount = 0;
            gpUpdateCount = 0;
            prompts.Clear();
            simulatedStatus = new DefenderTamperProtectionStatus(true, 5, true, "Normal", false);
            OptimizationItem.InBatching = true;
            var batchGuardResult = await item.SetIsOptimized(true);
            OptimizationItem.InBatching = false;
            Check(
                "InBatching safety guard blocks IsOptimizedChanging(true) while Tamper ON",
                !batchGuardResult
                    && !item.IsOptimized
                    && item.IsPrerequisiteBlocked
                    && item.RegistryValues.All(v => !v.IsOptimized),
                $"result={batchGuardResult}, IsOptimized={item.IsOptimized}"
            );

            sb.AppendLine("4. Manual settings open + window reactivation recheck");
            foreach (var v in item.RegistryValues)
                v.IsOptimized = true;
            simulatedStatus = new DefenderTamperProtectionStatus(true, 5, true, "Normal", false);
            await item.Initialize();
            openSettingsCount = 0;

            await vm.OpenOptimizationPrerequisiteSettingsAsync(item);
            Check("OpenOptimizationPrerequisiteSettingsAsync launches Defender settings", openSettingsCount == 1);

            simulatedStatus = new DefenderTamperProtectionStatus(false, 4, false, "Normal", false);
            await vm.RecheckPendingPrerequisiteItemAsync();
            Check(
                "RecheckPendingPrerequisiteItemAsync clears warning and restores IsOptimized once Tamper OFF",
                item.IsOptimized && !item.IsPrerequisiteBlocked,
                $"IsOptimized={item.IsOptimized}, IsPrerequisiteBlocked={item.IsPrerequisiteBlocked}"
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
        var liveItem = vm.AntivirusGroups
            .SelectMany(group => group.Items)
            .FirstOrDefault(item => item.NameKey == DisableWindowsRealTimeAntivirusItem.ItemNameKey);
        Check(
            "Antivirus tab contains DisableWindowsRealTimeAntivirusItem",
            liveItem is DisableWindowsRealTimeAntivirusItem
        );
        if (liveItem is DisableWindowsRealTimeAntivirusItem defenderItem)
        {
            var liveStatus = await DefenderProtection.GetTamperProtectionStatus();
            Check(
                "Live item flags ShouldTurnOffTamperProtection and ShouldUpdateGroupPolicy",
                defenderItem.ShouldTurnOffTamperProtection && defenderItem.ShouldUpdateGroupPolicy
            );
            Check(
                "Live item IsPrerequisiteBlocked matches DefenderProtection.IsBlockingRealTimeDisable",
                defenderItem.IsPrerequisiteBlocked == liveStatus.IsBlockingRealTimeDisable,
                $"itemBlocked={defenderItem.IsPrerequisiteBlocked}, liveBlocking={liveStatus.IsBlockingRealTimeDisable}"
            );
            Check(
                "Live item is never IsOptimized when Tamper Protection is blocking",
                !liveStatus.IsBlockingRealTimeDisable || !defenderItem.IsOptimized,
                $"itemOptimized={defenderItem.IsOptimized}, liveBlocking={liveStatus.IsBlockingRealTimeDisable}"
            );
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
