using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Jeek.Avalonia.Localization;
using JeekWindowsOptimizer.Views;

namespace JeekWindowsOptimizer;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Localizer.SetLocalizer(new TabLocalizer(@"Data\Languages.tab"));

            // Line below is needed to remove Avalonia data validation.
            // Without this line you will get duplicate validations from both Avalonia and CT
            BindingPlugins.DataValidators.RemoveAt(0);
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
            PositionMainWindow(desktop.MainWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void PositionMainWindow(Window mainWindow)
    {
        var screen = mainWindow.Screens.Primary;
        if (screen == null)
            return;

        // Limit window size to screen.WorkingArea
        var workAreaWidth = screen.WorkingArea.Width;
        var workAreaHeight = (int)(screen.WorkingArea.Height * 0.9); // Under Windows 11 height exceeds screen.WorkingArea
        if (mainWindow.Width > workAreaWidth)
            mainWindow.Width = workAreaWidth;
        if (mainWindow.Height > workAreaHeight)
            mainWindow.Height = workAreaHeight;

        // Center window in screen.WorkingArea
        mainWindow.Position = new PixelPoint(
            (int)(workAreaWidth - mainWindow.Width) / 2,
            (int)(workAreaHeight - mainWindow.Height) / 2);
    }
}
