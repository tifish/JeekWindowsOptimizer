using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace JeekWindowsOptimizer;

internal static class CleanupCommand
{
    internal static async Task<string> Run(string executable, string arguments, CancellationToken token)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), executable), arguments)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
          RedirectStandardError = true } };
        process.Start();
        // Read the raw bytes: a redirected console tool writes the OEM code page, which
        // Encoding.Default (UTF-8 on .NET) would turn into replacement characters and,
        // worse, can swallow the ASCII separators the output parsers rely on.
        var output = ReadAllAsync(process.StandardOutput.BaseStream, token);
        var error = ReadAllAsync(process.StandardError.BaseStream, token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var text = Decode(await output) + Decode(await error);
        if (process.ExitCode != 0)
            throw new IOException($"{executable} ({process.ExitCode}): {text.Trim()}");
        return text;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, token);
        return buffer.ToArray();
    }

    /// <summary>Decodes console output with the console's own code page, never the process default.</summary>
    internal static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0)
            return "";
        var codePage = (uint)GetOEMCP();
        if (codePage == 65001)
            return Encoding.UTF8.GetString(bytes);
        var length = MultiByteToWideChar(codePage, 0, bytes, bytes.Length, null, 0);
        if (length <= 0)
            return Encoding.UTF8.GetString(bytes);
        var chars = new char[length];
        length = MultiByteToWideChar(codePage, 0, bytes, bytes.Length, chars, length);
        return length <= 0 ? Encoding.UTF8.GetString(bytes) : new string(chars, 0, length);
    }

    [DllImport("kernel32.dll")]
    private static extern int GetOEMCP();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MultiByteToWideChar(uint codePage, uint flags, byte[] bytes, int byteCount,
        char[]? chars, int charCount);
}
