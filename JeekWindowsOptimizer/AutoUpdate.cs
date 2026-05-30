using System.Diagnostics;
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
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(AutoUpdate));

    public static string DownloadUrl { get; private set; } = "";
    public static int LocalCommitCount { get; private set; }
    public static int RemoteCommitCount { get; private set; }
    public static string FailureReason { get; private set; } = "";

    public static async Task<UpdateCheckOutcome> HasUpdateAsync(bool disableMirror)
    {
        DownloadUrl = "";
        RemoteCommitCount = 0;
        FailureReason = "";
        LocalCommitCount = ReadLocalCommitCount();

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

    public static bool LaunchUpdate()
    {
        try
        {
            if (string.IsNullOrEmpty(DownloadUrl))
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
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" \"{DownloadUrl}\"",
                    WorkingDirectory = workDir,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
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

    private static int ReadLocalCommitCount()
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
