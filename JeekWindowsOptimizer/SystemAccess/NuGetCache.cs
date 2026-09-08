using System.Diagnostics;
using System.Text;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

/// <summary>Uses NuGet's own location/configuration and cleanup implementation.</summary>
internal sealed class NuGetCache
{
    private readonly IReadOnlyDictionary<string, string>? _environment;
    private readonly string _workingDirectory;

    public NuGetCache() : this(null,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) { }

    internal NuGetCache(IReadOnlyDictionary<string, string>? environment, string workingDirectory)
    {
        _environment = environment;
        _workingDirectory = workingDirectory;
    }

    private static string DotnetPath => Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");

    public async Task<string?> GetPathAsync(string kind, CancellationToken cancellationToken)
    {
        if (!File.Exists(DotnetPath))
            return null;
        var output = await RunAsync(kind, "--list", cancellationToken);
        var prefix = kind + ":";
        var line = output.Split('\n').FirstOrDefault(line => line.TrimStart().StartsWith(prefix, StringComparison.Ordinal));
        if (line is null)
            throw new IOException(Localizer.Get("NuGetCacheQueryFailed"));
        var path = line.Trim()[prefix.Length..].Trim();
        if (!Path.IsPathFullyQualified(path))
            throw new IOException(Localizer.Get("NuGetCacheQueryFailed"));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public async Task ClearAsync(string kind, string expectedPath, CancellationToken cancellationToken)
    {
        // Resolve again before mutation and pin it for the child process.
        var path = await GetPathAsync(kind, cancellationToken);
        if (!string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException(Localizer.Get("NuGetCacheQueryFailed"));
        ValidatePath(expectedPath, cancellationToken);
        await RunAsync(kind, "--clear", cancellationToken, expectedPath);
    }

    internal static void ValidatePath(string path, CancellationToken cancellationToken = default)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var protectedPaths = new[] { Path.GetPathRoot(full)!, AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
        if (protectedPaths.Any(value => Path.TrimEndingDirectorySeparator(value).Equals(full, StringComparison.OrdinalIgnoreCase)
                || value.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            || !FileSystemCleaner.IsPlainDirectoryPath(full))
            throw new IOException(Localizer.Get("NuGetCacheUnsafePath"));
        if (!Directory.Exists(full))
            return;
        // The external tool owns deletion; do not give it a tree containing links.
        var pending = new Stack<string>();
        pending.Push(full);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(Localizer.Get("NuGetCacheUnsafePath"));
                if (entry is DirectoryInfo)
                    pending.Push(entry.FullName);
            }
        }
    }

    private async Task<string> RunAsync(string kind, string operation, CancellationToken cancellationToken,
        string? pinnedPath = null)
    {
        if (kind is not ("http-cache" or "global-packages"))
            throw new ArgumentException("Unsupported NuGet cache kind.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(operation == "--list" ? 30 : 120));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(DotnetPath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = _workingDirectory,
            },
        };
        foreach (var argument in new[] { "nuget", "locals", kind, operation, "--force-english-output" })
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";
        process.StartInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        if (_environment is not null)
            foreach (var (name, value) in _environment)
                process.StartInfo.Environment[name] = value;
        if (pinnedPath is not null)
            process.StartInfo.Environment[kind == "http-cache" ? "NUGET_HTTP_CACHE_PATH" : "NUGET_PACKAGES"] = pinnedPath;
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }
        var output = await stdout;
        await stderr;
        if (process.ExitCode != 0)
            throw new IOException(Localizer.Get(operation == "--list" ? "NuGetCacheQueryFailed" : "NuGetCacheClearFailed"));
        return output;
    }
}
