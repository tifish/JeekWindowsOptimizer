namespace JeekWindowsOptimizer;

public class ScheduledTaskItem : OptimizationItem
{
    public override string GroupNameKey { get; }
    public override string NameKey { get; }
    public override string DescriptionKey { get; }

    private readonly List<TaskEntry> _tasks = [];

    public ScheduledTaskItem(
        string groupNameKey,
        string nameKey,
        string descriptionKey,
        OptimizationItemCategory category
    )
    {
        GroupNameKey = groupNameKey;
        NameKey = nameKey;
        DescriptionKey = descriptionKey;
        Category = category;
    }

    public void AddTask(string taskPath, bool defaultEnabled)
    {
        _tasks.Add(new TaskEntry(taskPath, defaultEnabled));
    }

    /// <summary>
    /// Returns true if at least one configured task exists on this machine.
    /// </summary>
    public Task<bool> AnyTaskExists()
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () => _tasks.Any(t => WindowsScheduledTask.Exists(t.TaskPath))
        );
    }

    public override async Task Initialize()
    {
        await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () =>
            {
                var manageable = GetManageableTasks();
                if (manageable.Count == 0)
                {
                    // Only missing/protected tasks: treat as already optimized so the item
                    // does not stick as permanently "not optimized".
                    IsOptimized = _tasks.Any(t => WindowsScheduledTask.Exists(t.TaskPath));
                    return;
                }

                foreach (var task in manageable)
                {
                    // Prefer live state as restore target when the task is currently enabled.
                    if (WindowsScheduledTask.IsEnabled(task.TaskPath))
                        task.RestoreEnabled = true;
                    else if (!task.RestoreEnabledKnown)
                        task.RestoreEnabled = task.DefaultEnabled;
                    task.RestoreEnabledKnown = true;
                }

                IsOptimized = manageable.All(t => WindowsScheduledTask.IsDisabled(t.TaskPath));
            }
        );
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () =>
            {
                var manageable = GetManageableTasks();
                if (manageable.Count == 0)
                    return _tasks.Any(t => WindowsScheduledTask.Exists(t.TaskPath));

                var ok = true;
                foreach (var task in manageable)
                {
                    if (value)
                    {
                        if (WindowsScheduledTask.IsEnabled(task.TaskPath))
                        {
                            task.RestoreEnabled = true;
                            task.RestoreEnabledKnown = true;
                        }

                        var result = WindowsScheduledTask.TrySetEnabled(
                            task.TaskPath,
                            enabled: false
                        );
                        if (result == WindowsScheduledTask.SetEnabledResult.AccessDenied)
                        {
                            task.Unmanageable = true;
                            continue;
                        }

                        if (result != WindowsScheduledTask.SetEnabledResult.Success)
                            ok = false;
                    }
                    else
                    {
                        var restore = task.RestoreEnabledKnown
                            ? task.RestoreEnabled
                            : task.DefaultEnabled;
                        var result = WindowsScheduledTask.TrySetEnabled(task.TaskPath, restore);
                        if (result == WindowsScheduledTask.SetEnabledResult.AccessDenied)
                        {
                            task.Unmanageable = true;
                            continue;
                        }

                        if (result != WindowsScheduledTask.SetEnabledResult.Success)
                            ok = false;
                    }
                }

                // Re-evaluate after possible Unmanageable markings.
                manageable = GetManageableTasks();
                if (manageable.Count == 0)
                    return ok;

                return ok
                    && manageable.All(t =>
                        value
                            ? WindowsScheduledTask.IsDisabled(t.TaskPath)
                            : WindowsScheduledTask.IsEnabled(t.TaskPath)
                                == (
                                    t.RestoreEnabledKnown ? t.RestoreEnabled : t.DefaultEnabled
                                )
                    );
            }
        );
    }

    private List<TaskEntry> GetManageableTasks()
    {
        var list = new List<TaskEntry>();
        foreach (var task in _tasks)
        {
            if (task.Unmanageable)
                continue;
            if (!WindowsScheduledTask.Exists(task.TaskPath))
                continue;

            // Some system tasks (e.g. SdbinstMergeDbTask) deny writes even to admins.
            if (!WindowsScheduledTask.CanModify(task.TaskPath))
            {
                task.Unmanageable = true;
                continue;
            }

            list.Add(task);
        }

        return list;
    }

    private sealed class TaskEntry(string taskPath, bool defaultEnabled)
    {
        public string TaskPath { get; } = taskPath;
        public bool DefaultEnabled { get; } = defaultEnabled;
        public bool RestoreEnabled { get; set; } = defaultEnabled;
        public bool RestoreEnabledKnown { get; set; }
        public bool Unmanageable { get; set; }
    }
}
