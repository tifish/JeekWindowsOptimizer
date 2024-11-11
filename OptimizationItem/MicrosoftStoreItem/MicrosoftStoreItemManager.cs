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
            if (row.Count != 4)
                continue;

            var groupName = row[0];
            var name = row[1];
            var description = row[2].Replace("\\n", "\r\n");
            var packageName = row[3];

            var item = new MicrosoftStoreItem(groupName, name, description, packageName);
            Items.Add(item);
        }
    }
}
