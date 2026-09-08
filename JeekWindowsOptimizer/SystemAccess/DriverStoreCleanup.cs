using System.ComponentModel;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace JeekWindowsOptimizer;

internal static class DriverStoreCleanup
{
    internal record Package(string Published, string Original, string Provider, string ClassId, Version Version,
        string Directory = "", string Architecture = "");

    internal static List<Package> Parse(string output)
    {
        var result = new List<Package>();
        foreach (var block in Regex.Split(output.Trim(), @"\r?\n\s*\r?\n"))
        {
            var fields = block.Split('\n').Select(line => Regex.Match(line, @"^[^:：]+[:：]\s*(.*?)\s*$"))
                .Where(m => m.Success).Select(m => m.Groups[1].Value).ToArray();
            if (fields.Length == 0 || !Regex.IsMatch(fields[0], @"^oem\d+\.inf$", RegexOptions.IgnoreCase)) continue;
            if (fields.Length < 6 || !Regex.IsMatch(fields[1], @"^[^\\/:]+\.inf$", RegexOptions.IgnoreCase)
                || string.IsNullOrWhiteSpace(fields[2]) || !Guid.TryParse(fields[4], out var classId))
                throw new IOException("Unrecognized pnputil driver record: " + fields[0]);
            var version = fields.Skip(5).Select(field => Regex.Match(field, @"\s(\d+\.\d+\.\d+\.\d+)\s*$")).FirstOrDefault(match => match.Success);
            if (version is null || !Version.TryParse(version.Groups[1].Value, out var parsed))
                throw new IOException("Unrecognized driver version: " + fields[0]);
            result.Add(new(fields[0], fields[1], fields[2], classId.ToString(), parsed));
        }
        if (result.Count == 0 && Regex.IsMatch(output, @"oem\d+\.inf", RegexOptions.IgnoreCase))
            throw new IOException("Unable to parse pnputil output.");
        return result;
    }

    internal static List<Package> SelectOld(IEnumerable<Package> packages, ISet<string> inUse) => packages
        .GroupBy(p => string.Join("|", p.Original, p.Provider, p.ClassId, p.Architecture).ToUpperInvariant())
        .SelectMany(group => group.Where(p => p.Version < group.Max(x => x.Version) && !inUse.Contains(p.Published)))
        .ToList();

    internal static async Task<List<Package>> Candidates(CancellationToken token)
    {
        var packages = Parse(await CleanupCommand.Run("pnputil.exe", "/enum-drivers", token));
        var resolved = new List<Package>();
        foreach (var package in packages)
        {
            token.ThrowIfCancellationRequested();
            var buffer = new StringBuilder(32768);
            if (!SetupGetInfDriverStoreLocation(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", package.Published),
                IntPtr.Zero, null, buffer, buffer.Capacity, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var directory = Path.GetDirectoryName(buffer.ToString())!;
            var root = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository") + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase) || !FileSystemCleaner.IsPlainDirectoryPath(directory))
                throw new IOException("Unexpected driver store path.");
            var architecture = Regex.Match(Path.GetFileName(directory), @"_(amd64|arm64|x86|arm|ia64)_", RegexOptions.IgnoreCase);
            if (!architecture.Success) continue; // Do not compare packages for unknown architectures.
            resolved.Add(package with { Directory = directory, Architecture = architecture.Groups[1].Value });
        }
        var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var search = new ManagementObjectSearcher("SELECT InfName FROM Win32_PnPSignedDriver");
        using var values = search.Get();
        foreach (ManagementObject value in values)
            if (value["InfName"] is string inf) inUse.Add(inf);
        return SelectOld(resolved, inUse);
    }

    internal static string DeleteArguments(string published)
    {
        if (!Regex.IsMatch(published, @"^oem\d+\.inf$", RegexOptions.IgnoreCase)) throw new ArgumentException("Invalid published INF");
        // PnPUtil supplies the final in-use guard, including devices absent from the WMI inventory.
        return "/delete-driver " + published;
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupGetInfDriverStoreLocationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupGetInfDriverStoreLocation(string fileName, IntPtr alternatePlatformInfo,
        string? localeName, StringBuilder buffer, int bufferSize, out int requiredSize);
}
