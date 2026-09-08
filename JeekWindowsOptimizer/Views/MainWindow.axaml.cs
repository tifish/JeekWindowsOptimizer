using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jeek.Avalonia.Localization;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer.Views;

public partial class MainWindow : Window
{
    private static readonly ILogger Log = LogManager.CreateLogger<MainViewModel>();

    private MainViewModel? _subscribedViewModel;

    public MainWindow()
    {
        InitializeComponent();
        foreach (var navigation in new[] { OptimizationGroupNavigation, DiskSpaceGroupNavigation, ToolGroupNavigation })
        {
            // ListBox handles pointer presses itself; observe the tunnel, including already-selected rows.
            navigation.AddHandler(PointerPressedEvent, GroupNavigation_OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            navigation.AddHandler(KeyDownEvent, GroupNavigation_OnKeyDown, RoutingStrategies.Tunnel);
        }

        Localizer.LanguageChanged += OnLanguageChanged;
        UpdateFontFamily();

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Deactivated += OnDeactivated;
        Closing += OnClosing;

#if DEBUG
        Application.Current?.AttachDeveloperTools();
#endif
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.ScrollToGroupRequested -= OnScrollToGroupRequested;
            _subscribedViewModel = null;
        }

        if (DataContext is MainViewModel vm)
        {
            _subscribedViewModel = vm;
            vm.ScrollToGroupRequested += OnScrollToGroupRequested;
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // The shell's folder-move progress dialog parents itself to this window.
        UserFolderRelocationItem.OwnerWindowHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

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
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.ScrollToGroupRequested -= OnScrollToGroupRequested;
            _subscribedViewModel = null;
        }
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
    }

    private void GroupNavigation_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox navigation || DataContext is not MainViewModel vm
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is not Visual source) return;
        var row = source as ListBoxItem ?? source.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (row?.DataContext is GroupNavItem item && navigation.Items.Contains(item))
            vm.ActivateGroupNavigation(item);
    }

    private void GroupNavigation_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || sender is not ListBox { SelectedItem: GroupNavItem item }
            || DataContext is not MainViewModel vm) return;
        vm.ActivateGroupNavigation(item);
        e.Handled = true;
    }

    private int _scrollRequestVersion;

    private void OnScrollToGroupRequested(object? sender, object? group)
    {
        if (group is null) return;
        var version = ++_scrollRequestVersion;
        Dispatcher.UIThread.Post(() => ScrollContentToGroup(group, version), DispatcherPriority.Loaded);
    }

    private void ScrollContentToGroup(object group, int version, bool retry = true)
    {
        if (version != _scrollRequestVersion) return;
        var (items, scroll) = group switch
        {
            OptimizationGroup => (OptimizationGroupsItemsControl, OptimizationContentScrollViewer),
            ToolGroup => (ToolGroupsItemsControl, ToolsContentScrollViewer),
            DiskSpaceGroup => (DiskSpaceGroupsItemsControl, DiskSpaceContentScrollViewer),
            _ => ((ItemsControl?)null, (ScrollViewer?)null),
        };
        if (items is null || scroll is null || !items.IsEffectivelyVisible) return;
        UpdateLayout();
        var container = items.ContainerFromItem(group);
        if (container is null)
        {
            if (retry) Dispatcher.UIThread.Post(() => ScrollContentToGroup(group, version, false), DispatcherPriority.Loaded);
            return;
        }

        // A short final group needs trailing space to put its heading at the viewport top.
        // Keep the space minimal; the usual long groups need no extra margin.
        if (items.Items.LastOrDefault() is { } lastItem && items.ContainerFromItem(lastItem) is { } last)
        {
            var margin = items.Margin;
            items.Margin = new Thickness(margin.Left, margin.Top, margin.Right, Math.Max(0, scroll.Viewport.Height - last.Bounds.Height));
            UpdateLayout();
        }
        if (container.TranslatePoint(default, items) is { } position)
            scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(position.Y + items.Margin.Top,
                0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
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

            await vm.SwitchStorageLocationAsync(StorageLocation.CustomDirectory, path);
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
                model.OnOptimizationItemStatusChanged(optimizationItem.Category);

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

    private void DiskSpaceItemTitle_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        switch (sender)
        {
            case Control { DataContext: DiskSpaceCleanupItem { IsBusy: false } cleanup }:
                cleanup.ToggleChecked();
                break;
            case Control { DataContext: DiskSpaceRelocationItem relocation }:
                relocation.ToggleChecked();
                break;
            default:
                return;
        }

        e.Handled = true;
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
