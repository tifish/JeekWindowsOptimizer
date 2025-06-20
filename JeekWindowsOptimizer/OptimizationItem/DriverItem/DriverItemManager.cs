using JeekTools;

namespace JeekWindowsOptimizer;

public static class DriverItemManager
{
    public static List<DriverItem> Items { get; } = [];

    public static async Task Load()
    {
        var tabFile = new TabFile();
        if (!await tabFile.LoadAsync(Path.Join(AppContext.BaseDirectory, @"Data\DriverItems.tab")))
            return;

        var itemsDict = new Dictionary<string, DriverItem>();

        foreach (var row in tabFile.Rows.Skip(1))
        {
            if (row.Count != 4)
                continue;

            var index = -1;

            var groupNameKey = row[++index];
            var nameKey = row[++index] + "Name";
            var descriptionKey = row[index] + "Description";
            if (!Enum.TryParse<OptimizationItemCategory>(row[++index], out var category))
                category = OptimizationItemCategory.Default;
            var driverPathPattern = row[++index];

            if (!itemsDict.TryGetValue(nameKey, out var item))
            {
                item = new DriverItem(groupNameKey, nameKey, descriptionKey)
                {
                    Category = category,
                };
                itemsDict[nameKey] = item;
                Items.Add(item);
            }

            item.DriverPathPatterns.Add(driverPathPattern);
        }
    }
}
