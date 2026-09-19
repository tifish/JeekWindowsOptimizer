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
using JeekWindowsOptimizer.Startup;
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
        host.AddTool("optimization_init_timings", OptimizationInitTimingsAsync);
        host.AddTool("optimization_refresh", async _ =>
        {
            var task = await OnUiAsync(() =>
            {
                var command = RequireMainVm().RefreshOptimizationItemStatesCommand;
                return command.CanExecute(null) ? command.ExecuteAsync(null) : null;
            });
            if (task is null)
                return ToolText("busy/unavailable: wait for the current operation and select an optimization tab.");
            await task;
            return ToolText(await OnUiAsync(() => RequireMainVm().StatusMessage));
        });
        host.AddTool("tool_items", ToolItemsAsync);
        host.AddTool("store_package_probe", async args =>
            ToolText(await MicrosoftStore.Describe(
                args["name"]?.GetValue<string>()
                    ?? throw new ArgumentException("name is required"))));
        host.AddTool("time_sync_status", TimeSyncStatusAsync);
        host.AddTool("service_probe", ServiceProbeAsync);
        host.AddTool("service_delete", ServiceDeleteAsync);
        host.AddTool("driver_remove_probe", DriverRemoveProbeAsync);
        host.AddTool("group_navigation_probe", async _ =>
        {
            var task = await OnUiAsync(() => GroupNavigationProbe.RunAsync(
                (Views.MainWindow)Desktop!.MainWindow!, RequireMainVm()));
            return ToolText(await task);
        });
        host.AddTool("disk_space_items", _ => DiskSpaceItemsAsync());
        host.AddTool("virtual_disk_migration_probe", async _ =>
        {
            var task = await OnUiAsync(VirtualDiskMigrationProbe.RunAsync);
            return ToolText(await task);
        });
        host.AddTool("wsl_native_migration_probe", async _ =>
        {
            var task = await OnUiAsync(VirtualDiskMigrationProbe.RunNativeWslAsync);
            return ToolText(await task);
        });
        host.AddTool("disk_space_cleanup_probe", async args =>
        {
            var task = await OnUiAsync(() => args["scenario"]?.GetValue<string>() == "queue"
                ? DiskSpaceOperationQueueProbe.RunAsync(RequireMainVm())
                : DiskSpaceCleanupProbe.RunAsync(args["scenario"]?.GetValue<string>() ?? "accuracy"));
            return ToolText(await task);
        });
        host.AddTool("startup_items", StartupItemsAsync);
        host.AddTool("startup_scan", StartupScanAsync);
        host.AddTool("startup_decide", StartupDecideAsync);
        host.AddTool("startup_enforce", StartupEnforceAsync);
        host.AddTool("startup_baseline", StartupBaselineAsync);
        host.AddTool("startup_ledger", StartupLedgerAsync);
        host.AddTool("startup_service_probe", async args =>
        {
            var task = await OnUiAsync(() => StartupServiceProbe.RunAsync(
                RequireMainVm(), args["name"]?.GetValue<string>() ?? "W32Time"));
            return ToolText(await task);
        });
        host.AddTool("startup_ledger_probe", async args =>
            ToolText(await StartupLedgerProbe.RunAsync(
                args["scenario"]?.GetValue<string>() ?? "all")));
        host.AddTool("disk_space_scan", DiskSpaceScanAsync);
        host.AddTool("disk_space_clean", DiskSpaceCleanAsync);
        host.AddTool("disk_space_enqueue", DiskSpaceEnqueueAsync);
        host.AddTool("disk_space_relocation_check", DiskSpaceRelocationCheckAsync);
        host.AddTool("disk_space_relocate", DiskSpaceRelocateAsync);
        host.AddTool("disk_space_restore_default", DiskSpaceRestoreDefaultAsync);
        host.AddTool("disk_space_move_checked", DiskSpaceMoveCheckedAsync);
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

    private static async Task<JsonObject> OptimizationInitTimingsAsync(JsonObject args)
    {
        var top = Math.Clamp(args["top"]?.GetValue<int>() ?? 30, 1, 1000);

        var text = await OnUiAsync(() =>
        {
            if (Desktop?.MainWindow?.DataContext is not MainViewModel vm)
                return "MainViewModel is not available yet.";

            var items = vm.OptimizingGroups.Concat(vm.AntivirusGroups).Concat(vm.PersonalGroups)
                .SelectMany(group => group.Items)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"total_ms={vm.ItemsInitializationMilliseconds}");
            sb.AppendLine($"store_snapshot_ms={vm.StorePackageSnapshotMilliseconds} store_wait_ms={vm.StoreSnapshotWaitMilliseconds}");
            sb.AppendLine($"data_load_ms={vm.DataLoadMilliseconds} battery_ms={vm.BatteryCheckMilliseconds} detection_ms={vm.DetectionMilliseconds}");
            sb.AppendLine($"item_count={items.Count} items_sum_ms={items.Sum(item => item.InitializeMilliseconds)}");
            foreach (var item in items.OrderByDescending(item => item.InitializeMilliseconds).Take(top))
                sb.AppendLine($"  {item.InitializeMilliseconds,6} ms  {item.GetType().Name}  {item.NameKey}  optimized={item.IsOptimized}");
            return sb.ToString();
        });

        return ToolText(text);
    }

    private static async Task<JsonObject> ToolItemsAsync(JsonObject args)
    {
        var text = await OnUiAsync(() =>
        {
            if (Desktop?.MainWindow?.DataContext is not MainViewModel vm)
                return "MainViewModel is not available yet.";

            var sb = new StringBuilder();
            foreach (var group in vm.AllToolGroups)
            {
                sb.AppendLine($"[{group.NameKey}]");
                foreach (var item in group.Items)
                    sb.AppendLine($"  {item.NameKey} kind={item.ExecutionKind} available={item.IsAvailable} target={item.Target}");
            }
            return sb.ToString();
        });

        return ToolText(text);
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

    private static async Task<JsonObject> ServiceProbeAsync(JsonObject args)
    {
        var name = args["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
            return ToolText("name is required.", isError: true);

        return await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () =>
            {
                using var service = new WindowsService(name);
                var exists = service.Exists();
                var startMode = "n/a";
                if (exists)
                {
                    try
                    {
                        startMode = service.GetStartMode().ToString();
                    }
                    catch
                    {
                        startMode = "unknown";
                    }
                }
                return ToolText($"name={name}\nexists={exists}\nstartMode={startMode}");
            }
        );
    }

    private static async Task<JsonObject> ServiceDeleteAsync(JsonObject args)
    {
        var name = args["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
            return ToolText("name is required.", isError: true);

        return await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () =>
            {
                using var service = new WindowsService(name);
                var existedBefore = service.Exists();
                var deleted = service.Delete();
                return ToolText(
                    $"name={name}\nexistedBefore={existedBefore}\ndeleted={deleted}\nexistsAfter={service.Exists()}"
                );
            }
        );
    }

    private static async Task<JsonObject> DriverRemoveProbeAsync(JsonObject args)
    {
        var services = (args["services"] as JsonArray)?
            .Select(n => n?.GetValue<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList() ?? [];
        var paths = (args["paths"] as JsonArray)?
            .Select(p => p?.GetValue<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList() ?? [];

        if (services.Count == 0 && paths.Count == 0)
            return ToolText("Provide at least one service name or path.", isError: true);

        var item = new DriverItem("DebugGroup", "DebugDriverName", "DebugDriverDescription");
        item.ServiceNames.AddRange(services);
        item.DriverPathPatterns.AddRange(paths);

        var failures = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            item.RemoveProduct
        );

        var succeeded =
            failures.RemainingServices.Count == 0 && failures.RemainingPaths.Count == 0;

        return ToolText(
            $"succeeded={succeeded}\n"
            + $"remainingServices=[{string.Join(", ", failures.RemainingServices)}]\n"
            + $"remainingPaths=[{string.Join(", ", failures.RemainingPaths)}]"
        );
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
        sb.AppendLine($"queueRunning={vm.OperationQueue.IsRunning} queueCurrent={vm.OperationQueue.CurrentItem?.NameKey} queuePending={vm.OperationQueue.PendingCount} canQueue={vm.CanQueueDiskSpace}");
        sb.AppendLine($"scanning={vm.IsDiskSpaceScanning} canScan={vm.CanScanDiskSpace} canClean={vm.CanCleanDiskSpace}");
        sb.AppendLine($"scanCommandEnabled={vm.ScanDiskSpaceCommand.CanExecute(null)} cleanCommandEnabled={vm.CleanCheckedDiskSpaceItemsCommand.CanExecute(null)} moveCommandEnabled={vm.MoveCheckedDiskSpaceItemsCommand.CanExecute(null)}");

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
                        sb.Append($" checked={cleanup.IsChecked} slow={cleanup.IsSlow} upperBound={cleanup.IsReclaimableUpperBound}");
                        sb.Append($" freedBytesKnown={cleanup.IsFreedBytesKnown}");
                        sb.Append($" queuePosition={cleanup.QueuePosition} canClean={vm.CleanDiskSpaceItemCommand.CanExecute(cleanup)}");
                        if (cleanup.FreedBytes > 0)
                            sb.Append($" freed={cleanup.FreedBytes}");
                        break;
                    case DiskSpaceRelocationItem relocation:
                        sb.Append($" checked={relocation.IsChecked} canCheck={relocation.CanCheck} queuePosition={relocation.QueuePosition}");
                        sb.Append($" onSystemDrive={relocation.IsOnSystemDrive}");
                        sb.Append($" atDefault={relocation.IsAtDefaultLocation} canRestore={relocation.CanRestoreDefault}");
                        sb.Append($" location=\"{relocation.CurrentLocation}\"");
                        sb.Append($" default=\"{relocation.DefaultLocationText}\"");
                        sb.Append($" drives=[{string.Join(", ", relocation.TargetDrives.Select(d => d.Label))}]");
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

    private static Task<JsonObject> DiskSpaceEnqueueAsync(JsonObject args) => OnUiAsync(() =>
    {
        var vm = RequireMainVm();
        vm.EnsureDiskSpaceItems();
        var item = vm.DiskSpaceItems.FirstOrDefault(item => string.Equals(item.NameKey,
            args["item"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase));
        var action = args["action"]?.GetValue<string>();
        Task request;
        if (item is DiskSpaceCleanupItem cleanup && action == "clean"
            && vm.CleanDiskSpaceItemCommand.CanExecute(cleanup))
            request = vm.CleanDiskSpaceItemsAsync([cleanup], confirm: false);
        else if (item is DiskSpaceRelocationItem relocation && action == "move"
            && vm.MoveDiskSpaceItemCommand.CanExecute(relocation))
        {
            var root = args["drive"]?.GetValue<string>()?.TrimEnd('\\', ':');
            var drive = root is null ? relocation.SelectedTargetDrive : relocation.TargetDrives.FirstOrDefault(
                drive => string.Equals(drive.Root.TrimEnd('\\', ':'), root, StringComparison.OrdinalIgnoreCase));
            if (drive is null)
                return ToolText("Unknown or unavailable destination drive.", isError: true);
            request = vm.MoveDiskSpaceItemAsync(relocation, drive, confirm: false);
        }
        else if (item is DiskSpaceRelocationItem restore && action == "restore"
            && vm.RestoreDiskSpaceItemDefaultCommand.CanExecute(restore))
            request = vm.RestoreDiskSpaceItemDefaultAsync(restore, confirm: false);
        else
            return ToolText("Not queued: item/action is unknown, not ready, empty, running or already queued.", isError: true);
        _ = ObserveQueueRequestAsync(request);
        return ToolText("accepted=true\n" + DescribeDiskSpaceItems(vm));
    });

    private static async Task ObserveQueueRequestAsync(Task request)
    {
        try { await request; }
        catch (Exception ex) { Log.ZLogError(ex, $"Queued debug disk operation failed"); }
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

    private static async Task<JsonObject> DiskSpaceRestoreDefaultAsync(JsonObject args)
    {
        var key = McpHost.RequiredString(args, "item");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(args["timeout_seconds"]?.GetValue<int>() ?? 1800, 1, 7200));

        var (item, _) = await ResolveRelocationTargetAsync(key, null);
        var work = OnUiAsync(() => RequireMainVm().RestoreDiskSpaceItemDefaultAsync(item, confirm: false)).Unwrap();
        var timedOut = await Task.WhenAny(work, Task.Delay(timeout)) != work;
        var succeeded = !timedOut && await work;

        var state = await OnUiAsync(() =>
            $"item={item.NameKey}\n"
            + $"default={item.DefaultLocationText}\n"
            + $"succeeded={succeeded}\n"
            + $"error={(timedOut ? "TIMED OUT; the operation keeps running." : item.ErrorMessage ?? "")}\n"
            + $"currentLocation={item.CurrentLocation}\n"
            + $"atDefault={item.IsAtDefaultLocation}\n"
            + $"state={item.State}\n"
            + $"status={item.StatusText}");
        return ToolText(state, !succeeded);
    }

    private static async Task<JsonObject> DiskSpaceMoveCheckedAsync(JsonObject args)
    {
        var keys = args["items"] is JsonArray array
            ? array.Select(node => node?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(args["timeout_seconds"]?.GetValue<int>() ?? 1800, 1, 7200));

        var work = OnUiAsync(() =>
        {
            var vm = RequireMainVm();
            vm.EnsureDiskSpaceItems();
            if (keys is not null)
                foreach (var item in vm.DiskSpaceRelocationItems)
                    item.IsChecked = keys.Contains(item.NameKey) && item.CanCheck;
            return vm.MoveCheckedDiskSpaceItemsAsync(confirm: false);
        }).Unwrap();

        var timedOut = await Task.WhenAny(work, Task.Delay(timeout)) != work;
        var (succeeded, failed) = timedOut ? (0, 0) : await work;

        var text = await OnUiAsync(() => DescribeDiskSpaceItems(RequireMainVm()));
        var header = timedOut
            ? "TIMED OUT waiting for the batch move; it keeps running.\n"
            : $"succeeded={succeeded} failed={failed}\nstatus={await OnUiAsync(() => RequireMainVm().StatusMessage)}\n";
        return ToolText(header + text, timedOut);
    }

    #endregion

    #region Startup

    private static async Task<JsonObject> StartupItemsAsync(JsonObject args)
    {
        var kind = args["kind"]?.GetValue<string>();
        var onlyPending = args["only_pending"]?.GetValue<bool>() ?? false;
        var includeHidden = args["include_hidden"]?.GetValue<bool>() ?? false;
        var limit = Math.Clamp(args["limit"]?.GetValue<int>() ?? 200, 1, 2000);

        // Start the scan on the UI thread but wait off it: the UI invoker has a short timeout.
        var scan = await OnUiAsync(() => RequireMainVm().EnsureStartupItemsAsync());
        var timedOut = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(300))) != scan;

        var text = await OnUiAsync(() =>
            DescribeStartupItems(RequireMainVm(), kind, onlyPending, includeHidden, limit));
        return ToolText(
            (timedOut ? "TIMED OUT waiting for the first scan; it keeps running.\n" : "") + text,
            timedOut);
    }

    private static async Task<JsonObject> StartupScanAsync(JsonObject args)
    {
        var timeout = TimeSpan.FromSeconds(
            Math.Clamp(args["timeout_seconds"]?.GetValue<int>() ?? 300, 1, 3600));

        var scan = await OnUiAsync(() => RequireMainVm().ScanStartupCommand.ExecuteAsync(null));
        var timedOut = await Task.WhenAny(scan, Task.Delay(timeout)) != scan;

        var text = await OnUiAsync(() =>
            DescribeStartupItems(RequireMainVm(), null, false, false, 200));
        return ToolText(
            (timedOut ? "TIMED OUT waiting for the scan; it keeps running.\n" : "") + text,
            timedOut);
    }

    private static async Task<JsonObject> StartupDecideAsync(JsonObject args)
    {
        var key = args["key"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(key))
            return ToolText("'key' is required.", isError: true);

        var decisionText = args["decision"]?.GetValue<string>() ?? "";
        StartupDecision decision;
        switch (decisionText.ToLowerInvariant())
        {
            case "allow":
                decision = StartupDecision.Allow;
                break;
            case "deny":
                decision = StartupDecision.Deny;
                break;
            default:
                return ToolText("'decision' must be allow or deny.", isError: true);
        }

        await (await OnUiAsync(() => RequireMainVm().EnsureStartupItemsAsync()));

        var work = await OnUiAsync(() =>
        {
            var vm = RequireMainVm();
            var item = vm.StartupItems.FirstOrDefault(i =>
                string.Equals(i.Key, key, StringComparison.Ordinal));
            if (item is null)
                return (Task.CompletedTask, (StartupItem?)null);

            var command = decision == StartupDecision.Allow
                ? vm.AllowStartupItemCommand
                : vm.DenyStartupItemCommand;
            return (command.ExecuteAsync(item), item);
        });

        if (work.Item2 is null)
            return ToolText($"No startup item with key '{key}'.", isError: true);

        await work.Item1;

        var text = await OnUiAsync(() => DescribeStartupItem(work.Item2!));
        return ToolText(text);
    }

    private static async Task<JsonObject> StartupEnforceAsync(JsonObject args)
    {
        var timeout = TimeSpan.FromSeconds(
            Math.Clamp(args["timeout_seconds"]?.GetValue<int>() ?? 600, 1, 3600));

        await (await OnUiAsync(() => RequireMainVm().EnsureStartupItemsAsync()));

        var before = await OnUiAsync(() =>
            RequireMainVm().StartupItems.Count(item => item.NeedsEnforcement));

        var work = await OnUiAsync(() => RequireMainVm().EnforceStartupDecisionsForDebugAsync());
        var timedOut = await Task.WhenAny(work, Task.Delay(timeout)) != work;

        var after = await OnUiAsync(() =>
            RequireMainVm().StartupItems.Count(item => item.NeedsEnforcement));
        var text = await OnUiAsync(() =>
            DescribeStartupItems(RequireMainVm(), null, false, false, 200));

        return ToolText(
            (timedOut ? "TIMED OUT; the work keeps running.\n" : "")
                + $"neededEnforcementBefore={before}\nneededEnforcementAfter={after}\n\n"
                + text,
            timedOut);
    }

    private static async Task<JsonObject> StartupBaselineAsync(JsonObject _)
    {
        await (await OnUiAsync(() => RequireMainVm().EnsureStartupItemsAsync()));

        var recorded = await OnUiAsync(() =>
            StartupItemManager.AcceptBaseline(RequireMainVm().StartupItems.ToList()));

        await OnUiAsync(() =>
        {
            RequireMainVm().RefreshStartupAfterLedgerChangeForDebug();
            return true;
        });

        return ToolText(
            $"recorded={recorded}\nledgerEntries={StartupDecisionStore.Count}\n"
            + $"epoch={StartupDecisionStore.Epoch}");
    }

    private static Task<JsonObject> StartupLedgerAsync(JsonObject args)
    {
        var includeEntries = args["entries"]?.GetValue<bool>() ?? false;
        var limit = Math.Clamp(args["limit"]?.GetValue<int>() ?? 100, 1, 2000);

        var sb = new StringBuilder();
        sb.AppendLine($"file={StartupDecisionStore.FilePath}");
        sb.AppendLine($"exists={File.Exists(StartupDecisionStore.FilePath)}");
        sb.AppendLine($"epoch={StartupDecisionStore.Epoch}");
        sb.AppendLine($"entries={StartupDecisionStore.Count}");
        sb.AppendLine($"writesBlocked={StartupDecisionStore.IsPoisoned}");
        if (StartupDecisionStore.PoisonReason is { } reason)
            sb.AppendLine($"blockedReason={reason}");
        sb.AppendLine($"baselineTaken={StartupLocalState.HasBaseline}");

        if (includeEntries)
        {
            sb.AppendLine();
            foreach (var (key, entry) in StartupDecisionStore.Snapshot()
                         .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                         .Take(limit))
            {
                sb.AppendLine(
                    $"{entry.Decision,-5} clock={entry.Clock,-4} on={entry.Machine} "
                    + $"at={entry.DecidedAtUtc:u} key={key}");
            }
        }

        return Task.FromResult(ToolText(sb.ToString()));
    }

    private static string DescribeStartupItems(
        MainViewModel vm,
        string? kind,
        bool onlyPending,
        bool includeHidden,
        int limit)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"scanning={vm.IsStartupScanning} busy={vm.IsStartupBusy}");
        sb.AppendLine($"baselinePending={vm.IsStartupBaselinePending}");
        sb.AppendLine($"ledgerEntries={StartupDecisionStore.Count} epoch={StartupDecisionStore.Epoch} "
            + $"writesBlocked={StartupDecisionStore.IsPoisoned}");
        sb.AppendLine($"summary={vm.StartupSummaryText}");
        if (vm.HasStartupWarning)
            sb.AppendLine($"warning={vm.StartupWarningText}");
        sb.AppendLine($"filters: showWindows={vm.ShowWindowsStartupEntries} "
            + $"hideMicrosoft={vm.HideMicrosoftStartupEntries} "
            + $"onlyPending={vm.ShowOnlyPendingStartupItems} "
            + $"autoEnforce={vm.AutoEnforceStartupDecisions}");
        sb.AppendLine();

        var items = (includeHidden ? vm.StartupItems : vm.VisibleStartupItems).ToList();

        if (!string.IsNullOrWhiteSpace(kind))
            items = items
                .Where(item => string.Equals(item.Kind.ToString(), kind, StringComparison.OrdinalIgnoreCase))
                .ToList();
        if (onlyPending)
            items = items.Where(item => item.IsPending).ToList();

        sb.AppendLine($"total={items.Count}");
        foreach (var group in items.GroupBy(item => item.Kind).OrderBy(g => g.Key))
            sb.AppendLine($"  {group.Key}: {group.Count()} "
                + $"(pending {group.Count(i => i.IsPending)}, "
                + $"needsEnforcement {group.Count(i => i.NeedsEnforcement)})");
        sb.AppendLine();

        foreach (var item in items.Take(limit))
            sb.AppendLine(DescribeStartupItem(item));

        if (items.Count > limit)
            sb.AppendLine($"... {items.Count - limit} more (raise 'limit' to see them)");

        return sb.ToString();
    }

    private static string DescribeStartupItem(StartupItem item)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{item.Kind}] {item.DisplayName}");
        sb.AppendLine($"  name={item.Name}");
        sb.AppendLine($"  key={item.Key}");
        if (item.LooseKey.Length > 0)
            sb.AppendLine($"  looseKey={item.LooseKey}");
        sb.AppendLine($"  location={item.Location}");
        if (item.Command.Length > 0)
            sb.AppendLine($"  command={item.Command}");
        sb.AppendLine($"  signer={item.SignerKind} publisher={item.Publisher ?? "(none)"}");
        sb.AppendLine($"  enabled={item.IsEnabled} canToggle={item.CanToggle} orphaned={item.IsOrphaned}");
        sb.AppendLine($"  decision={(item.Decision?.ToString() ?? "pending")} "
            + $"source={item.DecisionSource} compliant={item.IsCompliant} "
            + $"needsEnforcement={item.NeedsEnforcement}");
        if (item.DecidedOnMachine is { } machine)
            sb.AppendLine($"  decidedOn={machine} at={item.DecidedAtUtc:u}");
        if (item.HookPoints.Count > 0)
            sb.AppendLine($"  hooks({item.HookPoints.Count})={item.HookPointsText}");
        if (item.Handles.Count > 0)
            sb.AppendLine($"  handles={string.Join(", ", item.Handles)}");
        if (item.ErrorMessage is { } error)
            sb.AppendLine($"  error={error}");
        if (item.WarningMessage is { } warning)
            sb.AppendLine($"  warning={warning}");
        sb.AppendLine($"  status={item.StatusText}");
        return sb.ToString();
    }

    #endregion
}
