using JeekTools;

namespace JeekWindowsOptimizer;

public static class ServiceItemManager
{
    public static List<ServiceItem> Items { get; } = [];

    public static void Load()
    {
        var tabFile = new TabFile();
        if (!tabFile.Load(@"Data\ServiceItems.tab"))
            return;

        foreach (var row in tabFile.Rows.Skip(1))
        {
            if (row.Count != 4)
                continue;

            var groupName = row[0];
            var name = row[1];
            var description = row[2].Replace("\\n", "\r\n");
            var serviceName = row[3];

            var item = new ServiceItem(groupName, name, description, serviceName);
            Items.Add(item);
        }
    }
}
