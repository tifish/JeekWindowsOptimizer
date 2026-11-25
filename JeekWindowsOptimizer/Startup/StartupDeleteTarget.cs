using Microsoft.Win32;

namespace JeekWindowsOptimizer.Startup;

/// <summary>
///     Registration details needed to permanently delete an orphaned startup entry.
///     Only entries whose target file is missing (<see cref="StartupItem.IsOrphaned" />) are ever
///     deleted; healthy entries are always switched through Windows' own enable/disable mechanism.
/// </summary>
public abstract record StartupDeleteTarget;

/// <summary>A Run or RunOnce registry value, plus its optional StartupApproved switch.</summary>
public sealed record RegistryValueDeleteTarget(
    RegistryHive Hive,
    RegistryView View,
    string SubKey,
    string ValueName,
    string? ApprovedSubKey
) : StartupDeleteTarget;

/// <summary>A shortcut or file inside a Startup folder, plus its StartupApproved switch.</summary>
public sealed record StartupFolderDeleteTarget(
    string FilePath,
    RegistryHive ApprovedHive,
    string FileName
) : StartupDeleteTarget;

/// <summary>A scheduled task identified by its full Task Scheduler path.</summary>
public sealed record ScheduledTaskDeleteTarget(string TaskPath) : StartupDeleteTarget;

/// <summary>A Windows service or non-boot-critical driver under CurrentControlSet\Services.</summary>
public sealed record ServiceDeleteTarget(string ServiceName) : StartupDeleteTarget;

/// <summary>One concrete registry hook pointing to a shell or browser extension CLSID.</summary>
/// <param name="ValueName">
///     When non-null, delete this value under <paramref name="SubKeyPath" />.
///     When null, delete the subkey tree at <paramref name="SubKeyPath" />.
/// </param>
public sealed record ShellExtensionRegistration(
    RegistryHive Hive,
    string SubKeyPath,
    string? ValueName
);

/// <summary>All hook registrations backed by one missing shell/browser extension DLL.</summary>
public sealed record ShellExtensionDeleteTarget(
    IReadOnlyList<ShellExtensionRegistration> Registrations,
    IReadOnlyList<string> Clsids,
    bool IsExplorer
) : StartupDeleteTarget;
