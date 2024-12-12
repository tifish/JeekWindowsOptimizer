using JeekTools;

namespace JeekWindowsOptimizer;

public static class ServiceItemManager
{
    public static List<ServiceItem> Items { get; } = [];

    public static async Task Load()
    {
        var tabFile = new TabFile();
        if (!await tabFile.LoadAsync(Path.Join(AppContext.BaseDirectory, @"Data\ServiceItems.tab")))
            return;

        foreach (var row in tabFile.Rows.Skip(1))
        {
            if (row.Count != 4)
                continue;

            var groupNameKey = row[0];
            var nameKey = row[1] + "Name";
            var descriptionKey = row[1] + "Description";
            var isPersonal = row[2].Equals("true", StringComparison.CurrentCultureIgnoreCase);
            var serviceName = row[3];

            var item = new ServiceItem(groupNameKey, nameKey, descriptionKey, isPersonal, serviceName);
            Items.Add(item);
        }
    }
}
