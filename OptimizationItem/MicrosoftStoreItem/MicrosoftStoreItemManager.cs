using JeekTools;

namespace JeekWindowsOptimizer;

public static class MicrosoftStoreItemManager
{
    public static List<MicrosoftStoreItem> Items { get; } = [];

    public static async Task Load()
    {
        var tabFile = new TabFile();
        if (!await tabFile.LoadAsync(@"Data\MicrosoftStoreItems.tab"))
            return;

        foreach (var row in tabFile.Rows.Skip(1))
        {
            if (row.Count != 3)
                continue;

            var groupNameKey = row[0];
            var nameKey = row[1] + "Name";
            var descriptionKey = row[1] + "Description";
            var packageName = row[2];

            var item = new MicrosoftStoreItem(groupNameKey, nameKey, descriptionKey, packageName);
            Items.Add(item);
        }
    }
}
