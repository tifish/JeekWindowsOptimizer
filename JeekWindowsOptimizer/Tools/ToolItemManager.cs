using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

public static class ToolItemManager
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(ToolItemManager));

    public static List<ToolItem> Items { get; } = [];

    public static async Task Load()
    {
        Items.Clear();

        var tabFile = new TabFile();
        if (!await tabFile.LoadAsync(Path.Join(AppContext.BaseDirectory, @"Data\Tools.tab")))
            return;

        foreach (var row in tabFile.Rows.Skip(1))
        {
            if (row.Count != 6)
                continue;

            var index = -1;

            var groupNameKey = row[++index];
            var nameKey = row[++index] + "Name";
            var descriptionKey = row[index] + "Description";
            var executablePath = row[++index];
            var arguments = row[++index];
            var runAsAdministrator = row[++index].Equals(
                "true",
                StringComparison.CurrentCultureIgnoreCase
            );
            var waitForExit = row[++index].Equals("true", StringComparison.CurrentCultureIgnoreCase);

            try
            {
                Items.Add(
                    new ToolItem(
                        groupNameKey,
                        nameKey,
                        descriptionKey,
                        executablePath,
                        arguments,
                        runAsAdministrator,
                        waitForExit
                    )
                );
            }
            catch (Exception ex)
            {
                Log.ZLogError(ex, $"Failed to load tool item: {executablePath}");
            }
        }
    }
}
