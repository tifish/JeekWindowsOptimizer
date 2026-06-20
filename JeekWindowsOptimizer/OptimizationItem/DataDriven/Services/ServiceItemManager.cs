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
            if (row.Count != 5)
                throw new InvalidDataException(
                    $"ServiceItems.tab: expected 5 columns but got {row.Count} in row: {string.Join('\t', row)}"
                );

            var groupNameKey = row[0];
            var nameKey = row[1] + "Name";
            var descriptionKey = row[1] + "Description";
            if (!Enum.TryParse(row[2], out OptimizationItemCategory category))
                category = OptimizationItemCategory.Default;
            var serviceName = row[3];
            if (!Enum.TryParse(row[4], true, out WindowsService.StartMode defaultStartMode))
                throw new InvalidDataException(
                    $"ServiceItems.tab: invalid DefaultStartMode '{row[4]}' for service '{serviceName}'"
                );

            var item = new ServiceItem(
                groupNameKey,
                nameKey,
                descriptionKey,
                category,
                serviceName,
                defaultStartMode
            );

            // Skip services that are not installed on this machine (e.g. Fax on some
            // Windows 11 SKUs); otherwise they show up but can never be optimized.
            if (!await item.ServiceExists())
                continue;

            Items.Add(item);
        }
    }
}
