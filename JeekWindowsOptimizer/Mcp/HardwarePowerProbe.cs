using System.Text;
using Microsoft.Win32;

namespace JeekWindowsOptimizer.Mcp;

internal static class HardwarePowerProbe
{
    private const string SyntheticProbeKeyPrefix =
        @"Software\JeekWindowsOptimizer\DebugProbe\EthernetPower";

    public static async Task<string> DescribeStatusAsync(MainViewModel vm)
    {
        var sb = new StringBuilder();

        var (scheme, ethernetAdapters) = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () => (
                PowerPlanSettings.GetActiveScheme(),
                EthernetPowerManagement.GetPhysicalWiredAdapterStatuses()
            )
        );

        sb.AppendLine($"activePowerScheme={scheme}");
        sb.AppendLine();

        var powerItems = FindHardwarePowerItems(vm);
        sb.AppendLine($"registeredItems={powerItems.Count}");
        foreach (var (category, item, path) in powerItems)
        {
            sb.AppendLine(
                $"[{category}] {item.NameKey}: optimized={item.IsOptimized} checked={item.IsChecked} "
                    + $"shouldReboot={item.ShouldReboot} path={path}"
            );

            if (item is PowerPlanSettingItem powerItem)
            {
                var readouts = await OptimizationExecutionScheduler.RunAsync(
                    OptimizationExecutionAffinity.Background,
                    () => PowerPlanSettings.ReadStatus(powerItem.Specs)
                );
                foreach (var r in readouts)
                {
                    sb.AppendLine(
                        $"  setting \"{r.Name}\" ({r.SettingGuid}): exists={r.Exists} "
                            + $"current={r.CurrentAcValue?.ToString() ?? "n/a"} "
                            + $"default={r.DefaultAcValue?.ToString() ?? "n/a"} "
                            + $"optimized={r.OptimizedValue} revert={r.RevertValue} "
                            + $"isOptimized={r.IsOptimized}"
                    );
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine($"physicalWiredEthernetAdapters={ethernetAdapters.Count}");
        foreach (var adapter in ethernetAdapters)
        {
            sb.AppendLine(
                $"  [{adapter.SubKeyName}] {adapter.DriverDesc}: "
                    + $"PnPCapabilities={adapter.PnPCapabilities?.ToString() ?? "unset"} "
                    + $"turnOffDisabled={adapter.IsTurnOffDisabled} optimized={adapter.IsOptimized}"
            );
            foreach (var param in adapter.PowerSavingParams)
            {
                sb.AppendLine(
                    $"    param {param.Keyword}: current={param.CurrentValue} "
                        + $"revert={param.RevertValue} optimized={param.IsOptimized}"
                );
            }
        }

        return sb.ToString();
    }

    public static async Task<string> RunProbeAsync(MainViewModel vm, bool includeLivePowerPlanToggle)
    {
        var sb = new StringBuilder();
        var failures = new List<string>();

        await RunSyntheticEthernetProbeAsync(sb, failures);

        if (includeLivePowerPlanToggle)
            await RunLivePowerPlanToggleProbeAsync(vm, sb, failures);

        sb.AppendLine();
        sb.AppendLine(
            failures.Count == 0
                ? "RESULT: PASS"
                : $"RESULT: FAIL ({failures.Count} failure(s): {string.Join("; ", failures)})"
        );
        return sb.ToString();
    }

    private static Task RunSyntheticEthernetProbeAsync(StringBuilder sb, List<string> failures)
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () =>
            {
                var probePath = $@"{SyntheticProbeKeyPrefix}\{Guid.NewGuid():N}";
                using var hkcu = RegistryKey.OpenBaseKey(
                    RegistryHive.CurrentUser,
                    RegistryView.Registry64
                );

                try
                {
                    // 0000: Physical wired Intel NIC with *EEE, ULPMode, DMACoalescing (and an unrelated WOL param).
                    CreateSyntheticAdapter(
                        hkcu,
                        $@"{probePath}\0000",
                        driverDesc: "Synthetic Intel 10GbE",
                        characteristics: 0x4,
                        ifType: 6,
                        physicalMedia: 14,
                        pnpCapabilities: null,
                        ndiParams:
                        [
                            ("*EEE", "1", "1", true),
                            ("ULPMode", "1", "1", true),
                            ("DMACoalescing", "1000", "1000", true),
                            ("*WakeOnMagicPacket", "1", "1", true),
                        ]
                    );

                    // 0001: Physical wired Realtek NIC with existing PnPCapabilities bit 0x100 (magic packet only)
                    // and Realtek power-saving params.
                    CreateSyntheticAdapter(
                        hkcu,
                        $@"{probePath}\0001",
                        driverDesc: "Synthetic Realtek 2.5GbE",
                        characteristics: 0x4,
                        ifType: 6,
                        physicalMedia: 14,
                        pnpCapabilities: 0x100,
                        ndiParams:
                        [
                            ("AdvancedEEE", "1", "1", true),
                            ("EnableGreenEthernet", "1", "1", true),
                            ("GigaLite", "1", "1", true),
                            ("PowerSavingMode", "1", "1", true),
                        ]
                    );

                    // 0002: Physical Wi-Fi adapter — MUST be ignored.
                    CreateSyntheticAdapter(
                        hkcu,
                        $@"{probePath}\0002",
                        driverDesc: "Synthetic Wi-Fi 6E",
                        characteristics: 0x4,
                        ifType: 6,
                        physicalMedia: 9,
                        pnpCapabilities: null,
                        ndiParams: [("*EEE", "1", "1", true)]
                    );

                    // 0003: Virtual Ethernet adapter — MUST be ignored.
                    CreateSyntheticAdapter(
                        hkcu,
                        $@"{probePath}\0003",
                        driverDesc: "Synthetic Hyper-V Virtual Switch",
                        characteristics: 0x1 | 0x4,
                        ifType: 6,
                        physicalMedia: 14,
                        pnpCapabilities: null,
                        ndiParams: [("*EEE", "1", "1", true)]
                    );

                    var initial = EthernetPowerManagement.GetPhysicalWiredAdapterStatuses(
                        RegistryHive.CurrentUser,
                        probePath
                    );
                    Check(initial.Count == 2, $"expected 2 wired adapters, got {initial.Count}", failures);
                    Check(
                        !EthernetPowerManagement.AreAllOptimized(RegistryHive.CurrentUser, probePath),
                        "synthetic adapters should start unoptimized",
                        failures
                    );

                    Check(
                        EthernetPowerManagement.Apply(true, RegistryHive.CurrentUser, probePath),
                        "Apply(true) returned false",
                        failures
                    );
                    Check(
                        EthernetPowerManagement.AreAllOptimized(RegistryHive.CurrentUser, probePath),
                        "synthetic adapters should be optimized after Apply(true)",
                        failures
                    );

                    var afterOptimize = EthernetPowerManagement.GetPhysicalWiredAdapterStatuses(
                        RegistryHive.CurrentUser,
                        probePath
                    );
                    var realtekAfter = afterOptimize.FirstOrDefault(a => a.SubKeyName == "0001");
                    Check(
                        realtekAfter.PnPCapabilities == (0x100 | EthernetPowerManagement.DisableTurnOffToSavePowerBit),
                        $"Realtek PnPCapabilities should preserve 0x100 bit, got {realtekAfter.PnPCapabilities}",
                        failures
                    );

                    // Verify unrelated param and ignored adapters were untouched.
                    using (var intelKey = hkcu.OpenSubKey($@"{probePath}\0000"))
                        Check(
                            intelKey?.GetValue("*WakeOnMagicPacket")?.ToString() == "1",
                            "WakeOnMagicPacket must not be modified",
                            failures
                        );
                    using (var wifiKey = hkcu.OpenSubKey($@"{probePath}\0002"))
                        Check(
                            wifiKey?.GetValue("PnPCapabilities") is null
                                && wifiKey?.GetValue("*EEE")?.ToString() == "1",
                            "Wi-Fi adapter must not be modified",
                            failures
                        );
                    using (var virtKey = hkcu.OpenSubKey($@"{probePath}\0003"))
                        Check(
                            virtKey?.GetValue("PnPCapabilities") is null
                                && virtKey?.GetValue("*EEE")?.ToString() == "1",
                            "Virtual adapter must not be modified",
                            failures
                        );

                    // Revert and check exact restoration.
                    Check(
                        EthernetPowerManagement.Apply(false, RegistryHive.CurrentUser, probePath),
                        "Apply(false) returned false",
                        failures
                    );
                    Check(
                        !EthernetPowerManagement.AreAllOptimized(RegistryHive.CurrentUser, probePath),
                        "synthetic adapters should not be optimized after Apply(false)",
                        failures
                    );

                    var afterRevert = EthernetPowerManagement.GetPhysicalWiredAdapterStatuses(
                        RegistryHive.CurrentUser,
                        probePath
                    );
                    var intelReverted = afterRevert.FirstOrDefault(a => a.SubKeyName == "0000");
                    var realtekReverted = afterRevert.FirstOrDefault(a => a.SubKeyName == "0001");
                    Check(
                        intelReverted.PnPCapabilities is null,
                        $"Intel PnPCapabilities should be removed on revert, got {intelReverted.PnPCapabilities}",
                        failures
                    );
                    Check(
                        realtekReverted.PnPCapabilities == 0x100,
                        $"Realtek PnPCapabilities should restore 0x100 on revert, got {realtekReverted.PnPCapabilities}",
                        failures
                    );
                    Check(
                        intelReverted.PowerSavingParams.All(p => p.CurrentValue == p.RevertValue)
                            && realtekReverted.PowerSavingParams.All(p => p.CurrentValue == p.RevertValue),
                        "all NDI power-saving params should restore their default values on revert",
                        failures
                    );

                    sb.AppendLine("synthetic_ethernet_probe: checked optimize, bit preservation, Wi-Fi/virtual exclusion, and revert.");
                }
                finally
                {
                    hkcu.DeleteSubKeyTree(probePath, throwOnMissingSubKey: false);
                }
            }
        );
    }

    private static async Task RunLivePowerPlanToggleProbeAsync(
        MainViewModel vm,
        StringBuilder sb,
        List<string> failures
    )
    {
        var powerItems = FindHardwarePowerItems(vm)
            .Select(entry => entry.Item)
            .OfType<PowerPlanSettingItem>()
            .ToList();

        var scheme = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            PowerPlanSettings.GetActiveScheme
        );

        var snapshots = new List<(PowerPlanSettingSpec Spec, uint OriginalValue)>();
        await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () =>
            {
                foreach (var spec in powerItems.SelectMany(i => i.Specs))
                {
                    if (
                        PowerPlanSettings.TryReadAcValue(
                            scheme,
                            spec.SubgroupGuid,
                            spec.SettingGuid,
                            out var current
                        )
                    )
                    {
                        snapshots.Add((spec, current));
                    }
                }
            }
        );

        try
        {
            foreach (var item in powerItems)
            {
                var initialState = item.IsOptimized;

                // Toggle to opposite state, verify live Windows power setting + item state, then toggle back.
                var firstTarget = !initialState;
                var firstOk = await item.SetIsOptimized(firstTarget);
                var liveAfterFirst = await OptimizationExecutionScheduler.RunAsync(
                    OptimizationExecutionAffinity.Background,
                    () => PowerPlanSettings.AreAllOptimized(item.Specs)
                );
                Check(
                    firstOk && item.IsOptimized == firstTarget && liveAfterFirst == firstTarget,
                    $"{item.NameKey} toggle {initialState}->{firstTarget} failed (ok={firstOk}, item={item.IsOptimized}, live={liveAfterFirst})",
                    failures
                );

                var secondOk = await item.SetIsOptimized(initialState);
                var liveAfterSecond = await OptimizationExecutionScheduler.RunAsync(
                    OptimizationExecutionAffinity.Background,
                    () => PowerPlanSettings.AreAllOptimized(item.Specs)
                );
                Check(
                    secondOk && item.IsOptimized == initialState && liveAfterSecond == initialState,
                    $"{item.NameKey} toggle {firstTarget}->{initialState} failed (ok={secondOk}, item={item.IsOptimized}, live={liveAfterSecond})",
                    failures
                );

                sb.AppendLine(
                    $"live_power_item {item.NameKey}: {initialState} -> {firstTarget} -> {initialState} ok"
                );
            }
        }
        finally
        {
            await OptimizationExecutionScheduler.RunAsync(
                OptimizationExecutionAffinity.ExclusiveBackground,
                () =>
                {
                    foreach (var (spec, originalValue) in snapshots)
                    {
                        PowerPlanSettings.WriteAcValue(
                            scheme,
                            spec.SubgroupGuid,
                            spec.SettingGuid,
                            originalValue
                        );
                    }
                    PowerPlanSettings.ApplyScheme(scheme);
                }
            );

            foreach (var item in powerItems)
                await item.Initialize();
        }
    }

    private static void CreateSyntheticAdapter(
        RegistryKey hkcu,
        string path,
        string driverDesc,
        int characteristics,
        int ifType,
        int physicalMedia,
        int? pnpCapabilities,
        (string Keyword, string Current, string Default, bool HasZeroEnum)[] ndiParams
    )
    {
        using var adapterKey = hkcu.CreateSubKey(path);
        adapterKey.SetValue("DriverDesc", driverDesc, RegistryValueKind.String);
        adapterKey.SetValue("Characteristics", characteristics, RegistryValueKind.DWord);
        adapterKey.SetValue("*IfType", ifType, RegistryValueKind.DWord);
        adapterKey.SetValue("*PhysicalMediaType", physicalMedia, RegistryValueKind.DWord);
        if (pnpCapabilities is { } pnp)
            adapterKey.SetValue("PnPCapabilities", pnp, RegistryValueKind.DWord);

        foreach (var (keyword, current, def, hasZeroEnum) in ndiParams)
        {
            adapterKey.SetValue(keyword, current, RegistryValueKind.String);
            using var paramKey = adapterKey.CreateSubKey($@"Ndi\params\{keyword}");
            paramKey.SetValue("default", def, RegistryValueKind.String);
            if (hasZeroEnum)
            {
                using var enumKey = paramKey.CreateSubKey("enum");
                enumKey.SetValue("0", "Disabled", RegistryValueKind.String);
                enumKey.SetValue("1", "Enabled", RegistryValueKind.String);
            }
        }
    }

    private static List<(string Category, OptimizationItem Item, string Path)> FindHardwarePowerItems(
        MainViewModel vm
    )
    {
        var results = new List<(string, OptimizationItem, string)>();
        var categories = new (string Name, string CollectionProp, List<OptimizationGroup> Groups)[]
        {
            ("Optimizing", "OptimizingGroups", vm.OptimizingGroups),
            ("Personal", "PersonalGroups", vm.PersonalGroups),
        };

        foreach (var (category, prop, groups) in categories)
        {
            for (var gi = 0; gi < groups.Count; gi++)
            {
                var group = groups[gi];
                for (var ii = 0; ii < group.Items.Count; ii++)
                {
                    var item = group.Items[ii];
                    if (item is PowerPlanSettingItem or DisableEthernetPowerSavingItem)
                        results.Add((category, item, $"MainVm.{prop}[{gi}].Items[{ii}]"));
                }
            }
        }

        return results;
    }

    private static void Check(bool condition, string message, List<string> failures)
    {
        if (!condition)
            failures.Add(message);
    }
}
