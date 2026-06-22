using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
    }

    private void SaveUncheckedOptimizationItemsIfChanged()
    {
        if (DataContext is MainViewModel vm)
            vm.SaveUncheckedOptimizationItemsIfChanged();
    }

    // ReSharper disable once AsyncVoidMethod
    private async void StorageModeCustom_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = Localizer.Get("StorageModeCustomPickerTitle"),
                    AllowMultiple = false,
                }
            );

            if (folders.Count == 0)
                return;

            var path = folders[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
                return;

            AppSettingsStore.SwitchStorageMode(StorageMode.Custom, path);
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Failed to switch to custom storage directory");
        }
        finally
        {
            vm.RefreshStorageModeMenuCheckState();
        }
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

        var oldIsOptimized = optimizationItem.IsOptimized;

        try
        {
            await optimizationItem.SetIsOptimized(isOptimized);
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
            if (oldIsOptimized != optimizationItem.IsOptimized)
                model.UpdateItemStat(optimizationItem.Category);

            if (toggleButton.IsChecked != optimizationItem.IsOptimized)
                // Changing the toggle immediately can leave a stale visual state, so delay it.
                SynchronizationContext.Current!.Post(
                    _ =>
                    {
                        toggleButton.IsChecked = optimizationItem.IsOptimized;
                    },
                    null
                );

            model.IsBusy = false;
            model.StatusMessage = string.Format(
                Localizer.Get("OperatingItemFinished"),
                optimizationItem.Name
            );
        }
    }

    private void OptimizationItemTitle_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (DataContext is MainViewModel { AreOptimizationItemControlsEnabled: false })
            return;

        if (sender is not Control { DataContext: OptimizationItem item })
            return;

        item.ToggleChecked();
        e.Handled = true;
    }
}
