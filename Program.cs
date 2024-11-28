using Avalonia;
using Avalonia.Controls;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

sealed class Program
{
    private static readonly ILogger Log = LogManager.CreateLogger<MainViewModel>();

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            var appDirectory = AppDomain.CurrentDomain.SetupInformation.ApplicationBase;
            if (appDirectory != null)
                Environment.CurrentDirectory = appDirectory;

            if (Design.IsDesignMode)
                LogManager.DisableLogging();

            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.ZLogCritical(ex, $"An error occurred in Main");
        }
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
