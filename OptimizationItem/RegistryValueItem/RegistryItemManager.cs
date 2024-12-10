using JeekTools;

namespace JeekWindowsOptimizer;

public static class RegistryItemManager
{
    public static List<RegistryItem> Items { get; } = [];

    public static async Task Load()
    {
        var tabFile = new TabFile();
        if (!await tabFile.LoadAsync(@"Data\RegistryValueItems.tab"))
            return;

        var itemsDict = new Dictionary<string, RegistryItem>();

        foreach (var row in tabFile.Rows.Skip(1))
        {
            if (row.Count != 12)
                continue;

            var index = -1;

            var groupNameKey = row[++index];
            var nameKey = row[++index] + "Name";
            var descriptionKey = row[index] + "Description";
            var keyPath = row[++index];
            var valueName = row[++index];
            var type = row[++index];
            var defaultValue = row[++index];
            var optimizingValue = row[++index];
            var deleteDefaultValue = row[++index].Equals("true", StringComparison.CurrentCultureIgnoreCase);
            var shouldTurnOffTamperProtection = row[++index].Equals("true", StringComparison.CurrentCultureIgnoreCase);
            var shouldUpdateGroupPolicy = row[++index].Equals("true", StringComparison.CurrentCultureIgnoreCase);
            var shouldReboot = row[++index].Equals("true", StringComparison.CurrentCultureIgnoreCase);
            var shouldRestartExplorer = row[++index].Equals("true", StringComparison.CurrentCultureIgnoreCase);

            OptimizationRegistryValue value = type switch
            {
                "int" => new OptimizationRegistryIntValue(keyPath, valueName,
                    int.Parse(defaultValue), int.Parse(optimizingValue), deleteDefaultValue),
                "string" => new OptimizationRegistryStringValue(keyPath, valueName,
                    defaultValue, optimizingValue, deleteDefaultValue),
                "binary" => new OptimizationRegistryBinaryValue(keyPath, valueName,
                    Convert.FromHexString(defaultValue), Convert.FromHexString(optimizingValue), deleteDefaultValue),
                _ => throw new NotImplementedException("Unknown type: " + type),
            };

            if (!itemsDict.TryGetValue(nameKey, out var item))
            {
                item = new RegistryItem(groupNameKey, nameKey, descriptionKey)
                {
                    ShouldTurnOffTamperProtection = shouldTurnOffTamperProtection,
                    ShouldUpdateGroupPolicy = shouldUpdateGroupPolicy,
                    ShouldReboot = shouldReboot,
                    ShouldRestartExplorer = shouldRestartExplorer,
                };
                itemsDict[nameKey] = item;
                Items.Add(item);
            }

            item.RegistryValues.Add(value);
        }

        foreach (var item in itemsDict.Values)
            item.Initialized();
    }
}
