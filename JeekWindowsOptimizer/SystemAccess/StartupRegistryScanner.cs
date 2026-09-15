using JeekTools;
using JeekWindowsOptimizer.Startup;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ZLogger;

namespace JeekWindowsOptimizer;

/// <summary>
///     Reads and toggles the Run-style logon entries, and the <c>StartupApproved</c> switches that
///     Task Manager and the Settings app use to enable and disable them.
///     <para>
///         Disabling goes through <c>StartupApproved</c> rather than deleting or moving the Run
///         value. The registration stays where the owning program expects it, the state matches what
///         Task Manager shows, and re-enabling is exact. Relocating entries to a private "disabled"
///         key, the way older tools did, loses both of those.
///     </para>
/// </summary>
public static class StartupRegistryScanner
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(StartupRegistryScanner));

    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string PolicyRunPath =
        @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";
    private const string ApprovedRoot =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>A Run-style key plus the StartupApproved key that switches its entries on and off.</summary>
    private sealed record RunLocation(
        RegistryHive Hive,
        RegistryView View,
        string SubKey,
        string? ApprovedSubKey,
        bool PolicyManaged,
        StartupItemKind Kind = StartupItemKind.LogonRegistry
    )
    {
        public string DisplayPath =>
            $"{(Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\\{SubKey}"
            + (View == RegistryView.Registry32 ? " (32-bit)" : "");
    }

    private static IEnumerable<RunLocation> Locations()
    {
        // Run32 is the StartupApproved key for entries that live in the 32-bit registry view.
        yield return new(RegistryHive.LocalMachine, RegistryView.Registry64, RunPath, "Run", false);
        yield return new(RegistryHive.LocalMachine, RegistryView.Registry32, RunPath, "Run32", false);
        yield return new(RegistryHive.CurrentUser, RegistryView.Registry64, RunPath, "Run", false);
        yield return new(RegistryHive.CurrentUser, RegistryView.Registry32, RunPath, "Run32", false);

        // RunOnce is its own group. It is a different thing from Run: Windows removes the value as
        // it runs it, so these entries are one-shot leftovers of an install or update rather than
        // something that starts with the machine every day, and lumping them together made a
        // permanent allow-list decision look due for an entry that deletes itself.
        yield return new(RegistryHive.LocalMachine, RegistryView.Registry64, RunOncePath, null, false,
            StartupItemKind.RunOnce);
        yield return new(RegistryHive.LocalMachine, RegistryView.Registry32, RunOncePath, null, false,
            StartupItemKind.RunOnce);
        yield return new(RegistryHive.CurrentUser, RegistryView.Registry64, RunOncePath, null, false,
            StartupItemKind.RunOnce);
        yield return new(RegistryHive.CurrentUser, RegistryView.Registry32, RunOncePath, null, false,
            StartupItemKind.RunOnce);

        // Policy-driven entries are managed by an administrator or by Group Policy; the app shows
        // them but will not fight the policy engine over them.
        yield return new(RegistryHive.LocalMachine, RegistryView.Registry64, PolicyRunPath, null, true);
        yield return new(RegistryHive.CurrentUser, RegistryView.Registry64, PolicyRunPath, null, true);
    }

    public static List<RawStartupEntry> Scan()
    {
        var entries = new List<RawStartupEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var location in Locations())
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
                using var key = baseKey.OpenSubKey(location.SubKey);
                if (key is null)
                    continue;

                foreach (var valueName in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(valueName))
                        continue;

                    var command = key.GetValue(valueName) as string ?? "";
                    if (string.IsNullOrWhiteSpace(command))
                        continue;

                    // The 64-bit and 32-bit views return the same values on a 64-bit-only key.
                    if (!seen.Add($"{location.Hive}|{location.SubKey}|{valueName}|{command}"))
                        continue;

                    var approved = ReadApprovedState(location, valueName);

                    entries.Add(
                        new RawStartupEntry
                        {
                            Kind = location.Kind,
                            Name = valueName,
                            Location = location.DisplayPath,
                            Command = command,
                            ImagePath = StartupIdentity.ExtractImagePath(command),
                            IsEnabled = approved.Enabled,
                            CanToggle = !location.PolicyManaged && location.ApprovedSubKey is not null,
                            ReadOnlyReasonKey = location.PolicyManaged
                                ? "StartupStatusPolicyManaged"
                                : location.ApprovedSubKey is null
                                    ? "StartupStatusRunOnce"
                                    : null,
                            RestoreState = Handle(location, valueName),
                        }
                    );
                }
            }
            catch (Exception ex)
            {
                Log.ZLogWarning(ex, $"Failed to read startup entries from {location.DisplayPath}");
            }
        }

        return entries;
    }

    private static string Handle(RunLocation location, string valueName) =>
        $"{location.Hive}|{location.View}|{location.ApprovedSubKey}|{valueName}";

    private static bool TryParseHandle(
        string? handle,
        out RegistryHive hive,
        out RegistryView view,
        out string approvedSubKey,
        out string valueName
    )
    {
        hive = RegistryHive.CurrentUser;
        view = RegistryView.Registry64;
        approvedSubKey = "";
        valueName = "";

        var parts = handle?.Split('|');
        if (parts is not { Length: 4 })
            return false;
        if (!Enum.TryParse(parts[0], out hive) || !Enum.TryParse(parts[1], out view))
            return false;
        if (parts[2].Length == 0)
            return false;

        approvedSubKey = parts[2];
        valueName = parts[3];
        return valueName.Length > 0;
    }

    /// <summary>
    ///     Reads the StartupApproved switch. The value is a 12-byte blob whose first byte carries the
    ///     state: bit 0 set means disabled. A missing value means the entry has never been switched
    ///     off, so it is enabled.
    /// </summary>
    private static (bool Enabled, byte[]? Raw) ReadApprovedState(RunLocation location, string valueName)
    {
        if (location.ApprovedSubKey is null)
            return (true, null);

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(location.Hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey($@"{ApprovedRoot}\{location.ApprovedSubKey}");
            if (key?.GetValue(valueName) is not byte[] { Length: > 0 } raw)
                return (true, null);

            return ((raw[0] & 1) == 0, raw);
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to read StartupApproved state for {valueName}");
            return (true, null);
        }
    }

    /// <summary>Switches a Run entry on or off the same way Task Manager does.</summary>
    public static bool SetEnabled(string? handle, bool enabled, out string? error)
    {
        error = null;

        if (!TryParseHandle(handle, out var hive, out _, out var approvedSubKey, out var valueName))
        {
            error = "Entry cannot be switched.";
            return false;
        }

        try
        {
            // StartupApproved itself is not redirected, so always use the 64-bit view; which of the
            // Run / Run32 subkeys to write is already encoded in the handle.
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey($@"{ApprovedRoot}\{approvedSubKey}", writable: true);
            if (key is null)
            {
                error = "StartupApproved key is not writable.";
                return false;
            }

            var blob = new byte[12];
            if (enabled)
            {
                blob[0] = 2;
            }
            else
            {
                blob[0] = 3;
                // Bytes 4..11 hold the FILETIME of the change; Task Manager shows it as "disabled on".
                BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(blob, 4);
            }

            key.SetValue(valueName, blob, RegistryValueKind.Binary);
            return true;
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to set StartupApproved state for {valueName}");
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Sets a Startup folder shortcut's StartupApproved switch, keyed by file name.</summary>
    public static bool SetStartupFolderEnabled(
        bool perUser,
        string fileName,
        bool enabled,
        out string? error
    ) =>
        SetEnabled(
            $"{(perUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine)}|{RegistryView.Registry64}|StartupFolder|{fileName}",
            enabled,
            out error
        );

    /// <summary>Reads a Startup folder shortcut's StartupApproved switch.</summary>
    public static bool IsStartupFolderEnabled(bool perUser, string fileName)
    {
        var location = new RunLocation(
            perUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
            RegistryView.Registry64,
            "",
            "StartupFolder",
            false
        );
        return ReadApprovedState(location, fileName).Enabled;
    }

    /// <summary>The handle <see cref="SetEnabled" /> expects for a Startup folder shortcut.</summary>
    public static string StartupFolderHandle(bool perUser, string fileName) =>
        $"{(perUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine)}|{RegistryView.Registry64}|StartupFolder|{fileName}";
}
