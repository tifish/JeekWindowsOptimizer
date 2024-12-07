﻿using JeekTools;

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
            if (row.Count != 13)
                continue;

            var groupName = row[0];
            var name = row[1];
            var description = row[2].Replace("\\n", "\r\n");
            var keyPath = row[3];
            var valueName = row[4];
            var type = row[5];
            var defaultValue = row[6];
            var optimizingValue = row[7];
            var deleteDefaultValue = row[8].Equals("true", StringComparison.CurrentCultureIgnoreCase);
            var shouldTurnOffTamperProtection = row[9].Equals("true", StringComparison.CurrentCultureIgnoreCase);
            var shouldUpdateGroupPolicy = row[10].Equals("true", StringComparison.CurrentCultureIgnoreCase);
            var shouldReboot = row[11].Equals("true", StringComparison.CurrentCultureIgnoreCase);
            var shouldRestartExplorer = row[12].Equals("true", StringComparison.CurrentCultureIgnoreCase);

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

            if (!itemsDict.TryGetValue(name, out var item))
            {
                item = new RegistryItem(groupName, name, description)
                {
                    ShouldTurnOffTamperProtection = shouldTurnOffTamperProtection,
                    ShouldUpdateGroupPolicy = shouldUpdateGroupPolicy,
                    ShouldReboot = shouldReboot,
                    ShouldRestartExplorer = shouldRestartExplorer,
                };
                itemsDict[name] = item;
                Items.Add(item);
            }

            item.RegistryValues.Add(value);
        }

        foreach (var item in itemsDict.Values)
            item.Initialized();
    }
}
