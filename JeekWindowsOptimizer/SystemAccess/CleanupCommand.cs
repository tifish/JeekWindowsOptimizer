using System.Diagnostics;
using System.Text;

namespace JeekWindowsOptimizer;

internal static class CleanupCommand
{
    internal static async Task<string> Run(string executable, string arguments, CancellationToken token)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), executable), arguments)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
          RedirectStandardError = true, StandardOutputEncoding = Encoding.Default, StandardErrorEncoding = Encoding.Default } };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var text = await output + await error;
        if (process.ExitCode != 0)
            throw new IOException($"{executable} ({process.ExitCode}): {text.Trim()}");
        return text;
    }
}
