using System.Diagnostics;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

public sealed class GraphicsInstallerCleanupItem : FixedDirectoryCleanupItem
{
    private readonly string _kind;
    private readonly Func<bool> _installerRunning;
    public override string NameKey => "Graphics" + _kind + "CleanupName";
    public override string DescriptionKey => "Graphics" + _kind + "CleanupDescription";
    protected override bool DefaultChecked => false;
    public GraphicsInstallerCleanupItem(string kind) : this(kind, Resolve(kind)) { }
    internal GraphicsInstallerCleanupItem(string kind, string path, Func<bool>? installerRunning = null) : base(path)
    { _kind = kind; _installerRunning = installerRunning ?? InstallerRunning; }

    private static string Resolve(string kind) => kind switch
    {
        "NvidiaDownloader" => Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NVIDIA Corporation", "Downloader"),
        "NvidiaInstaller" => Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "Installer2"),
        "NvidiaRoot" => Path.Join(DiskSpaceItemManager.SystemDriveRoot, "NVIDIA"),
        "AmdRoot" => Path.Join(DiskSpaceItemManager.SystemDriveRoot, "AMD"),
        _ => throw new ArgumentException("Unknown graphics cache", nameof(kind)),
    };

    /// <summary>These folders are what a driver installer extracts into and reads back from.</summary>
    private static bool InstallerRunning()
    {
        foreach (var name in new[] { "msiexec", "setup" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length > 0) return true; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return false;
    }

    protected override void ValidateCleanup()
    {
        if (_installerRunning()) throw new IOException(Localizer.Get("GraphicsInstallerBusy"));
    }
}
