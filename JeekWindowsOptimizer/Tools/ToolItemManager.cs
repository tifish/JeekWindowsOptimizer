using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

public static class ToolItemManager
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(ToolItemManager));

    public static List<ToolItem> Items { get; private set; } = [];

    public static async Task Load()
    {
        var tabFile = new TabFile();
        if (!await tabFile.LoadAsync(Path.Join(AppPaths.ProgramDir, @"Data\Tools.tab")))
            return;

        var items = new List<ToolItem>();
        foreach (var row in tabFile.Rows.Skip(1))
        {
            if (row.Count != 11)
                continue;

            try
            {
                var item = ParseToolItem(row);
                // Optional tools are not shipped; show them only when the user supplied the files.
                var optional = ParseBool(row[10]);
                if (optional && !item.IsAvailable)
                    continue;
                items.Add(item);
            }
            catch (Exception ex)
            {
                Log.ZLogError(ex, $"Failed to load tool item: {string.Join(" ", row)}");
            }
        }
        Items = items;
    }

    private static ToolItem ParseToolItem(List<string> row)
    {
        var index = -1;

        var groupNameKey = row[++index];
        var nameKey = row[++index] + "Name";
        var descriptionKey = row[index] + "Description";

        var executionKind = Enum.Parse<ToolExecutionKind>(row[++index], true);
        var target = row[++index];
        var expandedArguments = row[++index];
        var iconPath = row[++index];
        var expandedRunAsAdministrator = ParseBool(row[++index]);
        var expandedWaitForExit = ParseBool(row[++index]);
        var confirmBeforeRun = ParseBool(row[++index]);
        var openInTerminal = ParseBool(row[++index]);

        return new ToolItem(
            groupNameKey,
            nameKey,
            descriptionKey,
            executionKind,
            target,
            expandedArguments,
            expandedRunAsAdministrator,
            expandedWaitForExit,
            confirmBeforeRun,
            openInTerminal,
            iconPath
        );
    }

    private static bool ParseBool(string value)
    {
        return value.Equals("true", StringComparison.CurrentCultureIgnoreCase);
    }
}
