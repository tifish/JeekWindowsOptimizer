using JeekTools;
using JeekWindowsOptimizer.Startup;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ZLogger;

namespace JeekWindowsOptimizer;

/// <summary>
///     Reads the per-user and all-users Startup folders. Shortcuts are resolved to the program they
///     launch, because that is what the decision has to be about: replacing the target of an
///     existing shortcut must not inherit the shortcut's permission.
/// </summary>
public static class StartupFolderScanner
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(StartupFolderScanner));

    private const string UserShellFoldersPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

    public static List<RawStartupEntry> Scan()
    {
        var entries = new List<RawStartupEntry>();

        foreach (var (perUser, folder) in ResolveFolders())
        {
            try
            {
                if (!Directory.Exists(folder))
                    continue;

                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var fileName = Path.GetFileName(file);

                    // Explorer ignores desktop.ini; so should the list.
                    if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var command = file;
                    var imagePath = file;

                    if (Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                        && ShellLinkResolver.TryResolve(file, out var target, out var arguments))
                    {
                        imagePath = target;
                        command = arguments.Length > 0 ? $"\"{target}\" {arguments}" : target;
                    }

                    entries.Add(
                        new RawStartupEntry
                        {
                            Kind = StartupItemKind.StartupFolder,
                            Name = fileName,
                            Location = folder,
                            Command = command,
                            ImagePath = imagePath,
                            IsEnabled = StartupRegistryScanner.IsStartupFolderEnabled(perUser, fileName),
                            RestoreState = StartupRegistryScanner.StartupFolderHandle(perUser, fileName),
                        }
                    );
                }
            }
            catch (Exception ex)
            {
                Log.ZLogWarning(ex, $"Failed to read the Startup folder {folder}");
            }
        }

        return entries;
    }

    /// <summary>
    ///     Resolves both Startup folders from the registry rather than assuming the default paths.
    ///     A redirected Startup folder is itself worth seeing, and hard-coding the default would
    ///     silently scan the wrong directory.
    /// </summary>
    private static List<(bool PerUser, string Folder)> ResolveFolders()
    {
        var folders = new List<(bool, string)>();

        var user = ReadShellFolder(RegistryHive.CurrentUser, "Startup")
            ?? SafeFolder(Environment.SpecialFolder.Startup);
        if (!string.IsNullOrWhiteSpace(user))
            folders.Add((true, user));

        var common = ReadShellFolder(RegistryHive.LocalMachine, "Common Startup")
            ?? SafeFolder(Environment.SpecialFolder.CommonStartup);
        if (!string.IsNullOrWhiteSpace(common)
            && !folders.Any(f => string.Equals(f.Item2, common, StringComparison.OrdinalIgnoreCase)))
            folders.Add((false, common));

        return folders;
    }

    private static string? ReadShellFolder(RegistryHive hive, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(UserShellFoldersPath);
            if (key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                is not string raw || raw.Length == 0)
                return null;

            return Environment.ExpandEnvironmentVariables(raw);
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to resolve the {valueName} shell folder");
            return null;
        }
    }

    private static string SafeFolder(Environment.SpecialFolder folder)
    {
        try
        {
            return Environment.GetFolderPath(folder);
        }
        catch
        {
            return "";
        }
    }
}
