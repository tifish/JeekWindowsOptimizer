using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using JeekWindowsOptimizer.Views;

namespace JeekWindowsOptimizer.Mcp;

internal static class GroupNavigationProbe
{
    internal static async Task<string> RunAsync(MainWindow window, MainViewModel vm)
    {
        if (vm.IsDiskSpaceActive) throw new InvalidOperationException("Wait until disk scanning/operations finish.");
        vm.EnsureDiskSpaceItems();
        var savedTab = vm.SelectedTabIndex;
        var savedSearch = vm.SearchText;
        var scannedField = typeof(MainViewModel).GetField("_diskSpaceScannedOnce", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var scanned = scannedField.GetValue(vm);
        var groups = vm.AllDiskSpaceGroups.ToDictionary(g => g, g => g.IsExpanded);
        var scroll = window.FindControl<ScrollViewer>("DiskSpaceContentScrollViewer")!;
        var items = window.FindControl<ItemsControl>("DiskSpaceGroupsItemsControl")!;
        var navigation = window.FindControl<ListBox>("DiskSpaceGroupNavigation")!;
        var savedOffset = scroll.Offset;
        var savedMargin = items.Margin;
        var selectedKey = vm.SelectedDiskSpaceGroupNavItem?.NameKey;
        var shortGroup = new DiskSpaceGroup("NavigationProbeShort", [new TempFilesCleanupItem()]);
        var results = new List<string>();
        async Task Layout()
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        }
        void Click(ListBox nav, object item)
        {
            window.UpdateLayout();
            var row = nav.ContainerFromItem(item) ?? throw new InvalidOperationException("Missing navigation row");
            using var pointer = new Avalonia.Input.Pointer(937, PointerType.Mouse, true);
            row.RaiseEvent(new PointerPressedEventArgs(row, pointer, window,
                row.TranslatePoint(new Point(2, 2), window) ?? default, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
        }
        void AtTop(ItemsControl content, ScrollViewer viewer, object group, string scenario)
        {
            var container = content.ContainerFromItem(group) ?? throw new InvalidOperationException("Missing group container");
            var y = container.TranslatePoint(default, content)!.Value.Y + content.Margin.Top - viewer.Offset.Y;
            DiskSpaceCleanupProbe.Require(Math.Abs(y) < 1.5, $"{scenario}: heading relative Y={y}, offset={viewer.Offset.Y}");
            results.Add(scenario);
        }
        double HeadingOffset(ItemsControl content, object group)
        {
            var container = content.ContainerFromItem(group) ?? throw new InvalidOperationException("Missing group container");
            return container.TranslatePoint(default, content)!.Value.Y + content.Margin.Top;
        }
        void NavIs(DiskSpaceGroup expected, string scenario)
        {
            var actual = vm.SelectedDiskSpaceGroupNavItem?.DiskSpaceGroup;
            DiskSpaceCleanupProbe.Require(ReferenceEquals(actual, expected),
                $"{scenario}: nav shows '{actual?.NameKey ?? "(none)"}', expected '{expected.NameKey}'");
            results.Add(scenario);
        }
        try
        {
            scannedField.SetValue(vm, true);
            vm.SearchText = "";
            vm.SelectedTabIndex = 3;
            await Layout();
            var first = vm.DiskSpaceGroupNavItems[0];
            var developer = vm.DiskSpaceGroupNavItems[1];
            Click(navigation, developer);
            await Layout();
            AtTop(items, scroll, developer.DiskSpaceGroup!, "pointer activation");
            scroll.Offset += new Vector(0, 250);
            Click(navigation, developer);
            await Layout();
            AtTop(items, scroll, developer.DiskSpaceGroup!, "repeat selected row");
            Click(navigation, first);
            await Layout();
            AtTop(items, scroll, first.DiskSpaceGroup!, "backward navigation");
            developer.DiskSpaceGroup!.IsExpanded = false;
            Click(navigation, developer);
            await Layout();
            DiskSpaceCleanupProbe.Require(developer.DiskSpaceGroup.IsExpanded, "collapsed group reopened");
            AtTop(items, scroll, developer.DiskSpaceGroup, "expand before positioning");
            // Scrolling now moves the nav highlight with the content, so Enter acts on whatever
            // row the scroll left highlighted. That is the contract worth asserting: the keyboard
            // path positions the selected group, whichever one that is.
            scroll.Offset += new Vector(0, 200);
            await Layout();
            var highlighted = (GroupNavItem)navigation.SelectedItem!;
            navigation.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Layout();
            AtTop(items, scroll, highlighted.DiskSpaceGroup!, "keyboard reactivation");
            Click(navigation, developer);
            Click(navigation, first);
            await Layout();
            AtTop(items, scroll, first.DiskSpaceGroup!, "latest rapid click wins");
            vm.AllDiskSpaceGroups.Add(shortGroup);
            vm.SearchText = " "; // Rebuild visible groups without filtering.
            await Layout();
            var shortNav = vm.DiskSpaceGroupNavItems.Single(n => n.DiskSpaceGroup == shortGroup);
            Click(navigation, shortNav);
            await Layout();
            AtTop(items, scroll, shortGroup, "short final group");
            var firstAgain = vm.DiskSpaceGroupNavItems[0];
            Click(navigation, firstAgain);
            await Layout();
            AtTop(items, scroll, firstAgain.DiskSpaceGroup!, "return from short final group");
            // The trailing space a short last group needs must not stay behind as dead scroll area.
            DiskSpaceCleanupProbe.Require(items.Margin.Bottom == 0, $"trailing space released (bottom={items.Margin.Bottom})");
            results.Add("trailing space released");
            vm.SearchText = "Maven";
            await Layout();
            Click(navigation, vm.DiskSpaceGroupNavItems.Single());
            await Layout();
            AtTop(items, scroll, vm.DiskSpaceGroups.Single(), "filtered replacement group");
            // The reverse link: scrolling the content moves the nav selection, and must never
            // scroll back, or the view would fight the user's wheel.
            vm.SearchText = "";
            await Layout();
            if (vm.DiskSpaceGroups.Count >= 2)
            {
                var firstGroup = vm.DiskSpaceGroups[0];
                var secondGroup = vm.DiskSpaceGroups[1];

                scroll.Offset = new Vector(0, 0);
                await Layout();
                NavIs(firstGroup, "top of the content selects the first group");

                scroll.Offset = new Vector(0, HeadingOffset(items, secondGroup));
                await Layout();
                NavIs(secondGroup, "scrolling to a heading selects that group");

                // Part way inside a group: still that group, and crucially the offset must stay
                // exactly where it was put. A sync that scrolled back would snap it to the heading.
                var inside = HeadingOffset(items, secondGroup) + 120;
                scroll.Offset = new Vector(0, inside);
                await Layout();
                NavIs(secondGroup, "scrolling inside a group keeps it selected");
                DiskSpaceCleanupProbe.Require(Math.Abs(scroll.Offset.Y - inside) < 1.5,
                    $"scroll sync does not scroll back: offset={scroll.Offset.Y}, expected {inside}");
                results.Add("scroll sync does not scroll back");

                // A few pixels above a heading still belongs to the group before it.
                scroll.Offset = new Vector(0, Math.Max(0, HeadingOffset(items, secondGroup) - 5));
                await Layout();
                NavIs(firstGroup, "just above a heading keeps the previous group");

                // The very bottom belongs to the last group even when that group is too short for
                // its heading to ever reach the top of the viewport.
                scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
                await Layout();
                NavIs(vm.DiskSpaceGroups[^1], "bottom of the content selects the last group");

                // A deliberate nav click still wins: the sync must not undo its positioning.
                Click(navigation, vm.DiskSpaceGroupNavItems[0]);
                await Layout();
                AtTop(items, scroll, firstGroup, "click still positions after a scroll sync");
                NavIs(firstGroup, "click leaves its own group selected");
            }

            foreach (var tab in new[] { (0, "OptimizationGroupNavigation", "OptimizationGroupsItemsControl", "OptimizationContentScrollViewer"),
                (4, "ToolGroupNavigation", "ToolGroupsItemsControl", "ToolsContentScrollViewer") })
            {
                vm.SearchText = "";
                vm.SelectedTabIndex = tab.Item1;
                await Layout();
                var nav = window.FindControl<ListBox>(tab.Item2)!;
                var content = window.FindControl<ItemsControl>(tab.Item3)!;
                var viewer = window.FindControl<ScrollViewer>(tab.Item4)!;
                if (nav.Items.Count == 0) continue;
                var entry = (GroupNavItem)nav.Items[0]!;
                var group = (object?)entry.OptimizationGroup ?? entry.ToolGroup!;
                Click(nav, entry);
                await Layout();
                viewer.Offset += new Vector(0, 150);
                Click(nav, entry);
                await Layout();
                AtTop(content, viewer, group, tab.Item2 + " repeated pointer");
            }
            return "PASS navigation: " + string.Join(", ", results);
        }
        finally
        {
            vm.AllDiskSpaceGroups.Remove(shortGroup);
            foreach (var (group, expanded) in groups) group.IsExpanded = expanded;
            vm.SelectedTabIndex = 3;
            vm.SearchText = savedSearch + " ";
            vm.SearchText = savedSearch;
            vm.SelectedDiskSpaceGroupNavItem = vm.DiskSpaceGroupNavItems.FirstOrDefault(n => n.NameKey == selectedKey);
            await Layout();
            items.Margin = savedMargin;
            window.UpdateLayout();
            scroll.Offset = savedOffset;
            vm.SelectedTabIndex = savedTab;
            scannedField.SetValue(vm, scanned);
        }
    }
}
