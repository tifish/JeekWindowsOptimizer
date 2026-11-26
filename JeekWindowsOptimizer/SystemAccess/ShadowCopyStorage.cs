using System.Management;

namespace JeekWindowsOptimizer;

internal sealed class ShadowCopyStorage
{
    internal record Snapshot(string Id, DateTime Created);
    internal static List<Snapshot> KeepNewest(IEnumerable<Snapshot> snapshots) =>
        snapshots.OrderByDescending(s => s.Created).ThenBy(s => s.Id, StringComparer.Ordinal).Skip(1).ToList();

    private static string VolumeName()
    {
        using var search = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_Volume WHERE DriveLetter='" + DiskSpaceItemManager.SystemDriveLetter + ":'");
        using var values = search.Get();
        return values.Cast<ManagementObject>().Select(v => (string)v["DeviceID"]).Single();
    }

    internal static List<Snapshot> List()
    {
        var volume = VolumeName();
        using var search = new ManagementObjectSearcher("SELECT ID, InstallDate, VolumeName, ClientAccessible FROM Win32_ShadowCopy");
        using var values = search.Get();
        return values.Cast<ManagementObject>()
            .Where(v => string.Equals((string)v["VolumeName"], volume, StringComparison.OrdinalIgnoreCase)
                && v["ClientAccessible"] is true)
            .Select(v => new Snapshot(Guid.Parse((string)v["ID"]).ToString("B"), ManagementDateTimeConverter.ToDateTime((string)v["InstallDate"]))).ToList();
    }

    internal static long UsedBytes(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var volume = VolumeName();
        long total = 0;
        using var search = new ManagementObjectSearcher("SELECT Volume, DiffVolume, UsedSpace FROM Win32_ShadowStorage");
        using var values = search.Get();
        foreach (ManagementObject value in values)
        {
            token.ThrowIfCancellationRequested();
            using var source = new ManagementObject((string)value["Volume"]);
            using var destination = new ManagementObject((string)value["DiffVolume"]);
            if (string.Equals((string)source["DeviceID"], volume, StringComparison.OrdinalIgnoreCase)
                && string.Equals((string)destination["DeviceID"], volume, StringComparison.OrdinalIgnoreCase))
                total += checked((long)(ulong)value["UsedSpace"]);
        }
        return total;
    }
}
