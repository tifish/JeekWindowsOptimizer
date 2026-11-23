using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Threading;
using JeekWindowsOptimizer.Views;

namespace JeekWindowsOptimizer.Mcp;

internal static class UiLayoutProbe
{
    internal static async Task<string> RunAsync(MainWindow window, MainViewModel vm)
    {
        var savedTab = vm.SelectedTabIndex;
        var savedSearch = vm.SearchText;
        var savedTheme = Application.Current?.RequestedThemeVariant ?? ThemeVariant.Default;
        var results = new List<string>();

        async Task Layout()
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        }

        try
        {
            var headerBar = window.FindControl<Border>("AppHeaderBar")
                ?? throw new InvalidOperationException("Missing #AppHeaderBar");
            var statusBar = window.FindControl<Border>("AppStatusBar")
                ?? throw new InvalidOperationException("Missing #AppStatusBar");
            var searchBox = window.FindControl<TextBox>("SearchTextBox")
                ?? throw new InvalidOperationException("Missing #SearchTextBox");
            var navTabs = window.FindControl<TabStrip>("MainNavigationTabs")
                ?? throw new InvalidOperationException("Missing #MainNavigationTabs");
            var navScroll = navTabs.Parent as ScrollViewer;
            var filterToggle = window.FindControl<ToggleButton>("ShowOnlyNotOptimizedToggle")
                ?? throw new InvalidOperationException("Missing #ShowOnlyNotOptimizedToggle");
            var refreshBtn = window.FindControl<Button>("RefreshOptimizationItemStatesButton")
                ?? throw new InvalidOperationException("Missing #RefreshOptimizationItemStatesButton");

            vm.SearchText = "";
            double? expectedHeaderHeight = null;
            double? expectedStatusHeight = null;

            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                if (Application.Current is { } app)
                    app.RequestedThemeVariant = theme;

                for (var tab = 0; tab <= 5; tab++)
                {
                    vm.SelectedTabIndex = tab;
                    await Layout();

                    var headerBounds = headerBar.Bounds;
                    var statusBounds = statusBar.Bounds;
                    var searchBounds = searchBox.Bounds;

                    DiskSpaceCleanupProbe.Require(
                        headerBounds.Height is >= 40 and <= 70,
                        $"Theme={theme} Tab={tab}: unexpected AppHeaderBar height={headerBounds.Height}"
                    );
                    DiskSpaceCleanupProbe.Require(
                        statusBounds.Height is >= 24 and <= 44,
                        $"Theme={theme} Tab={tab}: unexpected AppStatusBar height={statusBounds.Height}"
                    );
                    DiskSpaceCleanupProbe.Require(
                        searchBox.IsEffectivelyVisible && searchBounds.Width >= 150 && searchBounds.Height >= 28,
                        $"Theme={theme} Tab={tab}: SearchTextBox invalid bounds={searchBounds}"
                    );

                    expectedHeaderHeight ??= headerBounds.Height;
                    expectedStatusHeight ??= statusBounds.Height;

                    DiskSpaceCleanupProbe.Require(
                        Math.Abs(headerBounds.Height - expectedHeaderHeight.Value) < 0.5,
                        $"Theme={theme} Tab={tab}: AppHeaderBar height jumped ({headerBounds.Height} vs {expectedHeaderHeight.Value})"
                    );
                    DiskSpaceCleanupProbe.Require(
                        Math.Abs(statusBounds.Height - expectedStatusHeight.Value) < 0.5,
                        $"Theme={theme} Tab={tab}: AppStatusBar height jumped ({statusBounds.Height} vs {expectedStatusHeight.Value})"
                    );

                    var expectOpt = tab < 3;
                    DiskSpaceCleanupProbe.Require(
                        filterToggle.IsEffectivelyVisible == expectOpt,
                        $"Theme={theme} Tab={tab}: filterToggle visibility={filterToggle.IsEffectivelyVisible}, expected={expectOpt}"
                    );
                    DiskSpaceCleanupProbe.Require(
                        refreshBtn.IsEffectivelyVisible == expectOpt,
                        $"Theme={theme} Tab={tab}: refreshBtn visibility={refreshBtn.IsEffectivelyVisible}, expected={expectOpt}"
                    );
                }

                results.Add($"{theme} 6 tabs header/footer invariant");
            }

            // Search interaction check on Tools tab
            vm.SelectedTabIndex = 5;
            await Layout();
            var allTools = vm.ToolGroups.Sum(g => g.Items.Count);
            vm.SearchText = "CrystalDisk";
            await Layout();
            var filteredTools = vm.ToolGroups.Sum(g => g.Items.Count);
            DiskSpaceCleanupProbe.Require(
                filteredTools > 0 && filteredTools < allTools,
                $"Search filter failed on Tools: all={allTools}, filtered={filteredTools}"
            );
            vm.ClearSearchCommand.Execute(null);
            await Layout();
            DiskSpaceCleanupProbe.Require(
                vm.ToolGroups.Sum(g => g.Items.Count) == allTools,
                "ClearSearch did not restore full Tools list"
            );
            results.Add($"inline search filter ({allTools}->{filteredTools}->{allTools})");

            if (navScroll is not null)
            {
                DiskSpaceCleanupProbe.Require(
                    navScroll.Extent.Width <= navScroll.Viewport.Width + 1,
                    $"MainNavigationTabs clips horizontally: extent={navScroll.Extent.Width:0.#} > viewport={navScroll.Viewport.Width:0.#}"
                );
                results.Add($"nav tabs fit ({navScroll.Extent.Width:0.#}/{navScroll.Viewport.Width:0.#}px)");
            }

            return $"PASS ui_layout: {string.Join(", ", results)} | headerH={expectedHeaderHeight:0.#} statusH={expectedStatusHeight:0.#} window={window.ClientSize.Width:0}x{window.ClientSize.Height:0}";
        }
        finally
        {
            if (Application.Current is { } app)
                app.RequestedThemeVariant = savedTheme;
            vm.SearchText = savedSearch;
            vm.SelectedTabIndex = savedTab;
            await Layout();
        }
    }
}
