using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

internal record WslDistribution(string Id, string Name, string Location, int Version, string DiskName);

internal static class WslStorage
{
    internal const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    internal static List<WslDistribution> Discover()
    {
        var result = new List<WslDistribution>();
        using var root = Registry.CurrentUser.OpenSubKey(RegistryPath);
        if (root is null) return result;
        foreach (var id in root.GetSubKeyNames())
        {
            if (!Guid.TryParse(id, out _)) continue;
            using var key = root.OpenSubKey(id);
            if (key?.GetValue("DistributionName") is not string name
                || key.GetValue("BasePath") is not string path) continue;
            result.Add(new(id, name, Normalize(path), Convert.ToInt32(key.GetValue("Version") ?? 0),
                key.GetValue("VhdFileName") as string ?? "ext4.vhdx"));
        }
        return result;
    }

    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path));

    internal static async Task<(int ExitCode, string Output)> RunAsync(string[] args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wsl.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Unicode, StandardErrorEncoding = Encoding.Unicode,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Unable to start WSL.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        // A started move must be allowed to finish. Killing the client can leave the service moving data.
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, ((await stdout) + "\n" + (await stderr)).Trim().Replace("\0", ""));
    }

    internal static async Task RequireMoveSupportAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var help = await RunAsync(["--help"], timeout.Token);
        if (!help.Output.Contains("--move", StringComparison.Ordinal))
            throw new IOException(Localizer.Get("WslMigrationUpdateRequired"));
    }

    internal static void RequireDockerStopped()
    {
        foreach (var name in new[] { "Docker Desktop", "com.docker.backend", "com.docker.proxy", "com.docker.build" })
        {
            var processes = Process.GetProcessesByName(name);
            var running = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
            if (running) throw new IOException(Localizer.Get("DockerMigrationStopRequired"));
        }
    }

    internal static void ValidateTarget(string source, string target, long bytes)
    {
        source = Normalize(source);
        target = Normalize(target);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase)
            || Path.GetPathRoot(target) == target
            || !FileSystemCleaner.IsPlainDirectoryPath(source)
            || !FileSystemCleaner.IsPlainDirectoryPath(target))
            throw new IOException(Localizer.Get("VirtualDiskMigrationInvalidPath"));
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException(Localizer.Get("VirtualDiskMigrationTargetExists"));
        var drive = new DriveInfo(Path.GetPathRoot(target)!);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS")
            throw new IOException(Localizer.Get("VirtualDiskMigrationInvalidPath"));
        if (drive.AvailableFreeSpace < bytes)
            throw new IOException(Localizer.Get("VirtualDiskMigrationNoSpace"));
    }
}
