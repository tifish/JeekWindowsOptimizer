using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

internal interface IPnpmStore
{
    Task<string?> GetPathAsync(CancellationToken token);
    Task PruneAsync(string expectedPath, CancellationToken token);
}

internal sealed class PnpmStore : IPnpmStore
{
    private sealed record Launcher(string Executable, string? Script);
    private readonly Launcher? _launcher = FindLauncher();
    private readonly string _workingDirectory;
    private readonly string? _storeOverride;
    internal PnpmStore() : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), null) { }
    internal PnpmStore(string workingDirectory, string? storeOverride)
    { _workingDirectory = workingDirectory; _storeOverride = storeOverride; }
    internal bool IsInstalled => _launcher is not null;

    private static Launcher? FindLauncher()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(p => p.Trim().Trim('"')).Where(Path.IsPathFullyQualified)
            .Concat(new[] { Environment.GetEnvironmentVariable("PNPM_HOME") ?? "",
                Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pnpm"),
                Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm") })
            .Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var directory in directories)
        {
            var exe = Path.Join(directory, "pnpm.exe");
            if (File.Exists(exe)) return new(exe, null);
            foreach (var relative in new[] { @"node_modules\pnpm\bin\pnpm.cjs", @"node_modules\corepack\dist\pnpm.js" })
            {
                var script = Path.Join(directory, relative);
                if (!File.Exists(script)) continue;
                var node = new[] { directory }.Concat(directories).Select(p => Path.Join(p, "node.exe")).FirstOrDefault(File.Exists);
                if (node is not null) return new(node, script);
            }
        }
        return null;
    }

    internal static string ParsePath(string output)
    {
        var lines = output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (lines.Length != 1 || !Path.IsPathFullyQualified(lines[0]))
            throw new IOException(Localizer.Get("PnpmStoreQueryFailed"));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(lines[0]));
    }

    public async Task<string?> GetPathAsync(CancellationToken token) => _launcher is null ? null
        : ParsePath(await RunAsync("path", _storeOverride, token));

    public async Task PruneAsync(string expectedPath, CancellationToken token)
    {
        if (!string.Equals(expectedPath, await GetPathAsync(token), StringComparison.OrdinalIgnoreCase))
            throw new IOException(Localizer.Get("DeveloperCachePathChanged"));
        // store path includes pnpm's layout version, while --store-dir expects its parent.
        if (!Regex.IsMatch(Path.GetFileName(expectedPath), @"^v\d+$"))
            throw new IOException(Localizer.Get("PnpmStoreQueryFailed"));
        var storeRoot = Path.GetDirectoryName(expectedPath)!;
        if (!string.Equals(expectedPath, ParsePath(await RunAsync("path", storeRoot, token)), StringComparison.OrdinalIgnoreCase))
            throw new IOException(Localizer.Get("DeveloperCachePathChanged"));
        ValidateStore(expectedPath, token);
        // prune also clears metadata caches. Pin that side effect to an isolated directory,
        // so only the measured store is affected by this item (and by temporary-store probes).
        var metadata = Path.Join(Path.GetTempPath(), "JeekPnpmMetadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(metadata);
        try { await RunAsync("prune", storeRoot, token, metadata); }
        finally { FileSystemCleaner.DeleteDirectory(metadata); }
    }

    internal static void ValidateStore(string path, CancellationToken token)
    {
        try
        {
            new DeveloperCachePaths().ValidateRoot(path);
            if (!Directory.Exists(path)) return;
            var projects = Path.Join(path, "projects");
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.TryPop(out var directory))
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        // pnpm itself manages project registration junctions. Do not follow or delete their targets.
                        if (string.Equals(directory, projects, StringComparison.OrdinalIgnoreCase)) continue;
                        throw new IOException("Unexpected link in pnpm store.");
                    }
                    if (entry is DirectoryInfo) pending.Push(entry.FullName);
                }
            }
        }
        catch (IOException) { throw new IOException(Localizer.Get("DeveloperCacheUnsafePath")); }
    }

    private async Task<string> RunAsync(string operation, string? storeRoot, CancellationToken token, string? metadataCache = null)
    {
        if (_launcher is null) throw new IOException(Localizer.Get("PnpmStoreQueryFailed"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(operation == "path" ? 30 : 300));
        using var process = new Process { StartInfo = new ProcessStartInfo(_launcher.Executable)
        { WorkingDirectory = _workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
        if (_launcher.Script is not null) process.StartInfo.ArgumentList.Add(_launcher.Script);
        process.StartInfo.ArgumentList.Add("store");
        process.StartInfo.ArgumentList.Add(operation);
        if (storeRoot is not null)
        {
            process.StartInfo.ArgumentList.Add("--store-dir");
            process.StartInfo.ArgumentList.Add(storeRoot);
        }
        if (metadataCache is not null)
        {
            process.StartInfo.ArgumentList.Add("--config.cache-dir=" + metadataCache);
        }
        // A cache scan must not bootstrap a package manager or switch to a project's pinned version.
        process.StartInfo.Environment["COREPACK_ENABLE_NETWORK"] = "0";
        process.StartInfo.Environment["COREPACK_ENABLE_DOWNLOAD_PROMPT"] = "0";
        process.StartInfo.Environment["npm_config_manage_package_manager_versions"] = "false";
        process.StartInfo.Environment["pnpm_config_manage_package_manager_versions"] = "false";
        process.StartInfo.Environment["NO_UPDATE_NOTIFIER"] = "1";
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var output = await stdout;
        await stderr; // Do not echo user configuration or credentials in tool output.
        if (process.ExitCode != 0) throw new IOException(Localizer.Get("PnpmStoreCommandFailed") + " (" + process.ExitCode + ")");
        return output;
    }
}
