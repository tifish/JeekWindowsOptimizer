using System.Text.Json.Nodes;

namespace JeekWindowsOptimizer.Mcp;

/// <summary>
/// The debug surface's tool contract. Every tool the host serves must appear
/// here — a tool missing from this list is invisible to clients.
/// </summary>
public static class DebugMcpContract
{
    public const string PathHelp =
        "Paths start from a root: App (the Application), Desktop (the desktop lifetime), "
        + "MainWindow, or MainVm (MainWindow.DataContext). Segments: '.Member' reads a property or field "
        + "(non-public included), '[0]' indexes a list, '[\"key\"]' indexes a dictionary, and "
        + "'#Name' finds a named control in the visual tree below the current object. "
        + "Examples: MainVm.Groups[0].Items[0].IsOptimized, MainWindow.#SearchTextBox.Text";

    public static JsonArray BuildToolList() => new(
        Tool("describe",
            "Overview of the running app: instance, windows, roots, path syntax, and log file. Start here.",
            new()),
        Tool("get_value", "Read a value from the app's object graph. " + PathHelp,
            new()
            {
                ["path"] = Prop("string", "Object path to read."),
                ["depth"] = Prop("integer", "Nested expansion depth, 0-5 (default 1)."),
            }, ["path"]),
        Tool("set_value", "Write a property, field, or list element on the UI thread. " + PathHelp,
            new()
            {
                ["path"] = Prop("string", "Object path to write."),
                ["value"] = new JsonObject
                {
                    ["description"] = "New JSON value; {$path: ...} passes a live object.",
                },
            }, ["path", "value"]),
        Tool("invoke", "Execute an ICommand or call a method on the UI thread. " + PathHelp,
            new()
            {
                ["path"] = Prop("string", "Object path ending with a command or method."),
                ["args"] = new JsonObject { ["type"] = "array", ["description"] = "JSON arguments." },
                ["depth"] = Prop("integer", "Return expansion depth, 0-5 (default 1)."),
            }, ["path"]),
        Tool("list_members", "List properties, fields, and methods at a path. " + PathHelp,
            new() { ["path"] = Prop("string", "Object path to inspect.") }, ["path"]),
        Tool("visual_tree", "Dump the visual tree below a visual.",
            new()
            {
                ["path"] = Prop("string", "Starting Visual path (default MainWindow)."),
                ["max_depth"] = Prop("integer", "Maximum depth (default 12)."),
            }),
        Tool("screenshot", "Render the main window to PNG.", new()),
        Tool("read_logs", "Read the current app log tail.",
            new()
            {
                ["lines"] = Prop("integer", "Lines, 1-2000 (default 200)."),
                ["filter"] = Prop("string", "Case-insensitive filter."),
            }),
        Tool("defender_status",
            "Read the effective Defender tamper-protection state, its runtime source, and the registry fallback.",
            new()),
        Tool("optimization_items",
            "List the optimization groups and items with their optimized/checked state.",
            new()
            {
                ["category"] = Prop("string", "Optional tab filter: Optimizing | Antivirus | Personal."),
                ["only_not_optimized"] = Prop("boolean", "Only list items that are not optimized (default false)."),
            }),
        Tool("optimization_init_timings",
            "Startup detection timings: total time, the Microsoft Store package snapshot, and each item's Initialize() duration, slowest first.",
            new()
            {
                ["top"] = Prop("integer", "Number of slowest items to list, 1-1000 (default 30)."),
            }),
        Tool("time_sync_status",
            "Read Windows Time (W32Time) sync state: service start mode, trigger count, NTP Type, NtpClient, and the matching optimization item.",
            new()),
        Tool("service_probe",
            "Report whether a Windows service exists and its start mode.",
            new() { ["name"] = Prop("string", "Service name.") }, ["name"]),
        Tool("service_delete",
            "Stop and remove a Windows service via WindowsService.Delete(). Destructive; debug surface only.",
            new() { ["name"] = Prop("string", "Service name.") }, ["name"]),
        Tool("driver_remove_probe",
            "Run DriverItem.RemoveProduct() over the given service names and driver paths and "
                + "report what could not be removed. Destructive (it attempts real deletion); debug surface only.",
            new()
            {
                ["services"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = Prop("string", "Service name."),
                    ["description"] = "Service names to stop and delete.",
                },
                ["paths"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = Prop("string", "Driver file or folder path (may contain * or ?)."),
                    ["description"] = "Driver paths to delete.",
                },
            }),
        Tool("group_navigation_probe", "Test actual routed navigation clicks and heading alignment, including repeat clicks, collapsed/filtered groups and a short final group. No cleanup or relocation is performed.", new()),
        Tool("disk_space_items",
            "List the Disk Space tab's items (cleanup and relocation) with global scanning/busy state, scan/clean/move command availability, size, per-item state, checked flag, current location, target drives, and object paths. Creates the items if the tab has not been shown yet.",
            new()),
        Tool("virtual_disk_migration_probe", "Test WSL orchestration and real Docker disk copy/junction/restore against isolated temporary fixtures. Does not move installed distributions or Docker data.", new()),
        Tool("wsl_native_migration_probe", "Create a random temporary WSL 2 distribution, migrate it across two NTFS drives and back, verify its exported filesystem, then unregister only that fixture. Does not move user distributions.", new()),
        Tool("disk_space_cleanup_probe",
            "Run isolated cleanup regression checks in the app. Does not clean user data.",
            new() { ["scenario"] = Prop("string", "Scenario: accuracy | browser | nuget | user_dumps | queue | shadows | hibernation | drivers | graphics | installer_baseline | lcu | developer | pnpm | selection.") }),
        Tool("disk_space_scan",
            "Run the Disk Space scan (all items in parallel, DISM analysis included) and wait for it, then return the item list.",
            new()
            {
                ["timeout_seconds"] = Prop("integer", "Max seconds to wait (default 600)."),
            }),
        Tool("disk_space_clean",
            "Queue the given cleanup items and wait for this request to finish. Destructive; debug surface only.",
            new()
            {
                ["items"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = Prop("string", "Cleanup item NameKey, e.g. TempFilesCleanupName."),
                    ["description"] = "NameKeys of the cleanup items to run.",
                },
                ["timeout_seconds"] = Prop("integer", "Max seconds to wait (default 1800)."),
            }, ["items"]),
        Tool("disk_space_enqueue",
            "Enqueue one clean, move or restore operation and return immediately. Inspect disk_space_items for progress. No GUI confirmation; destructive, debug surface only.",
            new()
            {
                ["item"] = Prop("string", "Item NameKey."),
                ["action"] = Prop("string", "clean | move | restore"),
                ["drive"] = Prop("string", "For move: destination drive letter/root; defaults to the selected drive."),
            }, ["item", "action"]),
        Tool("disk_space_relocation_check",
            "Dry-run a relocation item against a drive: reports the current location, the computed target path, and the local validation verdict. Changes nothing.",
            new()
            {
                ["item"] = Prop("string", "Relocation item NameKey, e.g. DownloadsRelocationName."),
                ["drive"] = Prop("string", "Target drive letter or root, e.g. D or D:\\ (default: the item's selected drive)."),
            }, ["item"]),
        Tool("disk_space_relocate",
            "Perform a relocation without the GUI confirmation. Destructive; debug surface only. Pass 'drive' to move like the GUI does, or 'target_path' (user folders only) to redirect to an explicit folder, e.g. to move a folder back.",
            new()
            {
                ["item"] = Prop("string", "Relocation item NameKey."),
                ["drive"] = Prop("string", "Target drive letter or root (default: the item's selected drive)."),
                ["target_path"] = Prop("string", "User folders only: explicit target folder; overrides 'drive'."),
                ["timeout_seconds"] = Prop("integer", "Max seconds to wait (default 1800)."),
            }, ["item"]),
        Tool("disk_space_restore_default",
            "Move a relocation item back to its Windows default location (user profile folder, or automatic paging-file management) without the GUI confirmation. Destructive; debug surface only.",
            new()
            {
                ["item"] = Prop("string", "Relocation item NameKey."),
                ["timeout_seconds"] = Prop("integer", "Max seconds to wait (default 1800)."),
            }, ["item"]),
        Tool("startup_items",
            "List the Startup tab's items: kind, name, publisher and signature class, command, location, "
                + "whether it runs at startup now, the remembered decision and where that decision was made. "
                + "Runs the scan first if the tab has not been visited yet.",
            new()
            {
                ["kind"] = Prop("string", "Optional filter: LogonRegistry | RunOnce | StartupFolder | ScheduledTask | ExplorerExtension | InternetExplorerExtension | Service | Driver."),
                ["only_pending"] = Prop("boolean", "Only entries with no recorded decision (default false)."),
                ["include_hidden"] = Prop("boolean", "Include entries the tab's filters hide, such as Windows components (default false)."),
                ["limit"] = Prop("integer", "Maximum rows to return, 1-2000 (default 200)."),
            }),
        Tool("startup_scan",
            "Re-run the startup scan and wait for it, then return the item list. Reads only; changes nothing.",
            new()
            {
                ["timeout_seconds"] = Prop("integer", "Max seconds to wait (default 300)."),
            }),
        Tool("startup_decide",
            "Record allow or deny for one entry and put it into effect immediately, exactly as the GUI "
                + "buttons do. Changes the system; debug surface only.",
            new()
            {
                ["key"] = Prop("string", "Ledger key from startup_items."),
                ["decision"] = Prop("string", "allow | deny"),
            }, ["key", "decision"]),
        Tool("startup_enforce",
            "Turn off every entry that is denied but still runs, without the GUI confirmation. "
                + "Changes the system; debug surface only.",
            new()
            {
                ["timeout_seconds"] = Prop("integer", "Max seconds to wait (default 600)."),
            }),
        Tool("startup_baseline",
            "Record the current state of this machine as the decision baseline: what runs becomes allowed, "
                + "what is already off becomes denied. Writes decisions but changes nothing on the system.",
            new()),
        Tool("startup_ledger",
            "Inspect the decision ledger itself: file path, epoch, entry count, whether writes are blocked "
                + "because the file is unreadable, and optionally the raw entries.",
            new()
            {
                ["entries"] = Prop("boolean", "Include the stored entries (default false)."),
                ["limit"] = Prop("integer", "Maximum entries to return, 1-2000 (default 100)."),
            }),
        Tool("startup_service_probe",
            "Deny then allow one real service through the startup toggle and verify the start type, "
                + "stop on deny, start on allow, and exact restoration of the original start type, "
                + "delayed-start flag and running state. Also checks that a service with running "
                + "dependents is reported rather than stopped. Writes nothing to the decision ledger. "
                + "Briefly stops a real service; debug surface only.",
            new() { ["name"] = Prop("string", "Service name (default W32Time). Pick one that is safe to stop briefly.") }),
        Tool("startup_ledger_probe",
            "Run isolated regression checks against synthetic data, a temporary folder and a scratch "
                + "class id: cross-machine merge, reset epoch, sync conflict copies, unreadable files, "
                + "identity keys and the browser add-on switch. Reads no real startup entry and never "
                + "touches the real decision file.",
            new() { ["scenario"] = Prop("string", "Scenario: merge | epoch | conflict | poison | identity | addon | all (default all).") }),
        Tool("disk_space_move_checked",
            "Run the batch move (each checked relocation item to its own selected drive) without the GUI confirmation. Destructive; debug surface only.",
            new()
            {
                ["items"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = Prop("string", "Relocation item NameKey."),
                    ["description"] = "If given, sets the checked state to exactly these items first; otherwise uses the current checkboxes.",
                },
                ["timeout_seconds"] = Prop("integer", "Max seconds to wait (default 1800)."),
            }));

    private static JsonObject Tool(string name, string description, JsonObject properties, string[]? required = null)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required is { Length: > 0 })
            schema["required"] = new JsonArray([.. required.Select(JsonNode (r) => r)]);
        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
    }

    private static JsonObject Prop(string type, string description) =>
        new() { ["type"] = type, ["description"] = description };
}
