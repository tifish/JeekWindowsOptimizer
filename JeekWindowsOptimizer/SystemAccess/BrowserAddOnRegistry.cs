using JeekTools;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ZLogger;

namespace JeekWindowsOptimizer;

/// <summary>
///     Reads and writes the add-on switch that Internet Explorer's "Manage add-ons" dialog uses.
///     <para>
///         Browser add-ons need their own lever. Explorer's <c>Shell Extensions\Blocked</c> list is
///         consulted when the shell creates a shell extension; it does not stop the browser host
///         loading a Browser Helper Object, so using it here would report success while the add-on
///         kept loading.
///     </para>
/// </summary>
public static class BrowserAddOnRegistry
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(BrowserAddOnRegistry));

    private const string SettingsPath =
        @"Software\Microsoft\Windows\CurrentVersion\Ext\Settings";

    /// <summary>Bit 0 of the Flags value means "the user turned this add-on off".</summary>
    private const int DisabledBit = 1;

    /// <summary>True unless the add-on has been switched off. An unknown CLSID counts as enabled.</summary>
    public static bool IsEnabled(string clsid)
    {
        var normalized = ComServerRegistry.Normalize(clsid);
        if (normalized.Length == 0)
            return true;

        // A machine-wide switch wins over the per-user one.
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            if (TryReadFlags(hive, normalized, out var flags) && (flags & DisabledBit) != 0)
                return false;
        }

        return true;
    }

    private static bool TryReadFlags(RegistryHive hive, string clsid, out int flags)
    {
        flags = 0;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey($@"{SettingsPath}\{clsid}");
            if (key?.GetValue("Flags") is not int value)
                return false;

            flags = value;
            return true;
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to read the add-on flags for {clsid}");
            return false;
        }
    }

    /// <summary>
    ///     Switches every add-on CLSID backed by one DLL on or off, the way Manage add-ons does.
    ///     Written per user: that is where the dialog writes, and it needs no elevation.
    /// </summary>
    public static bool SetEnabled(string? clsidList, bool enabled, out string? error)
    {
        error = null;

        var clsids = (clsidList ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ComServerRegistry.Normalize)
            .Where(clsid => clsid.Length > 0)
            .ToList();

        if (clsids.Count == 0)
        {
            error = "No browser add-on CLSIDs to change.";
            return false;
        }

        try
        {
            foreach (var clsid in clsids)
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"{SettingsPath}\{clsid}", writable: true);
                if (key is null)
                {
                    error = "The add-on settings key is not writable.";
                    return false;
                }

                // Preserve the other bits. Real entries carry flags such as 0x400 that mean
                // something else entirely, and overwriting the whole value would discard them.
                var current = key.GetValue("Flags") as int? ?? 0;
                var updated = enabled ? current & ~DisabledBit : current | DisabledBit;
                key.SetValue("Flags", updated, RegistryValueKind.DWord);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to change a browser add-on's enabled state");
            error = ex.Message;
            return false;
        }
    }
}
