using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Jeek.Avalonia.Localization;
using JeekTools;
using JeekWindowsOptimizer.Views;
using Microsoft.Extensions.Logging;

namespace JeekWindowsOptimizer;

public class App : Application
{
    private static readonly ILogger Log = LogManager.CreateLogger<MainViewModel>();

    /// <summary>Win11Fluent dark-mode Checkbox / ToggleSwitch-on fill (~AccentFill).</summary>
    private static readonly Color Win11FluentControlAccentDark = Color.Parse("#48B2E9");

    /// <summary>Win11Fluent light-mode Checkbox / ToggleSwitch-on fill (<see cref="Win11FluentControlAccentDark" /> on white reads harsh).</summary>
    private static readonly Color Win11FluentControlAccentLight = Color.Parse("#0067C0");

    private ColorPaletteResources? _fluentLightPalette;
    private ColorPaletteResources? _fluentDarkPalette;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Localizer.SetLocalizer(
            new TabLocalizer(Path.Join(AppContext.BaseDirectory, @"Data\Languages.tab"))
        );

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel() };
            PositionMainWindow(desktop.MainWindow);
        }

        ApplyWin11FluentControlAccentPalettes();

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyWin11FluentControlAccentPalettes()
    {
        FluentTheme? fluent = null;
        foreach (var entry in Styles)
            if (entry is FluentTheme ft)
            {
                fluent = ft;
                break;
            }

        if (fluent is null)
            return;

        var palettes = fluent.Palettes;
        _fluentLightPalette ??= new ColorPaletteResources();
        _fluentDarkPalette ??= new ColorPaletteResources();
        palettes[ThemeVariant.Light] = _fluentLightPalette;
        palettes[ThemeVariant.Dark] = _fluentDarkPalette;
        _fluentLightPalette.Accent = Win11FluentControlAccentLight;
        _fluentDarkPalette.Accent = Win11FluentControlAccentDark;
    }

    private void PositionMainWindow(Window mainWindow)
    {
        var screen = mainWindow.Screens.Primary;
        if (screen == null)
            return;

        // Limit window size to screen.WorkingArea
        var workAreaWidth = screen.WorkingArea.Width / screen.Scaling;
        var workAreaHeight = (int)(screen.WorkingArea.Height * 0.9) / screen.Scaling; // Under Windows 11 height exceeds screen.WorkingArea
        if (mainWindow.Width > workAreaWidth)
            mainWindow.Width = workAreaWidth;
        if (mainWindow.Height > workAreaHeight)
            mainWindow.Height = workAreaHeight;

        // Center window in screen.WorkingArea
        mainWindow.Position = new PixelPoint(
            (int)((workAreaWidth - mainWindow.Width) / 2 * screen.Scaling),
            (int)((workAreaHeight - mainWindow.Height) / 2 * screen.Scaling)
        );
    }
}
