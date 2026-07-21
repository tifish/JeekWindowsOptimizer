using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

public enum UpdateCheckOutcome
{
    Available,
    UpToDate,
    Failed,
}

public static class AutoUpdate
{
    public const string ReleaseZipUrl =
        "https://github.com/tifish/JeekWindowsOptimizer/releases/download/latest_release/JeekWindowsOptimizer.zip";

    public const string VersionTxtUrl =
        "https://github.com/tifish/JeekWindowsOptimizer/releases/download/latest_release/version.txt";

    private const string UpdateScriptName = "AutoUpdate.ps1";
    private const string AppName = "JeekWindowsOptimizer";
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(AutoUpdate));

    public static string DownloadUrl { get; private set; } = "";
    public static string StagedDirectory { get; private set; } = "";
    public static int LocalCommitCount { get; private set; }
    public static int RemoteCommitCount { get; private set; }
    public static string FailureReason { get; private set; } = "";

    private static bool _disableMirror;

    public static async Task<UpdateCheckOutcome> HasUpdateAsync(bool disableMirror)
    {
        DownloadUrl = "";
        StagedDirectory = "";
        RemoteCommitCount = 0;
        FailureReason = "";
        _disableMirror = disableMirror;
        LocalCommitCount = GetLocalCommitCount();

        try
        {
            string versionUrl;
            if (disableMirror)
            {
                DownloadUrl = ReleaseZipUrl;
                versionUrl = VersionTxtUrl;
            }
            else
            {
                var zipMirror = await GitHubMirrors.GetFastestMirror(ReleaseZipUrl);
                if (string.IsNullOrEmpty(zipMirror))
                    return Fail("no reachable mirror");

                DownloadUrl = zipMirror;
                versionUrl = await GitHubMirrors.GetFastestMirror(VersionTxtUrl);
                if (string.IsNullOrEmpty(versionUrl))
                    versionUrl = VersionTxtUrl;
            }

            var remote = await DownloadTextAsync(versionUrl);
            if (string.IsNullOrWhiteSpace(remote))
                return Fail($"empty version.txt from {versionUrl}");

            if (!int.TryParse(remote.Trim(), out var remoteCount) || remoteCount <= 0)
                return Fail($"version.txt did not contain a positive integer: '{remote.Trim()}'");
            RemoteCommitCount = remoteCount;

            if (LocalCommitCount <= 0)
                return Fail("local version unavailable (dev build?)");

            if (RemoteCommitCount > LocalCommitCount)
            {
                Log.ZLogInformation(
                    $"AutoUpdate: local={LocalCommitCount} remote={RemoteCommitCount} update available"
                );
                return UpdateCheckOutcome.Available;
            }

            Log.ZLogInformation(
                $"AutoUpdate: local={LocalCommitCount} remote={RemoteCommitCount} up to date"
            );
            return UpdateCheckOutcome.UpToDate;
        }
        catch (Exception ex)
        {
            return Fail($"exception: {ex.Message}");
        }
    }

    public static async Task<bool> DownloadAndStageAsync(Action<double>? progressCallback = null)
    {
        StagedDirectory = "";
        FailureReason = "";

        if (string.IsNullOrEmpty(DownloadUrl))
        {
            FailureReason = "no download URL (run update check first)";
            return false;
        }

        var stageRoot = Path.Combine(Path.GetTempPath(), $"{AppName}-update");
        var packageDir = Path.Combine(stageRoot, "package");
        var zipPath = Path.Combine(Path.GetTempPath(), $"{AppName}-update.zip");

        try
        {
            if (Directory.Exists(stageRoot))
                Directory.Delete(stageRoot, true);
            if (File.Exists(zipPath))
                File.Delete(zipPath);

            // Try the mirror selected during the update check first, then fall back to the rest.
            var candidates = new List<string> { DownloadUrl };
            if (!_disableMirror)
                candidates.AddRange(
                    GitHubMirrors.GetMirrors(ReleaseZipUrl).Where(url => url != DownloadUrl)
                );

            var downloaded = false;
            foreach (var url in candidates)
            {
                if (await DownloadFileAsync(url, zipPath, progressCallback))
                {
                    downloaded = true;
                    break;
                }
            }

            if (!downloaded)
            {
                FailureReason = "download failed from all sources";
                Log.ZLogWarning($"AutoUpdate: {FailureReason}");
                return false;
            }

            Directory.CreateDirectory(packageDir);
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, packageDir, true));
            File.Delete(zipPath);

            if (!File.Exists(Path.Combine(packageDir, $"{AppName}.exe")))
            {
                FailureReason = $"update package is missing {AppName}.exe";
                Log.ZLogWarning($"AutoUpdate: {FailureReason}");
                return false;
            }

            StagedDirectory = packageDir;
            Log.ZLogInformation($"AutoUpdate: update staged at {packageDir}");
            return true;
        }
        catch (Exception ex)
        {
            FailureReason = $"exception: {ex.Message}";
            Log.LogError(ex, "AutoUpdate: failed to download and stage update");
            return false;
        }
    }

    private static async Task<bool> DownloadFileAsync(
        string url,
        string filePath,
        Action<double>? progressCallback
    )
    {
        try
        {
            using var client = HttpHelper.GetHttpClient(timeoutSeconds: 600);
            using var response = await client.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead
            );
            if (!response.IsSuccessStatusCode)
            {
                Log.ZLogWarning($"AutoUpdate: {url} returned {response.StatusCode}");
                return false;
            }

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var buffer = new byte[1024 * 1024];
            long totalRead = 0;
            var lastReportedPercent = -1.0;
            int read;

            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(
                filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None
            );

            while ((read = await contentStream.ReadAsync(buffer.AsMemory())) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                totalRead += read;

                if (totalBytes > 0 && progressCallback != null)
                {
                    var percent = (double)totalRead / totalBytes * 100;
                    if (percent - lastReportedPercent >= 1.0)
                    {
                        lastReportedPercent = percent;
                        progressCallback(percent);
                    }
                }
            }

            progressCallback?.Invoke(100.0);
            return true;
        }
        catch (Exception ex)
        {
            Log.ZLogWarning($"AutoUpdate: download from {url} failed: {ex.Message}");
            return false;
        }
    }

    public static bool LaunchUpdate()
    {
        try
        {
            if (string.IsNullOrEmpty(StagedDirectory) || !Directory.Exists(StagedDirectory))
                return false;

            var exePath = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(exePath))
                return false;

            var workDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(workDir))
                return false;

            var scriptPath = Path.Combine(workDir, UpdateScriptName);
            if (!File.Exists(scriptPath))
            {
                Log.LogError("AutoUpdate: script not found at {ScriptPath}", scriptPath);
                return false;
            }

            Process.Start(
                new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" \"{StagedDirectory}\"",
                    WorkingDirectory = workDir,
                    UseShellExecute = true,
                }
            );

            Log.LogInformation("AutoUpdate: launched updater");
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "AutoUpdate: failed to launch updater");
            return false;
        }
    }

    private static UpdateCheckOutcome Fail(string reason)
    {
        FailureReason = reason;
        Log.ZLogWarning($"AutoUpdate: {reason}");
        return UpdateCheckOutcome.Failed;
    }

    private static async Task<string?> DownloadTextAsync(string url)
    {
        try
        {
            using var client = HttpHelper.GetHttpClient();
            using var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsStringAsync();
        }
        catch
        {
            return null;
        }
    }

    public static int GetLocalCommitCount()
    {
        try
        {
            return Assembly.GetExecutingAssembly().GetName().Version?.Major ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
