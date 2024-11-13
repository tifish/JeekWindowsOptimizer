using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace JeekWindowsOptimizer.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

#if DEBUG
        this.AttachDevTools();
#endif
    }

    private async void ToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        var toggleButton = (ToggleButton)sender!;
        var optimizationItem = (OptimizationItem)toggleButton.DataContext!;
        var isOptimized = toggleButton.IsChecked ?? false;

        if (!await optimizationItem.SetIsOptimized(isOptimized))
            // Change the toggle immediately cause wrong UI status, so delay it
            SynchronizationContext.Current!.Post(_ =>
            {
                toggleButton.IsCheckedChanged -= ToggleButton_OnIsCheckedChanged;
                toggleButton.IsChecked = optimizationItem.IsOptimized;
                toggleButton.IsCheckedChanged += ToggleButton_OnIsCheckedChanged;
            }, null);
    }
}
