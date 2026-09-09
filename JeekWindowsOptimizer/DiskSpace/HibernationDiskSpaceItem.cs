using Jeek.Avalonia.Localization;
using Microsoft.Win32;

namespace JeekWindowsOptimizer;

public sealed class HibernationDiskSpaceItem : DiskSpaceItem
{
    public override string GroupNameKey => "DiskSpaceCleanup";
    public override string NameKey => "HibernationDiskSpaceName";
    public override string DescriptionKey => "HibernationDiskSpaceDescription";
    public string Mode { get; private set; } = "Unknown";
    public string ModeText => Localizer.Get("HibernationMode" + Mode);
    private readonly Func<(long Size, string Mode)> _read;
    private readonly Func<string, CancellationToken, Task<string>> _run;

    public HibernationDiskSpaceItem() : this(ReadState, (args, token) => CleanupCommand.Run("powercfg.exe", args, token)) { }
    internal HibernationDiskSpaceItem(Func<(long, string)> read, Func<string, CancellationToken, Task<string>> run)
    { _read = read; _run = run; }

    private static (long, string) ReadState()
    {
        var file = new FileInfo(Path.Join(DiskSpaceItemManager.SystemDriveRoot, "hiberfil.sys"));
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
        return (!file.Exists ? 0 : file.Length, ModeFor(file.Exists, key?.GetValue("HiberFileType")));
    }

    /// <summary>
    ///     The file is the authority: powercfg deletes it when hibernation is off and recreates it
    ///     when it is on. HiberFileType only tells reduced (1) from full, which is also what
    ///     Windows uses when the value was never written — so a change never stays unresolved and
    ///     a successful powercfg run is never reported as a mismatch.
    /// </summary>
    internal static string ModeFor(bool fileExists, object? hiberFileType) =>
        !fileExists ? "Off" : hiberFileType is 1 ? "Reduced" : "Full";

    internal static string[] Commands(string mode) => mode switch
    {
        "Reduced" => ["/h on", "/h /size 0", "/h /type reduced"],
        "Off" => ["/h off"],
        "Full" => ["/h on", "/h /type full"],
        _ => throw new ArgumentException("Unknown hibernation mode", nameof(mode)),
    };

    protected override async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var state = await Task.Run(_read, cancellationToken);
        SizeBytes = state.Size;
        Mode = state.Mode;
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(ModeText));
    }

    public async Task<DiskSpaceOperationResult> SetModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        var commands = Commands(mode);
        if (IsBusy) return new(false);
        State = DiskSpaceItemState.Working;
        ErrorMessage = null;
        var before = 0L;
        Exception? failure = null;
        try
        {
            before = (await Task.Run(_read, cancellationToken)).Size;
            foreach (var command in commands) await _run(command, cancellationToken);
        }
        catch (Exception ex) { failure = ex; }
        // Always reconcile partial success, e.g. /h on succeeded but reduced is unsupported.
        try { await RefreshCoreAsync(CancellationToken.None); }
        catch (Exception ex) { SizeBytes = null; failure ??= ex; }
        if (failure is not null || Mode != mode)
        {
            ErrorMessage = failure?.Message ?? Localizer.Get("HibernationModeMismatch");
            State = DiskSpaceItemState.Failed;
            return new(false);
        }
        State = DiskSpaceItemState.Done;
        return new(true, Math.Max(0, before - SizeBytes.GetValueOrDefault()));
    }

    public override void NotifyLanguageChanged()
    { base.NotifyLanguageChanged(); OnPropertyChanged(nameof(ModeText)); }
}
