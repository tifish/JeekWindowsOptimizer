using System.Diagnostics;
using System.Management;
using DotNetRun;
using Microsoft.Win32;

namespace JeekWindowsOptimizer;

public readonly record struct WindowsTimeSynchronizationStatus(
    bool ServiceExists,
    string ServiceStartMode,
    int TriggerCount,
    string Type,
    int NtpClientEnabled,
    bool IsEnabled
);

/// <summary>
/// Enables or disables Windows automatic time synchronization
/// (W32Time + NTP client). Enabling forces Automatic start; disabling
/// restores the Windows default Manual (trigger start) and Type=NoSync.
/// </summary>
public static class WindowsTimeSynchronization
{
    public const string ServiceName = "W32Time";

    private const string TypeNoSync = "NoSync";
    private const string TypeNtp = "NTP";
    private const string TypeNt5Ds = "NT5DS";
    private const string TypeAllSync = "AllSync";
    private const string TriggerInfoPath =
        @"SYSTEM\CurrentControlSet\Services\W32Time\TriggerInfo";

    private static readonly RegistryValue TypeValue = new(
        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\W32Time\Parameters",
        "Type"
    );

    private static readonly RegistryValue NtpClientEnabledValue = new(
        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\W32Time\TimeProviders\NtpClient",
        "Enabled"
    );

    public static WindowsTimeSynchronizationStatus GetStatus()
    {
        using var service = new WindowsService(ServiceName);
        var exists = service.Exists();
        var startMode = "Missing";
        if (exists)
        {
            try
            {
                startMode = service.GetStartMode().ToString();
            }
            catch
            {
                startMode = "Unknown";
            }
        }

        var type = TypeValue.GetValue("") ?? "";
        var ntpClientEnabled = NtpClientEnabledValue.GetValue(1);
        var isEnabled =
            exists
            && string.Equals(startMode, nameof(WindowsService.StartMode.Automatic), StringComparison.Ordinal)
            && IsSyncType(type)
            && ntpClientEnabled != 0;

        return new WindowsTimeSynchronizationStatus(
            exists,
            startMode,
            CountTriggers(),
            type,
            ntpClientEnabled,
            isEnabled
        );
    }

    public static bool SetEnabled(bool enabled)
    {
        using var service = new WindowsService(ServiceName);
        if (!service.Exists())
            return false;

        if (enabled)
        {
            if (service.GetStartMode() != WindowsService.StartMode.Automatic)
                service.SetStartMode(WindowsService.StartMode.Automatic);

            var type = TypeValue.GetValue("") ?? "";
            if (!IsSyncType(type))
                TypeValue.SetValue(IsDomainJoined() ? TypeNt5Ds : TypeNtp);

            if (NtpClientEnabledValue.GetValue(1) == 0)
                NtpClientEnabledValue.SetValue(1);

            service.Start();
            RunW32Time("/config /update");
            RunW32Time("/resync /force /nowait");
            return GetStatus().IsEnabled;
        }

        TypeValue.SetValue(TypeNoSync);
        if (service.GetStartMode() != WindowsService.StartMode.Manual)
            service.SetStartMode(WindowsService.StartMode.Manual);

        var status = GetStatus();
        return !status.IsEnabled
            && string.Equals(status.ServiceStartMode, nameof(WindowsService.StartMode.Manual), StringComparison.Ordinal);
    }

    private static bool IsSyncType(string type)
    {
        if (string.IsNullOrEmpty(type))
            return true;

        return type.Equals(TypeNtp, StringComparison.OrdinalIgnoreCase)
            || type.Equals(TypeNt5Ds, StringComparison.OrdinalIgnoreCase)
            || type.Equals(TypeAllSync, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountTriggers()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(TriggerInfoPath);
            return key?.GetSubKeyNames().Length ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsDomainJoined()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT PartOfDomain FROM Win32_ComputerSystem"
            );
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    return obj["PartOfDomain"] is true;
                }
            }
        }
        catch
        {
            // Workgroup default: NTP.
        }

        return false;
    }

    private static void RunW32Time(string arguments)
    {
        try
        {
            using var process = Process.Start(
                new ProcessStartInfo("w32tm.exe", arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            );
            process?.WaitForExit(15_000);
        }
        catch
        {
            // Best-effort: registry and service state are what Initialize checks.
        }
    }
}
