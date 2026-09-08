using System.Management;
using System.Globalization;
using System.Text.RegularExpressions;

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

    internal static long? ParseUsedBytes(string output)
    {
        // /for restricts output to one association: used, allocated, maximum, in that order.
        var match = Regex.Match(output, @"[:：]\s*([\d.,]+)\s*(KB|MB|GB|TB|B)\b", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var number = match.Groups[1].Value;
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            && !double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return null;
        var power = match.Groups[2].Value.ToUpperInvariant() switch { "KB" => 1, "MB" => 2, "GB" => 3, "TB" => 4, _ => 0 };
        return checked((long)(value * Math.Pow(1024, power)));
    }

    internal static async Task<long> UsedBytes(CancellationToken token)
    {
        // Keep the native diagnostic output available; WMI supplies exact bytes independently of display language/rounding.

        var volume = VolumeName();
        long total = 0;
        using var search = new ManagementObjectSearcher("SELECT Volume, DiffVolume, UsedSpace FROM Win32_ShadowStorage");
        using var values = search.Get();
        foreach (ManagementObject value in values)
        {
            using var source = new ManagementObject((string)value["Volume"]);
            using var destination = new ManagementObject((string)value["DiffVolume"]);
            if (string.Equals((string)source["DeviceID"], volume, StringComparison.OrdinalIgnoreCase)
                && string.Equals((string)destination["DeviceID"], volume, StringComparison.OrdinalIgnoreCase))
                total += checked((long)(ulong)value["UsedSpace"]);
        }
        if (total > 0)
            return ParseUsedBytes(await CleanupCommand.Run("vssadmin.exe", "list shadowstorage /for=" + DiskSpaceItemManager.SystemDriveLetter + ":", token)) ?? total;
        return total;
    }
}
