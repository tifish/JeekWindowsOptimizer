using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jeek.Avalonia.Localization;
using JeekTools;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

public enum ToolExecutionKind
{
    PackagedExecutable,
    SystemCommand,
    ShellOpen,
    BuiltInAction,
}

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
        ToolExecutionKind executionKind,
        string target,
        string arguments,
        bool runAsAdministrator,
        bool waitForExit,
        bool confirmBeforeRun,
        bool openInTerminal,
        string iconPath = ""
    )
    {
        GroupNameKey = groupNameKey;
        NameKey = nameKey;
        DescriptionKey = descriptionKey;
        ExecutionKind = executionKind;
        Target = target;
        Arguments = arguments;
        RunAsAdministrator = runAsAdministrator;
        WaitForExit = waitForExit;
        ConfirmBeforeRun = confirmBeforeRun;
        OpenInTerminal = openInTerminal;

        if (ExecutionKind == ToolExecutionKind.PackagedExecutable)
        {
            FullExecutablePath = ResolveToolsPath(target);
            FullWorkingDirectory = Path.GetDirectoryName(FullExecutablePath) ?? ToolsRoot;
        }
        else
        {
            FullExecutablePath = Environment.ExpandEnvironmentVariables(target);
            FullWorkingDirectory = "";
        }

        IconPath = ResolveIconPath(iconPath);

        if (Design.IsDesignMode)
            LoadToolIconSyncForDesigner();
        else
            _ = LoadToolIconAsync();
    }

    public string GroupNameKey { get; }
    public string NameKey { get; }
    public string DescriptionKey { get; }
    public ToolExecutionKind ExecutionKind { get; }
    public string Target { get; }
    public string FullExecutablePath { get; }
    public string FullWorkingDirectory { get; }
    public string Arguments { get; }
    public bool RunAsAdministrator { get; }
    public bool WaitForExit { get; }
    public bool ConfirmBeforeRun { get; }
    public bool OpenInTerminal { get; }
    public string IconPath { get; }

    public string Name => Localizer.Get(NameKey);
    public string Description => Localizer.Get(DescriptionKey);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToolIcon))]
    private Bitmap? _toolIcon;

    public bool HasToolIcon => ToolIcon is not null;
    public bool IsAvailable =>
        ExecutionKind != ToolExecutionKind.PackagedExecutable || File.Exists(FullExecutablePath);
    public bool HasDisplayStatus => !string.IsNullOrEmpty(DisplayStatus);

    public string DisplayStatus =>
        IsAvailable
            ? LastRunMessage
            : string.Format(Localizer.Get("ToolNotFound"), Target);

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
            if (ConfirmBeforeRun && !await ConfirmRun())
                return;

            var succeeded = ExecutionKind switch
            {
                ToolExecutionKind.PackagedExecutable => await RunProcess(),
                ToolExecutionKind.SystemCommand => await RunProcess(),
                ToolExecutionKind.ShellOpen => await RunProcess(),
                ToolExecutionKind.BuiltInAction => await BuiltInToolActions.Run(Target),
                _ => false,
            };

            LastRunMessage = succeeded ? "" : Localizer.Get("ToolFailed");
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

    private async Task<bool> ConfirmRun()
    {
        var msgResult = await MessageBoxManager
            .GetMessageBoxStandard(
                new MessageBoxStandardParams
                {
                    ContentTitle = Localizer.Get("ToolConfirmTitle"),
                    ContentMessage = string.Format(Localizer.Get("ToolConfirmMessage"), Name),
                    ButtonDefinitions = ButtonEnum.OkCancel,
                    Icon = Icon.Warning,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Topmost = true,
                    FontFamily = Localizer.Get("DefaultFontName"),
                }
            )
            .ShowAsync();

        return msgResult == ButtonResult.Ok;
    }

    private async Task<bool> RunProcess()
    {
        var startInfo = BuildStartInfo();

        if (WaitForExit)
            return await Executor.RunAndWait(startInfo);

        return Executor.Run(startInfo) is not null;
    }

    private ProcessStartInfo BuildStartInfo()
    {
        if (OpenInTerminal)
        {
            var command = BuildCommandLine(FullExecutablePath, Arguments);
            return new ProcessStartInfo("cmd.exe", $"/k \"{command.Replace("\"", "\\\"")}\"")
            {
                UseShellExecute = true,
                Verb = RunAsAdministrator ? "runas" : "",
            };
        }

        var startInfo = new ProcessStartInfo(
            FullExecutablePath,
            Environment.ExpandEnvironmentVariables(Arguments)
        )
        {
            UseShellExecute = true,
            WorkingDirectory = FullWorkingDirectory,
        };

        if (RunAsAdministrator)
            startInfo.Verb = "runas";

        return startInfo;
    }

    private static string BuildCommandLine(string fileName, string arguments)
    {
        var command = fileName.Contains(' ') ? $"\"{fileName}\"" : fileName;
        if (!string.IsNullOrWhiteSpace(arguments))
            command += " " + Environment.ExpandEnvironmentVariables(arguments);
        return command;
    }

    private string ResolveIconPath(string iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
            return FullExecutablePath;

        if (ExecutionKind != ToolExecutionKind.PackagedExecutable || Path.IsPathRooted(iconPath))
            return iconPath;

        return ResolveToolsPath(iconPath);
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
            ToolIcon = ToolIconExtractor.TryLoadToolIcon(IconPath);
        }
        catch
        {
            // 设计器占位路径或非 Windows 预览环境
        }
    }

    private async Task LoadToolIconAsync()
    {
        var path = IconPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        byte[]? pngBytes;
        try
        {
            pngBytes = await Task.Run(() => ToolIconExtractor.TryEncodeToolIconPng(path))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to encode tool icon: {Name} ({IconPath})");
            return;
        }

        if (pngBytes is null || pngBytes.Length == 0)
        {
            Log.ZLogWarning(
                $"Tool icon not found: {Name}, target={Target}, iconPath={IconPath}"
            );
            return;
        }

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
                        $"Failed to create tool icon bitmap: {Target}"
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
