using Microsoft.Win32;

namespace JeekWindowsOptimizer;

public readonly record struct EthernetPowerParamStatus(
    string Keyword,
    string CurrentValue,
    string RevertValue,
    bool IsOptimized
);

public readonly record struct EthernetAdapterPowerStatus(
    string SubKeyName,
    string DriverDesc,
    int? PnPCapabilities,
    bool IsTurnOffDisabled,
    IReadOnlyList<EthernetPowerParamStatus> PowerSavingParams,
    bool IsOptimized
);

/// <summary>
/// Inspects and disables driver-level power saving (PnPCapabilities turn-off bit and
/// EEE / Green Ethernet / ULP / GigaLite advanced properties) on physical wired Ethernet adapters.
/// </summary>
public static class EthernetPowerManagement
{
    public const string DefaultNetworkAdapterClassPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    private const int NcfVirtual = 0x1;
    private const int NcfPhysical = 0x4;
    private const int NcfHidden = 0x8;
    private const int IfTypeEthernetCsmacd = 6;
    private const int PhysicalMediaWirelessLan = 1;
    private const int PhysicalMediaNative80211 = 9;

    // Bit 4 (0x10) of PnPCapabilities disables "Allow the computer to turn off this device to save power".
    public const int DisableTurnOffToSavePowerBit = 0x10;

    private static readonly HashSet<string> PowerSavingParamKeywords = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "*EEE",
        "EEELinkAdvertisement",
        "AdvancedEEE",
        "EnableGreenEthernet",
        "GigaLite",
        "PowerSavingMode",
        "ULPMode",
        "ReduceSpeedOnPowerDown",
        "AutoDisableGigabit",
        "EnableDynamicPowerGating",
        "EnableSavePowerNow",
        "DMACoalescing",
    };

    public static bool HasPhysicalWiredAdapters(
        RegistryHive hive = RegistryHive.LocalMachine,
        string classKeyPath = DefaultNetworkAdapterClassPath
    )
    {
        return GetPhysicalWiredAdapterStatuses(hive, classKeyPath).Count > 0;
    }

    public static bool AreAllOptimized(
        RegistryHive hive = RegistryHive.LocalMachine,
        string classKeyPath = DefaultNetworkAdapterClassPath
    )
    {
        var adapters = GetPhysicalWiredAdapterStatuses(hive, classKeyPath);
        return adapters.Count > 0 && adapters.All(adapter => adapter.IsOptimized);
    }

    public static IReadOnlyList<EthernetAdapterPowerStatus> GetPhysicalWiredAdapterStatuses(
        RegistryHive hive = RegistryHive.LocalMachine,
        string classKeyPath = DefaultNetworkAdapterClassPath
    )
    {
        var results = new List<EthernetAdapterPowerStatus>();
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var classKey = baseKey.OpenSubKey(classKeyPath, writable: false);
        if (classKey is null)
            return results;

        foreach (var subKeyName in classKey.GetSubKeyNames())
        {
            if (!IsAdapterIndexName(subKeyName))
                continue;

            using var adapterKey = classKey.OpenSubKey(subKeyName, writable: false);
            if (adapterKey is null || !IsPhysicalWiredEthernet(adapterKey))
                continue;

            var driverDesc = adapterKey.GetValue("DriverDesc")?.ToString() ?? subKeyName;
            var pnpCapabilities = ReadNullableInt(adapterKey.GetValue("PnPCapabilities"));
            var isTurnOffDisabled =
                pnpCapabilities is { } flags && (flags & DisableTurnOffToSavePowerBit) != 0;

            var powerParams = ReadPowerSavingParams(adapterKey);
            var isOptimized = isTurnOffDisabled && powerParams.All(p => p.IsOptimized);

            results.Add(
                new EthernetAdapterPowerStatus(
                    subKeyName,
                    driverDesc,
                    pnpCapabilities,
                    isTurnOffDisabled,
                    powerParams,
                    isOptimized
                )
            );
        }

        return results;
    }

    public static bool Apply(
        bool optimize,
        RegistryHive hive = RegistryHive.LocalMachine,
        string classKeyPath = DefaultNetworkAdapterClassPath
    )
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var classKey = baseKey.OpenSubKey(classKeyPath, writable: true);
        if (classKey is null)
            return false;

        var touchedAny = false;

        foreach (var subKeyName in classKey.GetSubKeyNames())
        {
            if (!IsAdapterIndexName(subKeyName))
                continue;

            using var adapterKey = classKey.OpenSubKey(subKeyName, writable: true);
            if (adapterKey is null || !IsPhysicalWiredEthernet(adapterKey))
                continue;

            touchedAny = true;

            var currentPnp = ReadNullableInt(adapterKey.GetValue("PnPCapabilities")) ?? 0;
            if (optimize)
            {
                adapterKey.SetValue(
                    "PnPCapabilities",
                    currentPnp | DisableTurnOffToSavePowerBit,
                    RegistryValueKind.DWord
                );
            }
            else
            {
                var revertedPnp = currentPnp & ~DisableTurnOffToSavePowerBit;
                if (revertedPnp == 0)
                    adapterKey.DeleteValue("PnPCapabilities", throwOnMissingValue: false);
                else
                    adapterKey.SetValue("PnPCapabilities", revertedPnp, RegistryValueKind.DWord);
            }

            foreach (var param in ReadPowerSavingParams(adapterKey))
            {
                var target = optimize ? "0" : param.RevertValue;
                adapterKey.SetValue(param.Keyword, target, RegistryValueKind.String);
            }
        }

        return touchedAny;
    }

    private static bool IsAdapterIndexName(string name)
    {
        return name.Length == 4
            && char.IsAsciiDigit(name[0])
            && char.IsAsciiDigit(name[1])
            && char.IsAsciiDigit(name[2])
            && char.IsAsciiDigit(name[3]);
    }

    private static bool IsPhysicalWiredEthernet(RegistryKey adapterKey)
    {
        var characteristics = ReadNullableInt(adapterKey.GetValue("Characteristics"));
        if (characteristics is not { } flags)
            return false;
        if ((flags & NcfPhysical) == 0)
            return false;
        if ((flags & (NcfVirtual | NcfHidden)) != 0)
            return false;

        var ifType = ReadNullableInt(adapterKey.GetValue("*IfType"));
        if (ifType != IfTypeEthernetCsmacd)
            return false;

        var physicalMedia = ReadNullableInt(adapterKey.GetValue("*PhysicalMediaType"));
        if (physicalMedia is PhysicalMediaWirelessLan or PhysicalMediaNative80211)
            return false;

        return true;
    }

    private static List<EthernetPowerParamStatus> ReadPowerSavingParams(RegistryKey adapterKey)
    {
        var list = new List<EthernetPowerParamStatus>();
        using var paramsKey = adapterKey.OpenSubKey(@"Ndi\params", writable: false);
        if (paramsKey is null)
            return list;

        foreach (var paramName in paramsKey.GetSubKeyNames())
        {
            if (!PowerSavingParamKeywords.Contains(paramName))
                continue;

            using var paramKey = paramsKey.OpenSubKey(paramName, writable: false);
            using var enumKey = paramKey?.OpenSubKey("enum", writable: false);
            if (paramKey is null || enumKey is null)
                continue;

            // Only touch parameters whose driver explicitly defines "0" (Disabled) in its enum.
            if (enumKey.GetValue("0") is null)
                continue;

            var defaultValue = paramKey.GetValue("default")?.ToString();
            var revertValue = !string.IsNullOrWhiteSpace(defaultValue) ? defaultValue : "1";
            var currentValue = adapterKey.GetValue(paramName)?.ToString() ?? defaultValue ?? "";

            list.Add(
                new EthernetPowerParamStatus(
                    paramName,
                    currentValue,
                    revertValue,
                    string.Equals(currentValue, "0", StringComparison.Ordinal)
                )
            );
        }

        return list;
    }

    private static int? ReadNullableInt(object? value)
    {
        return value switch
        {
            int i => i,
            uint u => unchecked((int)u),
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null,
        };
    }
}
