using JeekTools;
using JeekWindowsOptimizer.Startup;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

/// <summary>
///     Walks the Task Scheduler tree and reports the tasks that run at logon or boot.
///     <para>
///         Only startup-shaped triggers are listed. A task that runs every Tuesday afternoon is a
///         scheduled job, not a startup item, and pulling all several hundred of them in would bury
///         the entries this tab exists to show.
///     </para>
/// </summary>
public static class ScheduledTaskScanner
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(ScheduledTaskScanner));

    // TASK_TRIGGER_TYPE2 values that mean "runs at startup".
    private const int TriggerRegistration = 7;
    private const int TriggerBoot = 8;
    private const int TriggerLogon = 9;
    private const int TriggerSessionStateChange = 11;

    // TASK_ACTION_TYPE values.
    private const int ActionExec = 0;
    private const int ActionComHandler = 5;

    public static List<RawStartupEntry> Scan()
    {
        var entries = new List<RawStartupEntry>();
        object? service = null;

        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType is null)
                return entries;

            service = Activator.CreateInstance(serviceType);
            if (service is null)
                return entries;

            WindowsScheduledTask.InvokeCom(service, "Connect");

            var root = WindowsScheduledTask.InvokeCom(service, "GetFolder", "\\");
            if (root is null)
                return entries;

            WalkFolder(root, entries, depth: 0);
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to enumerate scheduled tasks");
        }
        finally
        {
            WindowsScheduledTask.ReleaseCom(service);
        }

        return entries;
    }

    private static void WalkFolder(object folder, List<RawStartupEntry> entries, int depth)
    {
        // The tree is shallow in practice; the cap is only a guard against a pathological loop.
        if (depth > 12)
            return;

        try
        {
            // GetTasks(1) includes tasks that are hidden from the Task Scheduler UI. Persistence
            // very often sets the hidden flag, so leaving them out would defeat the purpose.
            if (WindowsScheduledTask.InvokeCom(folder, "GetTasks", 1) is { } tasks)
            {
                try
                {
                    var count = WindowsScheduledTask.GetComProperty<int>(tasks, "Count");
                    for (var i = 1; i <= count; i++)
                    {
                        var task = WindowsScheduledTask.GetComIndexed(tasks, "Item", i);
                        if (task is null)
                            continue;

                        try
                        {
                            if (ReadTask(task) is { } entry)
                                entries.Add(entry);
                        }
                        finally
                        {
                            WindowsScheduledTask.ReleaseCom(task);
                        }
                    }
                }
                finally
                {
                    WindowsScheduledTask.ReleaseCom(tasks);
                }
            }
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to read tasks in a scheduled task folder");
        }

        try
        {
            if (WindowsScheduledTask.InvokeCom(folder, "GetFolders", 0) is not { } folders)
                return;

            try
            {
                var count = WindowsScheduledTask.GetComProperty<int>(folders, "Count");
                for (var i = 1; i <= count; i++)
                {
                    var child = WindowsScheduledTask.GetComIndexed(folders, "Item", i);
                    if (child is null)
                        continue;

                    try
                    {
                        WalkFolder(child, entries, depth + 1);
                    }
                    finally
                    {
                        WindowsScheduledTask.ReleaseCom(child);
                    }
                }
            }
            finally
            {
                WindowsScheduledTask.ReleaseCom(folders);
            }
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to enumerate scheduled task subfolders");
        }
    }

    private static RawStartupEntry? ReadTask(object task)
    {
        var path = "";

        try
        {
            path = WindowsScheduledTask.GetComProperty<string>(task, "Path");
            var enabled = WindowsScheduledTask.GetComProperty<bool>(task, "Enabled");

            var definition = WindowsScheduledTask.GetComProperty<object>(task, "Definition");
            if (definition is null)
                return null;

            try
            {
                if (!HasStartupTrigger(definition))
                    return null;

                var (command, imagePath) = ReadPrimaryAction(definition);
                var canModify = WindowsScheduledTask.CanModify(path);

                return new RawStartupEntry
                {
                    Kind = StartupItemKind.ScheduledTask,
                    Name = path,
                    Location = "Task Scheduler",
                    Command = command,
                    ImagePath = imagePath,
                    IsEnabled = enabled,
                    CanToggle = canModify,
                    ReadOnlyReasonKey = canModify ? null : "StartupStatusTaskProtected",
                    RestoreState = path,
                };
            }
            finally
            {
                WindowsScheduledTask.ReleaseCom(definition);
            }
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to read scheduled task {path}");
            return null;
        }
    }

    /// <summary>
    ///     Combines a relative program name with the task's working directory, which is the only
    ///     place that says where a bare name such as <c>FanControl.exe</c> actually lives.
    /// </summary>
    private static string ResolveAgainstWorkingDirectory(string image, string workingDirectory)
    {
        try
        {
            if (image.Length == 0 || workingDirectory.Length == 0)
                return image;

            var expandedImage = Environment.ExpandEnvironmentVariables(image.Trim('"'));
            if (Path.IsPathRooted(expandedImage))
                return image;

            var expandedDirectory = Environment.ExpandEnvironmentVariables(workingDirectory.Trim('"'));
            var combined = Path.Combine(expandedDirectory, expandedImage);
            return File.Exists(combined) ? combined : image;
        }
        catch
        {
            return image;
        }
    }

    private static bool HasStartupTrigger(object definition)
    {
        var triggers = WindowsScheduledTask.GetComProperty<object>(definition, "Triggers");
        if (triggers is null)
            return false;

        try
        {
            var count = WindowsScheduledTask.GetComProperty<int>(triggers, "Count");
            for (var i = 1; i <= count; i++)
            {
                var trigger = WindowsScheduledTask.GetComIndexed(triggers, "Item", i);
                if (trigger is null)
                    continue;

                try
                {
                    var type = WindowsScheduledTask.GetComProperty<int>(trigger, "Type");
                    if (type is TriggerRegistration or TriggerBoot or TriggerLogon
                        or TriggerSessionStateChange)
                        return true;
                }
                finally
                {
                    WindowsScheduledTask.ReleaseCom(trigger);
                }
            }
        }
        catch
        {
            // A task whose triggers cannot be read is not treated as a startup item.
        }
        finally
        {
            WindowsScheduledTask.ReleaseCom(triggers);
        }

        return false;
    }

    /// <summary>
    ///     The command and the file a task actually runs.
    ///     <para>
    ///         Most of Windows' own tasks run a COM handler rather than an executable. Reporting no
    ///         file for those makes them unattributable, so they cannot be recognised as Windows
    ///         components and they pile up in the list as "unsigned". Resolving the handler's class
    ///         id to its server gives them a real publisher.
    ///     </para>
    /// </summary>
    private static (string Command, string ImagePath) ReadPrimaryAction(object definition)
    {
        var actions = WindowsScheduledTask.GetComProperty<object>(definition, "Actions");
        if (actions is null)
            return ("", "");

        try
        {
            var count = WindowsScheduledTask.GetComProperty<int>(actions, "Count");

            // An executable action is the more informative one, so prefer it over a handler.
            for (var pass = 0; pass < 2; pass++)
            {
                var wanted = pass == 0 ? ActionExec : ActionComHandler;

                for (var i = 1; i <= count; i++)
                {
                    var action = WindowsScheduledTask.GetComIndexed(actions, "Item", i);
                    if (action is null)
                        continue;

                    try
                    {
                        if (WindowsScheduledTask.GetComProperty<int>(action, "Type") != wanted)
                            continue;

                        if (wanted == ActionExec)
                        {
                            var path = WindowsScheduledTask.GetComProperty<string>(action, "Path") ?? "";
                            var arguments =
                                WindowsScheduledTask.GetComProperty<string>(action, "Arguments") ?? "";
                            if (path.Length == 0)
                                continue;

                            var command = arguments.Length > 0 ? $"\"{path}\" {arguments}" : path;

                            // Run the whole command through the shared extractor rather than trusting
                            // the action's Path: a task that launches through cmd would otherwise be
                            // recorded as cmd, and every such task would share one identity.
                            var image = StartupIdentity.ExtractImagePath(command);
                            if (image.Length == 0)
                                image = path;

                            // A task can name its program relative to its own working directory.
                            var workingDirectory =
                                WindowsScheduledTask.GetComProperty<string>(action, "WorkingDirectory") ?? "";
                            image = ResolveAgainstWorkingDirectory(image, workingDirectory);

                            return (command, image);
                        }

                        var classId = WindowsScheduledTask.GetComProperty<string>(action, "ClassId");
                        var server = ComServerRegistry.ResolveServer(classId);
                        if (string.IsNullOrEmpty(server))
                            continue;

                        var name = ComServerRegistry.ResolveName(classId);
                        var label = string.IsNullOrWhiteSpace(name)
                            ? ComServerRegistry.Normalize(classId)
                            : $"{name} {ComServerRegistry.Normalize(classId)}";
                        return ($"COM: {label}", server);
                    }
                    catch
                    {
                        // A single unreadable action must not lose the rest.
                    }
                    finally
                    {
                        WindowsScheduledTask.ReleaseCom(action);
                    }
                }
            }
        }
        catch
        {
            // E-mail and message actions have no file behind them; the row stays unattributed.
        }
        finally
        {
            WindowsScheduledTask.ReleaseCom(actions);
        }

        return ("", "");
    }
}
