using JeekTools;

namespace JeekWindowsOptimizer;

public static class RegistryItemManager
{
    public static List<RegistryItem> Items { get; } = [];

    public static void Load()
    {
        var tabFile = new TabFile();
        if (!tabFile.Load(@"Data\RegistryValueItems.tab"))
            return;

        var itemsDict = new Dictionary<string, RegistryItem>();

        foreach (var row in tabFile.Rows.Skip(1))
        {
            if (row.Count != 8)
                continue;

            var name = row[0];
            var description = row[1].Replace("\\n", "\r\n");
            var keyPath = row[2];
            var valueName = row[3];
            var type = row[4];
            var defaultValue = row[5];
            var optimizingValue = row[6];
            var deleteDefaultValue = row[7].Equals("true", StringComparison.CurrentCultureIgnoreCase);

            OptimizationRegistryValue value = type switch
            {
                "int" => new OptimizationRegistryIntValue(keyPath, valueName, int.Parse(defaultValue), int.Parse(optimizingValue), deleteDefaultValue),
                "string" => new OptimizationRegistryStringValue(keyPath, valueName, defaultValue, optimizingValue, deleteDefaultValue),
                "binary" => new OptimizationRegistryBinaryValue(keyPath, valueName, Convert.FromHexString(defaultValue), Convert.FromHexString(optimizingValue), deleteDefaultValue),
                _ => throw new NotImplementedException("Unknown type: " + type),
            };

            if (!itemsDict.TryGetValue(name, out var item))
            {
                item = new RegistryItem(name, description);
                itemsDict[name] = item;
                Items.Add(item);
            }

            item.RegistryValues.Add(value);
        }
    }
}
