using JeekTools;
using JeekWindowsOptimizer.Startup;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ZLogger;

namespace JeekWindowsOptimizer;

/// <summary>
///     Enumerates the in-process extensions that load into File Explorer and into the browser host,
///     reporting them one row per DLL rather than one row per registration.
///     <para>
///         A single cloud-storage or archiver client typically registers seven or eight CLSIDs: a
///         context menu on files, another on folder backgrounds, an icon overlay, a property sheet,
///         a column handler. Listing those separately asks the user the same question eight times
///         about one program. Grouping by the DLL that backs them asks it once, and the answer then
///         covers every hook that DLL installs.
///     </para>
///     <para>
///         Explorer extensions and browser add-ons are reported as two separate kinds, because they
///         are switched off by two different mechanisms. Explorer consults its own
///         <c>Shell Extensions\Blocked</c> list; the browser host consults the add-on flags that
///         "Manage add-ons" writes. A DLL that registers both appears in both groups, since allowing
///         a context menu and allowing a browser add-on are genuinely separate permissions.
///     </para>
///     <para>
///         Nothing is ever unregistered. Both mechanisms leave every registration exactly where its
///         owner put it, so a repair install cannot quietly undo the user's choice, and re-enabling
///         is just removing the flag.
///     </para>
/// </summary>
public static class ShellExtensionScanner
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(ShellExtensionScanner));

    private const string ExplorerPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    private const string InternetExplorerPath = @"Software\Microsoft\Internet Explorer";

    private const string BlockedPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    /// <summary>Classes whose ShellEx handlers Explorer consults for ordinary browsing.</summary>
    private static readonly string[] ShellExClasses =
    [
        "*",
        "AllFilesystemObjects",
        "Directory",
        @"Directory\Background",
        "Folder",
        "Drive",
        "LibraryFolder",
        "Network",
        "Printers",
    ];

    private static readonly string[] ShellExHandlerGroups =
    [
        "ContextMenuHandlers",
        "PropertySheetHandlers",
        "DragDropHandlers",
        "CopyHookHandlers",
        "ColumnHandlers",
    ];

    /// <summary>ShellEx keys whose default value is the CLSID directly, with no per-handler subkey.</summary>
    private static readonly string[] ShellExDirectHandlers = ["IconHandler", "ThumbnailHandler"];

    /// <summary>Which host loads the extension, and therefore which group it belongs to.</summary>
    private enum HookHost
    {
        Explorer,
        InternetExplorer,
    }

    /// <summary>How, if at all, this build can switch a registration off.</summary>
    private enum ToggleMechanism
    {
        /// <summary>Explorer's blocked-CLSID list.</summary>
        ShellBlockedList,

        /// <summary>The browser's own add-on flag, as written by Manage add-ons.</summary>
        BrowserAddOnFlag,

        /// <summary>No switch Windows provides; the row is shown read-only.</summary>
        None,
    }

    private sealed class DllGroup
    {
        public required string DllPath { get; init; }
        public required HookHost Host { get; init; }
        public List<string> Clsids { get; } = [];
        public List<string> HookPoints { get; } = [];
        public string? FriendlyName { get; set; }

        /// <summary>
        ///     False as soon as one hook in the group has no supported switch. Turning off only part
        ///     of a DLL's registrations and reporting success would be a lie.
        /// </summary>
        public bool CanToggle { get; set; } = true;
    }

    public static List<RawStartupEntry> Scan()
    {
        var groups = new Dictionary<string, DllGroup>(StringComparer.OrdinalIgnoreCase);
        var clsidToDll = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        void Register(string? clsid, string hookPoint, HookHost host, ToggleMechanism mechanism)
        {
            if (string.IsNullOrWhiteSpace(clsid))
                return;

            var normalized = ComServerRegistry.Normalize(clsid);
            if (normalized.Length == 0)
                return;

            if (!clsidToDll.TryGetValue(normalized, out var dll))
            {
                // Both hosts load these in-process, so an out-of-process server does not count.
                dll = ComServerRegistry.ResolveInProcServer(normalized);
                clsidToDll[normalized] = dll;
            }

            // A CLSID that resolves to no in-process server is a stale registration, not something
            // that will ever be loaded.
            if (string.IsNullOrWhiteSpace(dll))
                return;

            var key = $"{host}|{StartupIdentity.NormalizePath(dll)}";
            if (!groups.TryGetValue(key, out var group))
            {
                group = new DllGroup { DllPath = dll!, Host = host };
                groups[key] = group;
            }

            if (mechanism == ToggleMechanism.None)
                group.CanToggle = false;

            if (!group.Clsids.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                group.Clsids.Add(normalized);
                group.FriendlyName ??= ComServerRegistry.ResolveName(normalized);
            }

            if (!group.HookPoints.Contains(hookPoint, StringComparer.OrdinalIgnoreCase))
                group.HookPoints.Add(hookPoint);
        }

        ScanExplorerLists(Register);
        ScanShellExHandlers(Register);
        ScanBrowserAddOns(Register);
        ScanProtocolHandlers(Register);

        var blocked = ReadBlockedClsids();

        return
        [
            .. groups
                .Values.Select(group => new RawStartupEntry
                {
                    Kind = group.Host == HookHost.Explorer
                        ? StartupItemKind.ExplorerExtension
                        : StartupItemKind.InternetExplorerExtension,
                    Name = group.FriendlyName ?? Path.GetFileName(group.DllPath),
                    Location = group.DllPath,
                    Command = group.DllPath,
                    ImagePath = group.DllPath,
                    // The host loads the DLL unless every CLSID it backs has been switched off.
                    IsEnabled = group.Host == HookHost.Explorer
                        ? group.Clsids.Any(clsid => !blocked.Contains(clsid))
                        : group.Clsids.Any(BrowserAddOnRegistry.IsEnabled),
                    CanToggle = group.CanToggle,
                    ReadOnlyReasonKey = group.CanToggle ? null : "StartupStatusNoSwitch",
                    HookPoints = group.HookPoints,
                    Handles = group.Clsids,
                    RestoreState = string.Join(';', group.Clsids),
                })
                .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    // ---------- Explorer ----------

    private static void ScanExplorerLists(
        Action<string?, string, HookHost, ToggleMechanism> register
    )
    {
        void Explorer(string? clsid, string hookPoint) =>
            register(clsid, hookPoint, HookHost.Explorer, ToggleMechanism.ShellBlockedList);

        ForEachHive(
            (hive, hiveName) =>
            {
                // Keys whose value names are CLSIDs.
                ReadValueNamesAsClsids(hive, $@"{ExplorerPath}\ShellExecuteHooks", Explorer, $"{hiveName} ShellExecuteHooks");
                ReadValueNamesAsClsids(hive, $@"{ExplorerPath}\SharedTaskScheduler", Explorer, $"{hiveName} SharedTaskScheduler");

                // Keys whose subkey names are CLSIDs.
                ReadSubKeyNamesAsClsids(hive, $@"{ExplorerPath}\ShellServiceObjects", Explorer, $"{hiveName} ShellServiceObjects");
                ReadSubKeyNamesAsClsids(hive, $@"{ExplorerPath}\Desktop\NameSpace", Explorer, $"{hiveName} Desktop namespace");
                ReadSubKeyNamesAsClsids(hive, $@"{ExplorerPath}\MyComputer\NameSpace", Explorer, $"{hiveName} This PC namespace");

                // Values whose data is the CLSID.
                ReadValueDataAsClsids(hive, $@"{ExplorerPath}\ShellServiceObjectDelayLoad", Explorer, $"{hiveName} ShellServiceObjectDelayLoad");

                // Icon overlays: each subkey's default value is the CLSID.
                ReadSubKeyDefaultsAsClsids(
                    hive,
                    $@"{ExplorerPath}\ShellIconOverlayIdentifiers",
                    Explorer,
                    name => $"{hiveName} icon overlay ({name})"
                );
            }
        );
    }

    private static void ScanShellExHandlers(
        Action<string?, string, HookHost, ToggleMechanism> register
    )
    {
        void Explorer(string? clsid, string hookPoint) =>
            register(clsid, hookPoint, HookHost.Explorer, ToggleMechanism.ShellBlockedList);

        foreach (var className in ShellExClasses)
        {
            foreach (var handlerGroup in ShellExHandlerGroups)
            {
                ReadSubKeyDefaultsAsClsids(
                    Registry.ClassesRoot,
                    $@"{className}\ShellEx\{handlerGroup}",
                    Explorer,
                    name => $"{className} {Humanize(handlerGroup)} ({name})"
                );
            }

            foreach (var handler in ShellExDirectHandlers)
            {
                var clsid = ReadDefaultValue(Registry.ClassesRoot, $@"{className}\ShellEx\{handler}");
                Explorer(clsid, $"{className} {Humanize(handler)}");
            }
        }
    }

    // ---------- Internet Explorer ----------

    /// <summary>
    ///     The browser add-on registration points. Internet Explorer itself is gone from Windows 11,
    ///     but these still load into Edge's IE mode and into any application hosting the WebBrowser
    ///     control, so they remain worth listing and worth switching off.
    /// </summary>
    private static void ScanBrowserAddOns(
        Action<string?, string, HookHost, ToggleMechanism> register
    )
    {
        void AddOn(string? clsid, string hookPoint) =>
            register(clsid, hookPoint, HookHost.InternetExplorer, ToggleMechanism.BrowserAddOnFlag);

        ForEachHive(
            (hive, hiveName) =>
            {
                // Browser Helper Objects live under the Explorer key but load into the browser.
                ReadSubKeyNamesAsClsids(hive, $@"{ExplorerPath}\Browser Helper Objects", AddOn, $"{hiveName} Browser Helper Object");

                ReadValueNamesAsClsids(hive, $@"{InternetExplorerPath}\Toolbar", AddOn, $"{hiveName} toolbar");
                ReadValueNamesAsClsids(hive, $@"{InternetExplorerPath}\Toolbar\WebBrowser", AddOn, $"{hiveName} browser toolbar");
                ReadValueNamesAsClsids(hive, $@"{InternetExplorerPath}\Toolbar\ShellBrowser", AddOn, $"{hiveName} shell toolbar");
                ReadValueNamesAsClsids(hive, $@"{InternetExplorerPath}\URLSearchHooks", AddOn, $"{hiveName} URL search hook");
                ReadSubKeyNamesAsClsids(hive, $@"{InternetExplorerPath}\Explorer Bars", AddOn, $"{hiveName} Explorer bar");
                ReadSubKeyNamesAsClsids(hive, $@"{InternetExplorerPath}\Extensions", AddOn, $"{hiveName} browser extension");
            }
        );
    }

    private static void ScanProtocolHandlers(
        Action<string?, string, HookHost, ToggleMechanism> register
    )
    {
        // Protocol filters and handlers are URL moniker features used by the browser stack. Windows
        // offers no supported switch for them, so they are listed read-only rather than disabled by
        // deleting the registration, which a repair install would silently put back anyway.
        void Protocol(string? clsid, string hookPoint) =>
            register(clsid, hookPoint, HookHost.InternetExplorer, ToggleMechanism.None);

        ReadSubKeyDefaultsAsClsids(
            Registry.ClassesRoot,
            @"Protocols\Filter",
            Protocol,
            name => $"Protocol filter ({name})"
        );

        try
        {
            using var handlers = Registry.ClassesRoot.OpenSubKey(@"Protocols\Handler");
            if (handlers is null)
                return;

            foreach (var name in handlers.GetSubKeyNames())
            {
                using var handler = handlers.OpenSubKey(name);
                var clsid = handler?.GetValue("CLSID") as string ?? handler?.GetValue("") as string;
                Protocol(clsid, $"Protocol handler ({name})");
            }
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to read protocol handlers");
        }
    }

    // ---------- Registry readers ----------

    private static void ForEachHive(Action<RegistryKey, string> action)
    {
        action(Registry.LocalMachine, "HKLM");
        action(Registry.CurrentUser, "HKCU");
    }

    private static void ReadValueNamesAsClsids(
        RegistryKey hive,
        string path,
        Action<string?, string> register,
        string hookPoint
    )
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key is null)
                return;
            foreach (var valueName in key.GetValueNames())
                register(valueName, hookPoint);
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to read shell extension list {path}");
        }
    }

    private static void ReadValueDataAsClsids(
        RegistryKey hive,
        string path,
        Action<string?, string> register,
        string hookPoint
    )
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key is null)
                return;
            foreach (var valueName in key.GetValueNames())
                register(key.GetValue(valueName) as string, hookPoint);
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to read shell extension list {path}");
        }
    }

    private static void ReadSubKeyNamesAsClsids(
        RegistryKey hive,
        string path,
        Action<string?, string> register,
        string hookPoint
    )
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key is null)
                return;
            foreach (var subKeyName in key.GetSubKeyNames())
                register(subKeyName, hookPoint);
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to read shell extension list {path}");
        }
    }

    private static void ReadSubKeyDefaultsAsClsids(
        RegistryKey hive,
        string path,
        Action<string?, string> register,
        Func<string, string> describe
    )
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key is null)
                return;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(subKeyName);
                register(subKey?.GetValue("") as string, describe(subKeyName));
            }
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to read shell extension handlers under {path}");
        }
    }

    private static string? ReadDefaultValue(RegistryKey hive, string path)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue("") as string;
        }
        catch
        {
            return null;
        }
    }

    private static string Humanize(string handlerGroup) =>
        handlerGroup switch
        {
            "ContextMenuHandlers" => "context menu",
            "PropertySheetHandlers" => "property sheet",
            "DragDropHandlers" => "drag and drop",
            "CopyHookHandlers" => "copy hook",
            "ColumnHandlers" => "column",
            "IconHandler" => "icon",
            "ThumbnailHandler" => "thumbnail",
            _ => handlerGroup,
        };

    // ---------- Blocking ----------

    private static HashSet<string> ReadBlockedClsids()
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                var path = hive == Registry.CurrentUser
                    ? @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked"
                    : BlockedPath;
                using var key = hive.OpenSubKey(path);
                if (key is null)
                    continue;

                foreach (var valueName in key.GetValueNames())
                {
                    var normalized = ComServerRegistry.Normalize(valueName);
                    if (normalized.Length > 0)
                        blocked.Add(normalized);
                }
            }
            catch (Exception ex)
            {
                Log.ZLogWarning(ex, $"Failed to read the blocked shell extension list");
            }
        }

        return blocked;
    }

    /// <summary>Blocks or unblocks every CLSID backed by one DLL, in a single step.</summary>
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
            error = "No shell extension CLSIDs to change.";
            return false;
        }

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(BlockedPath, writable: true);
            if (key is null)
            {
                error = "The blocked shell extension list is not writable.";
                return false;
            }

            foreach (var clsid in clsids)
            {
                if (enabled)
                    key.DeleteValue(clsid, throwOnMissingValue: false);
                else
                    key.SetValue(clsid, "", RegistryValueKind.String);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to change the blocked shell extension list");
            error = ex.Message;
            return false;
        }
    }
}
