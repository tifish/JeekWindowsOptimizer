using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
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
        InitializeComponent();

        Localizer.LanguageChanged += OnLanguageChanged;
        UpdateFontFamily();

        Loaded += OnLoaded;
        Deactivated += OnDeactivated;
        Closing += OnClosing;

#if DEBUG
        Application.Current?.AttachDeveloperTools();
#endif
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.LoadedCommand.CanExecute(null))
            vm.LoadedCommand.Execute(null);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        SaveUncheckedOptimizationItemsIfChanged();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        SaveUncheckedOptimizationItemsIfChanged();
    }

    private void SaveUncheckedOptimizationItemsIfChanged()
    {
        if (DataContext is MainViewModel vm)
            vm.SaveUncheckedOptimizationItemsIfChanged();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        UpdateFontFamily();
    }

    private void UpdateFontFamily()
    {
        FontFamily = new FontFamily(Localizer.Get("DefaultFontName"));
    }

    private void Window_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        ShowSearchAndFocus();
        e.Handled = true;
    }

    private void SearchButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        vm.ToggleSearchCommand.Execute(null);
        if (vm.IsSearchVisible)
            FocusSearchTextBox();
    }

    private void SearchTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        if (DataContext is MainViewModel vm)
            vm.ExitSearchCommand.Execute(null);

        e.Handled = true;
    }

    private void ShowSearchAndFocus()
    {
        if (DataContext is not MainViewModel vm)
            return;

        vm.ShowSearchCommand.Execute(null);
        FocusSearchTextBox();
    }

    private void FocusSearchTextBox()
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                SearchTextBox.Focus();
                SearchTextBox.SelectAll();
            },
            DispatcherPriority.Input
        );
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
                SynchronizationContext.Current!.Post(
                    _ =>
                    {
                        toggleButton.IsChecked = optimizationItem.IsOptimized;
                    },
                    null
                );
        }
        catch (Exception ex)
        {
            Log.ZLogError(
                ex,
                $"Failed to change optimization item '{optimizationItem.Name}' status."
            );
        }
        finally
        {
            model.IsBusy = false;
            model.StatusMessage = string.Format(
                Localizer.Get("OperatingItemFinished"),
                optimizationItem.Name
            );
        }
    }

    private void InputElement_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (DataContext is MainViewModel { IsBusy: true })
            return;

        var border = (Border)sender!;
        var item = border.DataContext as OptimizationItem;
        item!.ToggleChecked();
    }
}
