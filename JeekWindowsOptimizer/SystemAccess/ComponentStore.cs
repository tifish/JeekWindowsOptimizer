using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JeekWindowsOptimizer;

/// <summary>
///     WinSxS component store analysis and cleanup through DISM. Output is forced
///     to English with /English so the size lines can be parsed on any locale.
/// </summary>
public static partial class ComponentStore
{
    public readonly record struct Analysis(
        long ReclaimableBytes,
        long ActualSizeBytes,
        int ReclaimablePackages,
        bool CleanupRecommended,
        string RawOutput
    );

    private static string DismPath =>
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), "Dism.exe");

    public static async Task<Analysis?> Analyze(CancellationToken cancellationToken = default)
    {
        var output = await RunDism(
            "/Online /Cleanup-Image /AnalyzeComponentStore /English",
            cancellationToken
        );
        if (output is null)
            return null;

        var backups = ParseSize(output, "Backups and Disabled Features");
        var cache = ParseSize(output, "Cache and Temporary Data");
        var actual = ParseSize(output, "Actual Size of Component Store");
        var packages = ParseInt(output, "Number of Reclaimable Packages");
        var recommended = ParseYesNo(output, "Component Store Cleanup Recommended");

        if (backups < 0 && cache < 0 && actual < 0)
            return null;

        return new Analysis(
            Math.Max(0, backups) + Math.Max(0, cache),
            Math.Max(0, actual),
            Math.Max(0, packages),
            recommended,
            output
        );
    }

    /// <summary>
    ///     Removes superseded component versions. With <paramref name="resetBase" />
    ///     every superseded version goes immediately, after which installed updates
    ///     can no longer be uninstalled.
    /// </summary>
    public static async Task<bool> Cleanup(
        bool resetBase,
        CancellationToken cancellationToken = default
    )
    {
        var arguments = "/Online /Cleanup-Image /StartComponentCleanup /English";
        if (resetBase)
            arguments += " /ResetBase";

        var output = await RunDism(arguments, cancellationToken);
        return output is not null
            && output.Contains("The operation completed successfully", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> RunDism(string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(DismPath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            if (!process.Start())
                return null;
        }
        catch
        {
            return null;
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // Already gone.
            }
            throw;
        }

        return await stdout + await stderr;
    }

    private static long ParseSize(string output, string label)
    {
        var match = SizeLine().Matches(output).FirstOrDefault(m =>
            m.Groups["label"].Value.Trim().Equals(label, StringComparison.OrdinalIgnoreCase)
        );
        if (match is null)
            return -1;

        if (
            !double.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value
            )
        )
            return -1;

        var multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            "TB" => 1024d * 1024 * 1024 * 1024,
            _ => 1d,
        };

        return (long)(value * multiplier);
    }

    private static int ParseInt(string output, string label)
    {
        var match = IntLine().Matches(output).FirstOrDefault(m =>
            m.Groups["label"].Value.Trim().Equals(label, StringComparison.OrdinalIgnoreCase)
        );
        return match is not null && int.TryParse(match.Groups["value"].Value, out var value)
            ? value
            : -1;
    }

    private static bool ParseYesNo(string output, string label)
    {
        var match = YesNoLine().Matches(output).FirstOrDefault(m =>
            m.Groups["label"].Value.Trim().Equals(label, StringComparison.OrdinalIgnoreCase)
        );
        return match is not null
            && match.Groups["value"].Value.Equals("Yes", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^\s*(?<label>[A-Za-z][A-Za-z ()]+?)\s*:\s*(?<value>[\d.]+)\s*(?<unit>[KMGT]?B)\s*$", RegexOptions.Multiline)]
    private static partial Regex SizeLine();

    [GeneratedRegex(@"^\s*(?<label>[A-Za-z][A-Za-z ()]+?)\s*:\s*(?<value>\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex IntLine();

    [GeneratedRegex(@"^\s*(?<label>[A-Za-z][A-Za-z ()]+?)\s*:\s*(?<value>Yes|No)\s*$", RegexOptions.Multiline)]
    private static partial Regex YesNoLine();
}
