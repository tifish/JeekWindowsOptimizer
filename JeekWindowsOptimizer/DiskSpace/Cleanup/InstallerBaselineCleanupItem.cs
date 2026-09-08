using System.Diagnostics;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer;

public sealed class InstallerBaselineCleanupItem : FixedDirectoryCleanupItem
{
    public override string NameKey => "InstallerBaselineCleanupName";
    public override string DescriptionKey => "InstallerBaselineCleanupDescription";
    protected override bool DefaultChecked => false;
    private readonly Func<bool> _installerRunning;

    public InstallerBaselineCleanupItem() : this(Environment.GetFolderPath(Environment.SpecialFolder.Windows), InstallerRunning) { }
    internal InstallerBaselineCleanupItem(string windowsDirectory, Func<bool> installerRunning)
        : base(Path.Join(windowsDirectory, "Installer", "$PatchCache$")) { _installerRunning = installerRunning; }

    private static bool InstallerRunning()
    {
        var processes = Process.GetProcessesByName("msiexec");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    protected override void ValidateCleanup()
    {
        if (_installerRunning()) throw new IOException(Localizer.Get("InstallerBaselineBusy"));
    }
}
