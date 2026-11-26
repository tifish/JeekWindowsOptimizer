using JeekTools;

namespace JeekWindowsOptimizer;

public static class MicrosoftStoreItemManager
{
    public static List<MicrosoftStoreItem> Items { get; private set; } = [];

    public static async Task Load()
    {
        var tabFile = new TabFile();
        if (
            !await tabFile.LoadAsync(
                Path.Join(AppPaths.ProgramDir, @"Data\MicrosoftStoreItems.tab")
            )
        )
            return;

        var items = new List<MicrosoftStoreItem>();
        foreach (var row in tabFile.Rows.Skip(1))
        {
            if (row.Count != 4)
                continue;

            var groupNameKey = row[0];
            var nameKey = row[1] + "Name";
            var descriptionKey = row[1] + "Description";
            if (!Enum.TryParse<OptimizationItemCategory>(row[2], out var category))
                category = OptimizationItemCategory.Default;
            var packageName = row[3];

            var item = new MicrosoftStoreItem(
                groupNameKey,
                nameKey,
                descriptionKey,
                category,
                packageName
            );
            items.Add(item);
        }
        Items = items;
    }
}
