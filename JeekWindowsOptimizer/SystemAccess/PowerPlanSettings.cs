using System.ComponentModel;
using System.Runtime.InteropServices;

namespace JeekWindowsOptimizer;

public readonly record struct PowerPlanSettingSpec(
    string Name,
    Guid SubgroupGuid,
    Guid SettingGuid,
    uint OptimizedValue,
    uint FallbackDefaultValue
);

public readonly record struct PowerPlanSettingReadout(
    string Name,
    Guid SubgroupGuid,
    Guid SettingGuid,
    bool Exists,
    uint? CurrentAcValue,
    uint? DefaultAcValue,
    uint OptimizedValue,
    uint RevertValue,
    bool IsOptimized
);

/// <summary>
/// Reads and writes AC power-plan settings directly via powrprof.dll so hidden
/// hardware power-saving settings (ASPM, USB LPM, AHCI, NVMe, wireless, disk idle)
/// can be toggled and immediately applied to active drivers.
/// </summary>
public static class PowerPlanSettings
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorFileNotFound = 2;

    public static readonly Guid SubgroupPciExpress = new("501a4d13-42af-4429-9fd1-a8218c268e20");
    public static readonly Guid SubgroupUsb = new("2a737441-1930-4402-8d77-b2bebba308a3");
    public static readonly Guid SubgroupDisk = new("0012ee47-9041-4b5d-9b77-535fba8b1442");
    public static readonly Guid SubgroupWireless = new("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");

    public static readonly PowerPlanSettingSpec PcieAspm = new(
        "PCIe ASPM",
        SubgroupPciExpress,
        new("ee12f906-d277-404b-b6da-e5fa1a576df5"),
        OptimizedValue: 0,
        FallbackDefaultValue: 1
    );

    public static readonly PowerPlanSettingSpec UsbSelectiveSuspend = new(
        "USB Selective Suspend",
        SubgroupUsb,
        new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226"),
        OptimizedValue: 0,
        FallbackDefaultValue: 1
    );

    public static readonly PowerPlanSettingSpec Usb3LinkPowerManagement = new(
        "USB 3 Link Power Management",
        SubgroupUsb,
        new("d4e98f31-5ffe-4ce1-be31-1b38b384c009"),
        OptimizedValue: 0,
        FallbackDefaultValue: 2
    );

    public static readonly PowerPlanSettingSpec DiskIdleTimeout = new(
        "Hard Disk Idle Timeout",
        SubgroupDisk,
        new("6738e2c4-e8a5-4a42-b16a-e040e769756e"),
        OptimizedValue: 0,
        FallbackDefaultValue: 1200
    );

    public static readonly PowerPlanSettingSpec AhciLinkPowerManagement = new(
        "AHCI Link Power Management (HIPM/DIPM)",
        SubgroupDisk,
        new("0b2d69d7-a2a1-449c-9680-f91c70521c60"),
        OptimizedValue: 0,
        FallbackDefaultValue: 1
    );

    public static readonly PowerPlanSettingSpec NvmeNoppme = new(
        "NVMe NOPPME",
        SubgroupDisk,
        new("fc7372b6-ab2d-43ee-8797-15e9841f2cca"),
        OptimizedValue: 0,
        FallbackDefaultValue: 1
    );

    public static readonly PowerPlanSettingSpec PrimaryNvmeLatencyTolerance = new(
        "Primary NVMe Transition Latency Tolerance",
        SubgroupDisk,
        new("fc95af4d-40e7-4b6d-835a-56d131dbc80e"),
        OptimizedValue: 0,
        FallbackDefaultValue: 15
    );

    public static readonly PowerPlanSettingSpec SecondaryNvmeLatencyTolerance = new(
        "Secondary NVMe Transition Latency Tolerance",
        SubgroupDisk,
        new("dbc9e238-6de9-49e3-92cd-8c2b4946b472"),
        OptimizedValue: 0,
        FallbackDefaultValue: 100
    );

    public static readonly PowerPlanSettingSpec WirelessPowerSaving = new(
        "Wireless Adapter Power Saving Mode",
        SubgroupWireless,
        new("12bbebe6-58d6-4636-95bb-3217ef867c1a"),
        OptimizedValue: 0,
        FallbackDefaultValue: 1
    );

    public static bool IsOnAcPower()
    {
        if (!GetSystemPowerStatus(out var status))
            return true;

        // 0 = Offline (DC battery), 1 = Online (AC), 255 = Unknown (treat as AC).
        return status.ACLineStatus != 0;
    }

    public static Guid GetActiveScheme()
    {
        var result = PowerGetActiveScheme(IntPtr.Zero, out var schemePtr);
        if (result != ErrorSuccess)
            throw new Win32Exception((int)result);

        try
        {
            return Marshal.PtrToStructure<Guid>(schemePtr);
        }
        finally
        {
            LocalFree(schemePtr);
        }
    }

    public static void ApplyScheme(Guid scheme)
    {
        var result = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (result != ErrorSuccess)
            throw new Win32Exception((int)result);
    }

    public static bool TryReadAcValue(Guid scheme, Guid subgroup, Guid setting, out uint value)
    {
        var result = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value);
        if (result == ErrorSuccess)
            return true;
        if (result == ErrorFileNotFound)
        {
            value = 0;
            return false;
        }

        throw new Win32Exception((int)result);
    }

    public static bool TryReadAcDefaultValue(Guid scheme, Guid subgroup, Guid setting, out uint value)
    {
        var result = PowerReadACDefaultIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value);
        if (result == ErrorSuccess)
            return true;

        value = 0;
        return false;
    }

    public static uint ResolveRevertValue(Guid scheme, PowerPlanSettingSpec spec)
    {
        if (
            TryReadAcDefaultValue(scheme, spec.SubgroupGuid, spec.SettingGuid, out var defaultValue)
            && defaultValue != spec.OptimizedValue
        )
        {
            return defaultValue;
        }

        return spec.FallbackDefaultValue;
    }

    public static void WriteAcValue(Guid scheme, Guid subgroup, Guid setting, uint value)
    {
        var result = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value);
        if (result != ErrorSuccess)
            throw new Win32Exception((int)result);
    }

    public static bool AreAllOptimized(IReadOnlyList<PowerPlanSettingSpec> specs)
    {
        var scheme = GetActiveScheme();
        var foundAny = false;

        foreach (var spec in specs)
        {
            if (!TryReadAcValue(scheme, spec.SubgroupGuid, spec.SettingGuid, out var current))
                continue;

            foundAny = true;
            if (current != spec.OptimizedValue)
                return false;
        }

        return foundAny;
    }

    public static bool Apply(IReadOnlyList<PowerPlanSettingSpec> specs, bool optimize)
    {
        var scheme = GetActiveScheme();
        var updatedAny = false;

        foreach (var spec in specs)
        {
            if (!TryReadAcValue(scheme, spec.SubgroupGuid, spec.SettingGuid, out _))
                continue;

            var target = optimize ? spec.OptimizedValue : ResolveRevertValue(scheme, spec);
            WriteAcValue(scheme, spec.SubgroupGuid, spec.SettingGuid, target);
            updatedAny = true;
        }

        if (!updatedAny)
            return false;

        // Re-apply the active scheme so drivers receive the updated power setting immediately.
        ApplyScheme(scheme);
        return true;
    }

    public static IReadOnlyList<PowerPlanSettingReadout> ReadStatus(
        IReadOnlyList<PowerPlanSettingSpec> specs
    )
    {
        var scheme = GetActiveScheme();
        var list = new List<PowerPlanSettingReadout>(specs.Count);

        foreach (var spec in specs)
        {
            var exists = TryReadAcValue(scheme, spec.SubgroupGuid, spec.SettingGuid, out var current);
            uint? defaultValue = TryReadAcDefaultValue(
                scheme,
                spec.SubgroupGuid,
                spec.SettingGuid,
                out var def
            )
                ? def
                : null;
            var revert = ResolveRevertValue(scheme, spec);
            list.Add(
                new PowerPlanSettingReadout(
                    spec.Name,
                    spec.SubgroupGuid,
                    spec.SettingGuid,
                    exists,
                    exists ? current : null,
                    defaultValue,
                    spec.OptimizedValue,
                    revert,
                    exists && current == spec.OptimizedValue
                )
            );
        }

        return list;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(
        [In, Optional] IntPtr rootPowerKey,
        [Out] out IntPtr activePolicyGuid
    );

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(
        [In, Optional] IntPtr rootPowerKey,
        [In] ref Guid activePolicyGuid
    );

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(
        [In, Optional] IntPtr rootPowerKey,
        [In] ref Guid schemeGuid,
        [In] ref Guid subGroupOfPowerSettingsGuid,
        [In] ref Guid powerSettingGuid,
        [Out] out uint acValueIndex
    );

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACDefaultIndex(
        [In, Optional] IntPtr rootPowerKey,
        [In] ref Guid schemeGuid,
        [In] ref Guid subGroupOfPowerSettingsGuid,
        [In] ref Guid powerSettingGuid,
        [Out] out uint acDefaultIndex
    );

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(
        [In, Optional] IntPtr rootPowerKey,
        [In] ref Guid schemeGuid,
        [In] ref Guid subGroupOfPowerSettingsGuid,
        [In] ref Guid powerSettingGuid,
        [In] uint acValueIndex
    );

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree([In] IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}
