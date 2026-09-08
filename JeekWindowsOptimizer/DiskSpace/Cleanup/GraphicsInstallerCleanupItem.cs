namespace JeekWindowsOptimizer;

public sealed class GraphicsInstallerCleanupItem : FixedDirectoryCleanupItem
{
    private readonly string _kind;
    public override string NameKey => "Graphics" + _kind + "CleanupName";
    public override string DescriptionKey => "Graphics" + _kind + "CleanupDescription";
    protected override bool DefaultChecked => false;
    public GraphicsInstallerCleanupItem(string kind) : this(kind, Resolve(kind)) { }
    internal GraphicsInstallerCleanupItem(string kind, string path) : base(path) { _kind = kind; }

    private static string Resolve(string kind) => kind switch
    {
        "NvidiaDownloader" => Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NVIDIA Corporation", "Downloader"),
        "NvidiaInstaller" => Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "Installer2"),
        "NvidiaRoot" => Path.Join(DiskSpaceItemManager.SystemDriveRoot, "NVIDIA"),
        "AmdRoot" => Path.Join(DiskSpaceItemManager.SystemDriveRoot, "AMD"),
        _ => throw new ArgumentException("Unknown graphics cache", nameof(kind)),
    };
}
