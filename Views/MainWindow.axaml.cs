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

    private void ToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        var toggleButton = (ToggleButton)sender!;
        var optimizationItem = (OptimizationItem)toggleButton.DataContext!;
        optimizationItem.HasOptimized = toggleButton.IsChecked ?? false;

        if (toggleButton.IsChecked != optimizationItem.HasOptimized)
        {
            SynchronizationContext.Current!.Post(_ =>
            {
                toggleButton.IsCheckedChanged -= ToggleButton_OnIsCheckedChanged;
                toggleButton.IsChecked = optimizationItem.HasOptimized;
                toggleButton.IsCheckedChanged += ToggleButton_OnIsCheckedChanged;
            }, null);
        }
    }
}
