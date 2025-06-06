using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Jeek.Avalonia.Localization;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer.Views;

public partial class MainWindow : Window
{
    private static readonly ILogger Log = LogManager.CreateLogger<MainViewModel>();

    public MainWindow()
    {
        OnLanguageChanged(null, EventArgs.Empty);
        Localizer.LanguageChanged += OnLanguageChanged;

        InitializeComponent();

#if DEBUG
        this.AttachDevTools();
#endif
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        FontFamily = new FontFamily(Localizer.Get("DefaultFontName"));
    }

    // ReSharper disable once AsyncVoidMethod
    private async void ToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        var toggleButton = (ToggleButton)sender!;
        if (toggleButton.DataContext is not OptimizationItem optimizationItem)
            return;
        var isOptimized = toggleButton.IsChecked ?? false;

        if (optimizationItem.IsOptimized == isOptimized)
            return;

        var model = (MainViewModel)DataContext!;
        model.IsBusy = true;
        model.StatusMessage = string.Format(Localizer.Get("OperatingItem"), optimizationItem.Name);

        try
        {
            if (await optimizationItem.SetIsOptimized(isOptimized))
                model.UpdateItemStat(optimizationItem.Category);
            else
                // Change the toggle immediately cause wrong UI status, so delay it
                SynchronizationContext.Current!.Post(_ => { toggleButton.IsChecked = optimizationItem.IsOptimized; }, null);
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Failed to change optimization item '{optimizationItem.Name}' status.");
        }
        finally
        {
            model.IsBusy = false;
            model.StatusMessage = string.Format(Localizer.Get("OperatingItemFinished"), optimizationItem.Name);
        }
    }

    private void InputElement_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var border = (Border)sender!;
        var item = border.DataContext as OptimizationItem;
        item!.ToggleChecked();
    }
}
