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
        Tool("time_sync_status",
            "Read Windows Time (W32Time) sync state: service start mode, trigger count, NTP Type, NtpClient, and the matching optimization item.",
            new()));

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
