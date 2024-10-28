using Avalonia;
using Avalonia.Controls;
using JeekTools;

namespace JeekWindowsOptimizer;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        var appDirectory = AppDomain.CurrentDomain.SetupInformation.ApplicationBase;
        if (appDirectory != null)
            Environment.CurrentDirectory = appDirectory;

        if (Design.IsDesignMode)
            LogManager.DisableLogging();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
