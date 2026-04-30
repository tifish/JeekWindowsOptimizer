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
            if (row.Count != 10)
                continue;

            try
            {
                Items.Add(ParseToolItem(row));
            }
            catch (Exception ex)
            {
                Log.ZLogError(ex, $"Failed to load tool item: {string.Join(" ", row)}");
            }
        }
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
