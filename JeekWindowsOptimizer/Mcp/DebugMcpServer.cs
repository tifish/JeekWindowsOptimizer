using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer.Mcp;

/// <summary>
/// App-specific configuration over the generic <see cref="McpHost" /> in
/// JeekTools: object-graph roots (App/Desktop/MainWindow/MainVm), '#Name'
/// visual-tree lookup, the Avalonia tools (visual_tree, screenshot), and the
/// optimization-item probe. Compiled into all configurations so Debug and
/// Release behave identically, but the listener only starts in Debug builds.
/// Agents reach it through the repo-root <c>JeekWindowsOptimizerDebugMcp.cmd</c>
/// (or the fixed per-user adapter with <c>--surface debug --app</c>), which
/// forwards stdio to this instance's named pipe — the pipe name carries the
/// worktree's instance id, so parallel Debug builds never answer for each other
/// and there is no port to collide over.
/// </summary>
internal static class DebugMcpServer
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(DebugMcpServer));

    // Runtime gate instead of #if DEBUG around the whole file: the code
    // compiles in every configuration, only Debug builds actually listen.
    private static readonly bool ListeningEnabled =
#if DEBUG
        true;
#else
        false;
#endif

    private static readonly ObjectGraph Graph = new(new ObjectGraphOptions
    {
        ResolveRoot = ResolveRoot,
        RootNamesHelp = "App, Desktop, MainWindow, MainVm",
        FindNamedChild = (target, name) => target is Visual visual
            ? FindDescendantByName(visual, name)
            : throw new InvalidOperationException(
                $"'#{name}' requires a Visual; {target.GetType().Name} is not one."),
    });

    private static readonly McpHost Host = CreateHost();

    public static void Start()
    {
        Host.Start();
        if (Host.PipeName.Length > 0)
            Log.ZLogInformation($@"Debug MCP listening on \\.\pipe\{Host.PipeName}");
    }

    public static void Stop()
    {
        Host.Stop();
    }

    private static McpHost CreateHost()
    {
        var host = new McpHost(new McpHostOptions
        {
            ServerName = "jeek-windows-optimizer-debug",
            ServerTitle = "JeekWindowsOptimizer Debug Server",
            Graph = Graph,
            GetVersion = () => $"{AutoUpdate.GetLocalCommitCount()}",
            Enabled = ListeningEnabled,
            // Named pipe only: no port to collide over between worktree instances.
            PipeName = McpPipeNames.Debug(McpPipeNames.InstanceId(AppContext.BaseDirectory)),
            DefaultPort = 0,
            UiInvoker = func => Dispatcher.UIThread.InvokeAsync(func).GetTask()
                .WaitAsync(TimeSpan.FromSeconds(15)),
            Describe = BuildDescribeText,
            ToolListProvider = DebugMcpContract.BuildToolList,
        });

        host.AddTool("visual_tree", VisualTreeAsync);
        host.AddTool("screenshot", _ => ScreenshotAsync());
        host.AddTool("defender_status", DefenderStatusAsync);
        host.AddTool("optimization_items", OptimizationItemsAsync);
        host.AddTool("time_sync_status", TimeSyncStatusAsync);
        host.AddTool("disk_space_items", _ => DiskSpaceItemsAsync());
        host.AddTool("disk_space_scan", DiskSpaceScanAsync);
        host.AddTool("disk_space_clean", DiskSpaceCleanAsync);
        host.AddTool("disk_space_relocation_check", DiskSpaceRelocationCheckAsync);
        host.AddTool("disk_space_relocate", DiskSpaceRelocateAsync);
        return host;
    }

    private static Task<T> OnUiAsync<T>(Func<T> func) => Host.OnUiAsync(func);

    private static JsonObject ToolText(string text, bool isError = false) =>
        McpHost.ToolText(text, isError);

    #region Roots

    private static IClassicDesktopStyleApplicationLifetime? Desktop =>
        Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    private static object ResolveRoot(string name) => name switch
    {
        "App" => Application.Current
                 ?? throw new InvalidOperationException("Application.Current is null."),
        "Desktop" => Desktop
                     ?? throw new InvalidOperationException("No desktop lifetime."),
        "MainWindow" => Desktop?.MainWindow
                        ?? throw new InvalidOperationException("MainWindow is not created yet."),
        "MainVm" => Desktop?.MainWindow?.DataContext
                    ?? throw new InvalidOperationException("MainWindow.DataContext is not set yet."),
        _ => throw new InvalidOperationException(
            $"Unknown root '{name}'. Available roots: App, Desktop, MainWindow, MainVm."),
    };

    private static Visual? FindDescendantByName(Visual root, string name)
    {
        var queue = new Queue<Visual>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var visual = queue.Dequeue();
            if (visual is StyledElement styled && styled.Name == name)
                return visual;
            foreach (var child in visual.GetVisualChildren())
                queue.Enqueue(child);
        }

        return null;
    }

    #endregion

    #region Describe

    private static string BuildDescribeText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $"JeekWindowsOptimizer debug MCP server (build {AutoUpdate.GetLocalCommitCount()}).");
        sb.AppendLine($"ProcessId: {Environment.ProcessId}");
        sb.AppendLine($"ExecutablePath: {Environment.ProcessPath}");
        sb.AppendLine($@"Pipe: \\.\pipe\{Host.PipeName}");
        sb.AppendLine($"Process uptime: {DateTime.Now - Process.GetCurrentProcess().StartTime:hh\\:mm\\:ss}.");
        sb.AppendLine($"Log file: {LogManager.CurrentRollingLogFile}");
        sb.AppendLine();
        sb.AppendLine("Roots for object paths:");
        sb.AppendLine("- App: the Avalonia Application instance");
        sb.AppendLine("- Desktop: the IClassicDesktopStyleApplicationLifetime (Windows list, Shutdown, ...)");
        sb.AppendLine("- MainWindow: the main window");
        sb.AppendLine("- MainVm: MainWindow.DataContext (MainViewModel)");
        sb.AppendLine();
        sb.AppendLine(DebugMcpContract.PathHelp);
        sb.AppendLine();

        if (Desktop is not { } desktop)
        {
            sb.AppendLine("No desktop lifetime yet.");
        }
        else
        {
            sb.AppendLine($"Windows ({desktop.Windows.Count}):");
            foreach (var window in desktop.Windows)
            {
                sb.AppendLine(
                    $"- {window.GetType().Name} \"{window.Title}\" Visible={window.IsVisible} "
                    + $"State={window.WindowState} ClientSize={window.ClientSize} "
                    + $"DataContext={window.DataContext?.GetType().Name ?? "null"}");
            }
        }

        return sb.ToString();
    }

    #endregion

    #region Avalonia tools

    private const int MaxVisualNodes = 2000;

    private static async Task<JsonObject> VisualTreeAsync(JsonObject args)
    {
        var path = args["path"]?.GetValue<string>() ?? "MainWindow";
        var maxDepth = Math.Max(1, args["max_depth"]?.GetValue<int>() ?? 12);

        var text = await OnUiAsync(() =>
        {
            if (Graph.Resolve(path) is not Visual root)
                throw new InvalidOperationException($"'{path}' is not a Visual.");

            var sb = new StringBuilder();
            var count = 0;
            AppendVisual(sb, root, 0, maxDepth, null, ref count);
            if (count >= MaxVisualNodes)
                sb.AppendLine($"… truncated at {MaxVisualNodes} nodes.");
            return sb.ToString();
        });

        return ToolText(text);
    }

    private static void AppendVisual(
        StringBuilder sb, Visual visual, int depth, int maxDepth, object? parentDataContext, ref int count)
    {
        if (count >= MaxVisualNodes)
            return;
        count++;

        sb.Append(' ', depth * 2).Append(visual.GetType().Name);

        var dataContext = parentDataContext;
        if (visual is StyledElement styled)
        {
            if (!string.IsNullOrEmpty(styled.Name))
                sb.Append(" #").Append(styled.Name);
            var classes = string.Join(' ', styled.Classes);
            if (classes.Length > 0)
                sb.Append(" (").Append(classes).Append(')');
            dataContext = styled.DataContext;
            if (dataContext != null && !ReferenceEquals(dataContext, parentDataContext))
                sb.Append(" DataContext=").Append(dataContext.GetType().Name);
        }

        var bounds = visual.Bounds;
        sb.Append($" [{bounds.X:0},{bounds.Y:0} {bounds.Width:0}x{bounds.Height:0}]");
        if (!visual.IsVisible)
            sb.Append(" HIDDEN");

        switch (visual)
        {
            case TextBlock { Text.Length: > 0 } textBlock:
                sb.Append($" Text=\"{ObjectGraph.Truncate(textBlock.Text, 80)}\"");
                break;
            case TextBox { Text.Length: > 0 } textBox:
                sb.Append($" Text=\"{ObjectGraph.Truncate(textBox.Text, 80)}\"");
                break;
        }

        sb.AppendLine();

        if (depth >= maxDepth)
        {
            if (visual.GetVisualChildren().Any())
                sb.Append(' ', (depth + 1) * 2).AppendLine("…");
            return;
        }

        foreach (var child in visual.GetVisualChildren())
            AppendVisual(sb, child, depth + 1, maxDepth, dataContext, ref count);
    }

    private static async Task<JsonObject> ScreenshotAsync()
    {
        var (bytes, pixelSize) = await OnUiAsync(() =>
        {
            var window = Desktop?.MainWindow
                         ?? throw new InvalidOperationException("MainWindow is not created yet.");
            var scaling = window.RenderScaling;
            var size = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(window.ClientSize.Width * scaling)),
                Math.Max(1, (int)Math.Ceiling(window.ClientSize.Height * scaling)));

            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(window);
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            return (stream.ToArray(), size);
        });

        return new JsonObject
        {
            ["content"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"Main window screenshot, {pixelSize.Width}x{pixelSize.Height}px.",
                },
                new JsonObject
                {
                    ["type"] = "image",
                    ["data"] = Convert.ToBase64String(bytes),
                    ["mimeType"] = "image/png",
                }),
        };
    }

    #endregion

    #region App probe tools

    private static async Task<JsonObject> DefenderStatusAsync(JsonObject args)
    {
        var status = await DefenderProtection.GetTamperProtectionStatus();
        var hasThirdPartyAntivirus = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            AntiVirus.HasThirdPartyAntivirusInstalled
        );

        var runtimeValue = status.RuntimeIsEnabled?.ToString() ?? "unavailable";
        return ToolText(
            $"runtimeIsTamperProtected={runtimeValue}\n"
            + $"registryTamperProtection={status.RegistryValue}\n"
            + $"effectiveTamperProtectionOff={status.IsOff}\n"
            + $"detectionSource={status.DetectionSource}\n"
            + $"hasThirdPartyAntivirus={hasThirdPartyAntivirus}"
        );
    }

    private static async Task<JsonObject> OptimizationItemsAsync(JsonObject args)
    {
        var categoryFilter = args["category"]?.GetValue<string>();
        var onlyNotOptimized = args["only_not_optimized"]?.GetValue<bool>() ?? false;

        var text = await OnUiAsync(() =>
        {
            if (Desktop?.MainWindow?.DataContext is not MainViewModel vm)
                return "MainViewModel is not available yet.";

            var categories = new (string Name, List<OptimizationGroup> Groups)[]
            {
                ("Optimizing", vm.OptimizingGroups),
                ("Antivirus", vm.AntivirusGroups),
                ("Personal", vm.PersonalGroups),
            };

            var sb = new StringBuilder();
            foreach (var (name, groups) in categories)
            {
                if (categoryFilter is { Length: > 0 }
                    && !string.Equals(name, categoryFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var group in groups)
                {
                    var items = onlyNotOptimized
                        ? group.Items.Where(item => !item.IsOptimized).ToList()
                        : [.. group.Items];
                    if (items.Count == 0)
                        continue;

                    sb.AppendLine($"[{name}] {group.NameKey} ({items.Count})");
                    foreach (var item in items)
                        sb.AppendLine(
                            $"  optimized={item.IsOptimized} checked={item.IsChecked} "
                            + $"{item.NameKey}: {item.Name}");
                }
            }

            if (sb.Length == 0)
                sb.AppendLine("No matching optimization items.");
            return sb.ToString();
        });

        return ToolText(text);
    }

    private static async Task<JsonObject> TimeSyncStatusAsync(JsonObject args)
    {
        var status = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            WindowsTimeSynchronization.GetStatus
        );

        var itemText = await OnUiAsync(() =>
        {
            if (Desktop?.MainWindow?.DataContext is not MainViewModel vm)
                return "item=unavailable (MainViewModel is not available yet.)";

            for (var groupIndex = 0; groupIndex < vm.OptimizingGroups.Count; groupIndex++)
            {
                var group = vm.OptimizingGroups[groupIndex];
                for (var itemIndex = 0; itemIndex < group.Items.Count; itemIndex++)
                {
                    var item = group.Items[itemIndex];
                    if (item.NameKey != "EnableTimeSynchronizationName")
                        continue;

                    return $"itemNameKey={item.NameKey}\n"
                        + $"itemName={item.Name}\n"
                        + $"itemIsOptimized={item.IsOptimized}\n"
                        + $"itemIsChecked={item.IsChecked}\n"
                        + $"itemPath=MainVm.OptimizingGroups[{groupIndex}].Items[{itemIndex}]";
                }
            }

            return "item=not registered";
        });

        return ToolText(
            $"serviceExists={status.ServiceExists}\n"
            + $"serviceStartMode={status.ServiceStartMode}\n"
            + $"triggerCount={status.TriggerCount}\n"
            + $"type={status.Type}\n"
            + $"ntpClientEnabled={status.NtpClientEnabled}\n"
            + $"isEnabled={status.IsEnabled}\n"
            + itemText
        );
    }

    #endregion

    #region Disk space tools

    private static MainViewModel RequireMainVm()
    {
        return Desktop?.MainWindow?.DataContext as MainViewModel
            ?? throw new InvalidOperationException("MainViewModel is not available yet.");
    }

    private static string DescribeDiskSpaceItems(MainViewModel vm)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"systemDriveUsage={vm.SystemDriveUsageText}");
        sb.AppendLine($"summary={vm.DiskSpaceSummaryText}");
        sb.AppendLine($"busy={vm.IsDiskSpaceBusy}");

        for (var groupIndex = 0; groupIndex < vm.AllDiskSpaceGroups.Count; groupIndex++)
        {
            var group = vm.AllDiskSpaceGroups[groupIndex];
            sb.AppendLine($"[{group.NameKey}] {group.Name} ({group.Items.Count})");
            for (var itemIndex = 0; itemIndex < group.Items.Count; itemIndex++)
            {
                var item = group.Items[itemIndex];
                sb.Append($"  {item.NameKey}: state={item.State} size={item.SizeText}");
                if (item.SizeBytes is { } bytes)
                    sb.Append($" bytes={bytes}");
                switch (item)
                {
                    case DiskSpaceCleanupItem cleanup:
                        sb.Append($" checked={cleanup.IsChecked} slow={cleanup.IsSlow}");
                        if (cleanup.FreedBytes > 0)
                            sb.Append($" freed={cleanup.FreedBytes}");
                        break;
                    case DiskSpaceRelocationItem relocation:
                        sb.Append($" onSystemDrive={relocation.IsOnSystemDrive}");
                        sb.Append($" location=\"{relocation.CurrentLocation}\"");
                        sb.Append($" drives=[{string.Join(", ", relocation.TargetDrives.Select(d => d.Letter))}]");
                        if (relocation.SelectedTargetDrive is { } drive)
                            sb.Append($" target=\"{relocation.GetTargetPath(drive)}\"");
                        sb.Append($" canMove={relocation.CanMove}");
                        break;
                }
                if (!string.IsNullOrEmpty(item.ErrorMessage))
                    sb.Append($" error=\"{item.ErrorMessage}\"");
                if (item.HasStatusText)
                    sb.Append($" status=\"{item.StatusText}\"");
                sb.Append($" path=MainVm.AllDiskSpaceGroups[{groupIndex}].Items[{itemIndex}]");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static async Task<JsonObject> DiskSpaceItemsAsync()
    {
        var text = await OnUiAsync(() =>
        {
            var vm = RequireMainVm();
            vm.EnsureDiskSpaceItems();
            return DescribeDiskSpaceItems(vm);
        });
        return ToolText(text);
    }

    private static async Task<JsonObject> DiskSpaceScanAsync(JsonObject args)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(args["timeout_seconds"]?.GetValue<int>() ?? 600, 1, 3600));

        // Start on the UI thread, wait off it: the UI invoker has its own short timeout.
        var scan = await OnUiAsync(() => RequireMainVm().ScanDiskSpaceAsync());
        var timedOut = await Task.WhenAny(scan, Task.Delay(timeout)) != scan;

        var text = await OnUiAsync(() => DescribeDiskSpaceItems(RequireMainVm()));
        return ToolText((timedOut ? "TIMED OUT waiting for the scan; it keeps running.\n" : "") + text, timedOut);
    }

    private static async Task<JsonObject> DiskSpaceCleanAsync(JsonObject args)
    {
        var keys = args["items"] is JsonArray array
            ? array.Select(node => node?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        if (keys.Count == 0)
            return ToolText("'items' must list at least one cleanup item NameKey.", isError: true);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(args["timeout_seconds"]?.GetValue<int>() ?? 1800, 1, 7200));

        var (clean, missing) = await OnUiAsync(() =>
        {
            var vm = RequireMainVm();
            vm.EnsureDiskSpaceItems();
            var items = vm.DiskSpaceCleanupItems.Where(item => keys.Contains(item.NameKey)).ToList();
            var unknown = keys.Except(items.Select(i => i.NameKey), StringComparer.OrdinalIgnoreCase).ToList();
            return (vm.CleanDiskSpaceItemsAsync(items, confirm: false), unknown);
        });

        var timedOut = await Task.WhenAny(clean, Task.Delay(timeout)) != clean;
        var freed = timedOut ? -1 : await clean;

        var text = await OnUiAsync(() => DescribeDiskSpaceItems(RequireMainVm()));
        var header = new StringBuilder();
        if (missing.Count > 0)
            header.AppendLine($"unknownItems={string.Join(", ", missing)}");
        header.AppendLine(timedOut ? "TIMED OUT waiting for the cleanup; it keeps running." : $"freedBytes={freed}");
        return ToolText(header + text, timedOut);
    }

    private static async Task<(DiskSpaceRelocationItem Item, DriveOption? Drive)> ResolveRelocationTargetAsync(
        string key, string? driveArg)
    {
        return await OnUiAsync(() =>
        {
            var vm = RequireMainVm();
            vm.EnsureDiskSpaceItems();
            var found = vm.DiskSpaceRelocationItems.FirstOrDefault(i =>
                string.Equals(i.NameKey, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Unknown relocation item '{key}'.");

            DriveOption? target;
            if (string.IsNullOrWhiteSpace(driveArg))
            {
                target = found.SelectedTargetDrive;
            }
            else
            {
                var letter = driveArg.Trim().TrimEnd('\\', '/', ':').ToUpperInvariant();
                target = found.TargetDrives.FirstOrDefault(d =>
                        d.Letter.TrimEnd(':').Equals(letter, StringComparison.OrdinalIgnoreCase))
                    ?? DiskSpaceItemManager.GetTargetDrives().FirstOrDefault(d =>
                        d.Letter.TrimEnd(':').Equals(letter, StringComparison.OrdinalIgnoreCase));
            }

            return (found, target);
        });
    }

    private static async Task<JsonObject> DiskSpaceRelocationCheckAsync(JsonObject args)
    {
        var key = McpHost.RequiredString(args, "item");
        var (item, drive) = await ResolveRelocationTargetAsync(key, args["drive"]?.GetValue<string>());

        if (drive is null)
            return ToolText("No target drive: pass 'drive' or scan first so the item has a selected drive.", isError: true);

        var targetPath = await OnUiAsync(() => item.GetTargetPath(drive));
        var (ok, error) = await OnUiAsync(() => item.CheckAsync(drive)).Unwrap();

        return ToolText(
            $"item={item.NameKey}\n"
            + $"currentLocation={item.CurrentLocation}\n"
            + $"onSystemDrive={item.IsOnSystemDrive}\n"
            + $"drive={drive.Letter}\n"
            + $"targetPath={targetPath}\n"
            + $"requiresReboot={item.RequiresReboot}\n"
            + $"validationPassed={ok}\n"
            + $"validationError={error ?? ""}"
        );
    }

    private static async Task<JsonObject> DiskSpaceRelocateAsync(JsonObject args)
    {
        var key = McpHost.RequiredString(args, "item");
        var targetPath = args["target_path"]?.GetValue<string>();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(args["timeout_seconds"]?.GetValue<int>() ?? 1800, 1, 7200));

        var (item, drive) = await ResolveRelocationTargetAsync(key, args["drive"]?.GetValue<string>());

        Task<(bool Succeeded, string? Error)> work;
        string describedTarget;
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            if (item is not UserFolderRelocationItem folder)
                return ToolText("'target_path' is only supported for user folder items.", isError: true);
            describedTarget = targetPath;
            work = OnUiAsync(() => folder.RedirectToAsync(targetPath)).Unwrap();
        }
        else
        {
            if (drive is null)
                return ToolText("No target drive: pass 'drive' or 'target_path', or scan first.", isError: true);
            describedTarget = await OnUiAsync(() => item.GetTargetPath(drive));
            work = OnUiAsync(async () =>
            {
                var ok = await RequireMainVm().MoveDiskSpaceItemAsync(item, drive, confirm: false);
                return (ok, ok ? null : item.ErrorMessage);
            }).Unwrap();
        }

        var timedOut = await Task.WhenAny(work, Task.Delay(timeout)) != work;
        var (succeeded, error) = timedOut ? (false, "TIMED OUT; the operation keeps running.") : await work;

        // Re-read so the report shows where the folder is now.
        if (!timedOut)
            await OnUiAsync(() => item.RefreshAsync()).Unwrap();

        var state = await OnUiAsync(() =>
            $"item={item.NameKey}\n"
            + $"target={describedTarget}\n"
            + $"succeeded={succeeded}\n"
            + $"error={error ?? ""}\n"
            + $"currentLocation={item.CurrentLocation}\n"
            + $"onSystemDrive={item.IsOnSystemDrive}\n"
            + $"state={item.State}\n"
            + $"status={item.StatusText}");
        return ToolText(state, !succeeded);
    }

    #endregion
}
