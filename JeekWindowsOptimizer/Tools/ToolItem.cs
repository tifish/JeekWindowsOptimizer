using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jeek.Avalonia.Localization;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

public partial class ToolItem : ObservableObject
{
    private static readonly ILogger Log = LogManager.CreateLogger<ToolItem>();
    private static readonly string ToolsRoot = Path.GetFullPath(
        Path.Join(AppContext.BaseDirectory, "Tools")
    );

    public ToolItem(
        string groupNameKey,
        string nameKey,
        string descriptionKey,
        string executablePath,
        string arguments,
        bool runAsAdministrator,
        bool waitForExit
    )
    {
        GroupNameKey = groupNameKey;
        NameKey = nameKey;
        DescriptionKey = descriptionKey;
        RelativeExecutablePath = executablePath;
        Arguments = arguments;
        RunAsAdministrator = runAsAdministrator;
        WaitForExit = waitForExit;

        FullExecutablePath = ResolveToolsPath(executablePath);
        FullWorkingDirectory = Path.GetDirectoryName(FullExecutablePath) ?? ToolsRoot;

        if (Design.IsDesignMode)
            LoadToolIconSyncForDesigner();
        else
            _ = LoadToolIconAsync();
    }

    public string GroupNameKey { get; }
    public string NameKey { get; }
    public string DescriptionKey { get; }
    public string RelativeExecutablePath { get; }
    public string FullExecutablePath { get; }
    public string FullWorkingDirectory { get; }
    public string Arguments { get; }
    public bool RunAsAdministrator { get; }
    public bool WaitForExit { get; }

    public string Name => Localizer.Get(NameKey);
    public string Description => Localizer.Get(DescriptionKey);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToolIcon))]
    private Bitmap? _toolIcon;

    public bool HasToolIcon => ToolIcon is not null;
    public bool IsAvailable => File.Exists(FullExecutablePath);
    public bool HasDisplayStatus => !string.IsNullOrEmpty(DisplayStatus);

    public string DisplayStatus =>
        IsAvailable
            ? LastRunMessage
            : string.Format(Localizer.Get("ToolNotFound"), RelativeExecutablePath);

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string LastRunMessage { get; set; } = "";

    private bool CanRun()
    {
        return !IsRunning && IsAvailable;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        IsRunning = true;
        LastRunMessage = "";

        try
        {
            var startInfo = new ProcessStartInfo(FullExecutablePath, Arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = FullWorkingDirectory,
            };

            if (RunAsAdministrator)
                startInfo.Verb = "runas";

            if (WaitForExit)
            {
                var succeeded = await Executor.RunAndWait(startInfo);
                LastRunMessage = succeeded ? "" : Localizer.Get("ToolFailed");
            }
            else
            {
                var process = Executor.Run(startInfo);
                LastRunMessage = process is null ? Localizer.Get("ToolFailed") : "";
            }
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Failed to run tool: {Name}");
            LastRunMessage = Localizer.Get("ToolFailed");
        }
        finally
        {
            IsRunning = false;
        }
    }

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
    }

    partial void OnLastRunMessageChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayStatus));
        OnPropertyChanged(nameof(HasDisplayStatus));
    }

    private void LoadToolIconSyncForDesigner()
    {
        try
        {
            ToolIcon = ToolIconExtractor.TryLoadToolIcon(FullExecutablePath);
        }
        catch
        {
            // 设计器占位路径或非 Windows 预览环境
        }
    }

    private async Task LoadToolIconAsync()
    {
        var path = FullExecutablePath;
        if (!File.Exists(path))
            return;

        byte[]? pngBytes;
        try
        {
            pngBytes = await Task.Run(() => ToolIconExtractor.TryEncodeToolIconPng(path))
                .ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (pngBytes is null || pngBytes.Length == 0)
            return;

        Dispatcher.UIThread.Post(
            () =>
            {
                try
                {
                    var next = new Bitmap(new MemoryStream(pngBytes));
                    var prev = ToolIcon;
                    ToolIcon = next;
                    prev?.Dispose();
                }
                catch (Exception ex)
                {
                    Log.ZLogWarning(
                        ex,
                        $"Failed to create tool icon bitmap: {RelativeExecutablePath}"
                    );
                }
            },
            DispatcherPriority.Background
        );
    }

    public void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(DisplayStatus));
        OnPropertyChanged(nameof(HasDisplayStatus));
    }

    private static string ResolveToolsPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidOperationException(
                "Tool paths must be relative to the Tools directory."
            );

        var fullPath = Path.GetFullPath(Path.Join(ToolsRoot, relativePath));
        var rootWithSeparator =
            ToolsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (
            !fullPath.Equals(ToolsRoot, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
        )
            throw new InvalidOperationException("Tool paths must stay inside the Tools directory.");

        return fullPath;
    }
}
