using JeekTools;

namespace JeekWindowsOptimizer;

public static class ScheduledTaskItemManager
{
    public static List<ScheduledTaskItem> Items { get; } = [];

    public static async Task Load()
    {
        var tabFile = new TabFile();
        if (
            !await tabFile.LoadAsync(
                Path.Join(AppContext.BaseDirectory, @"Data\ScheduledTaskItems.tab")
            )
        )
            return;

        var itemsDict = new Dictionary<string, ScheduledTaskItem>();

        foreach (var row in tabFile.Rows.Skip(1))
        {
            if (row.Count != 5)
                throw new InvalidDataException(
                    $"ScheduledTaskItems.tab: expected 5 columns but got {row.Count} in row: {string.Join('\t', row)}"
                );

            var groupNameKey = row[0];
            var nameStem = row[1];
            var nameKey = nameStem + "Name";
            var descriptionKey = nameStem + "Description";
            if (!Enum.TryParse(row[2], out OptimizationItemCategory category))
                category = OptimizationItemCategory.Default;
            var taskPath = row[3];
            if (string.IsNullOrWhiteSpace(taskPath))
                throw new InvalidDataException(
                    $"ScheduledTaskItems.tab: empty TaskPath for item '{nameStem}'"
                );
            var defaultEnabled = row[4].Equals("true", StringComparison.OrdinalIgnoreCase);

            if (!itemsDict.TryGetValue(nameKey, out var item))
            {
                // Continuation rows leave GroupName empty; first row must supply it.
                if (string.IsNullOrWhiteSpace(groupNameKey))
                    throw new InvalidDataException(
                        $"ScheduledTaskItems.tab: first row for '{nameStem}' must include GroupName"
                    );

                item = new ScheduledTaskItem(
                    groupNameKey,
                    nameKey,
                    descriptionKey,
                    category
                );
                itemsDict[nameKey] = item;
            }

            item.AddTask(taskPath, defaultEnabled);
        }

        foreach (var item in itemsDict.Values)
        {
            // Skip items whose tasks are not present on this machine.
            if (!await item.AnyTaskExists())
                continue;

            Items.Add(item);
        }
    }
}
