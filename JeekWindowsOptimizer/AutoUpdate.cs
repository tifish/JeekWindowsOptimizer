using JeekTools;

namespace JeekWindowsOptimizer;

/// <summary>App-side wrapper around the JeekTools <see cref="AutoUpdater" />:
/// fixed release URLs, Debug builds disabled, staged-package bookkeeping under LocalAppData.</summary>
public static class AutoUpdate
{
    public const string ReleaseZipUrl =
        "https://github.com/tifish/JeekWindowsOptimizer/releases/download/latest_release/JeekWindowsOptimizer.zip";

    public const string VersionTxtUrl =
        "https://github.com/tifish/JeekWindowsOptimizer/releases/download/latest_release/version.txt";

    private static readonly AutoUpdater Updater = new(
        new AutoUpdaterOptions
        {
            AppExeName = "JeekWindowsOptimizer.exe",
            ReleaseZipUrl = ReleaseZipUrl,
            VersionTxtUrl = VersionTxtUrl,
            UserAgent = "JeekWindowsOptimizer",
#if DEBUG
            Disabled = true,
#endif
            // Keep postponed packages outside the system temp folder so they
            // survive reboots and temp cleanups until the user installs.
            UpdateRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JeekWindowsOptimizer",
                "Update"),
        }
    );

    private static bool _disableMirror;

    public static string StagedDirectory { get; private set; } = "";

    public static int RemoteCommitCount => Updater.RemoteVersion;

    public static string FailureReason => Updater.FailureReason;

    public static Task<UpdateCheckOutcome> HasUpdateAsync(bool disableMirror)
    {
        _disableMirror = disableMirror;
        return Updater.HasUpdateAsync();
    }

    public static async Task<bool> DownloadAndStageAsync(Action<double>? progressCallback = null)
    {
        StagedDirectory = "";

        IReadOnlyList<string>? urls = _disableMirror ? [ReleaseZipUrl] : null;
        var progress =
            progressCallback is null
                ? null
                : new Progress<UpdateDownloadProgress>(p =>
                {
                    if (p.TotalBytes is > 0)
                        progressCallback((double)p.ReceivedBytes / p.TotalBytes.Value * 100);
                });

        var staged = await Updater.DownloadAndStageAsync(urls, progress);
        StagedDirectory = staged ?? "";
        return staged is not null;
    }

    public static bool LaunchUpdate()
    {
        return !string.IsNullOrEmpty(StagedDirectory) && Updater.LaunchInstall(StagedDirectory);
    }

    public static int GetLocalCommitCount()
    {
        return Updater.GetLocalVersion();
    }
}
