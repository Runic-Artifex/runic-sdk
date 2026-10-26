using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using TUnit.Core;
using TUnit.Core.Executors;
using static Runic.Navigation.Wpf.Tests.Ui;

namespace Runic.Navigation.Wpf.Tests;

[TestExecutor<WpfDispatcherExecutor>]
internal sealed class SelectorTests
{
    [Test, DisplayName("selector guard veto and external navigation"), NotInParallel("WpfSuite", Order = 21)]
    public void GuardVetoAndExternalSelection()
    {
        using var fixture = new NavFixture();
        var allow = false;
        var home = new GuardedPage("home", () => ValueTask.FromResult(allow));
        var other = new Page("other");
        var region = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Borrow<Page>(home));
        var tabs = new TabControl { ItemsSource = new Page[] { home, other }, DisplayMemberPath = nameof(Page.Name) };
        NavigationSelector.SetRegion(tabs, region);
        var window = ShowWindow(tabs, scope => AddTemplates(scope, typeof(Page)));
        try
        {
            Require(tabs.SelectedItem == home && tabs.ContentTemplate is not null, "The selector did not follow the initial region.");
            tabs.SelectedItem = other;
            PumpUntil(() => !region.IsTransitioning && tabs.SelectedItem == home, "a vetoed tab selection");
            Require(region.Current == home && region.History.Count == 0, "A veto changed the region.");
            allow = true;
            Pump(region.ReplaceAsync(NavigationTarget.Borrow<Page>(other)));
            Require(tabs.SelectedItem == other && !region.CanGoBack, "External navigation did not select its tab.");
            Drain();
            var host = Descendant<NavigationHost>(tabs);
            Require(host?.Region == region && ViewOf(host!) is TextBox { DataContext: var data } && data == other,
                "The default tab template did not present the shared region through NavigationHost.");

            var custom = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(TextBlock)) };
            tabs.ContentTemplate = custom;
            NavigationSelector.SetRegion(tabs, null);
            Require(ReferenceEquals(tabs.ContentTemplate, custom), "Detaching overwrote an application content template.");
        }
        finally { window.Close(); }
    }

    [Test, DisplayName("rapid selection and late guard"), NotInParallel("WpfSuite", Order = 22)]
    public void RapidSelectionAndLateGuard()
    {
        using var fixture = new NavFixture();
        var calls = 0;
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var home = new GuardedPage("home", () => ++calls == 1 ? new(first.Task) : ValueTask.FromResult(true));
        var middle = new Page("middle");
        var last = new Page("last");
        var region = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Borrow<Page>(home));
        var committed = new List<Page?>();
        region.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(region.Current)) committed.Add(region.Current);
        };
        var tabs = new TabControl { ItemsSource = new Page[] { home, middle, last } };
        NavigationSelector.SetRegion(tabs, region);
        var window = ShowWindow(tabs, scope => AddTemplates(scope, typeof(Page)));
        try
        {
            tabs.SelectedItem = middle;
            PumpUntil(() => calls == 1, "the first selection guard");
            tabs.SelectedItem = last;
            Require(tabs.SelectedItem == last && region.Current == home && calls == 1,
                "Selecting the latest tab bypassed the still-running guard or lost the user's choice.");
            // The shared engine serializes a region's guards. A superseded hook can ignore its
            // token, but the next transition waits for it rather than running another guard concurrently.
            first.SetResult(true);
            PumpUntil(() => region.Current == last && !region.IsTransitioning, "the latest selected tab");
            Pump(fixture.Navigator.WhenIdleAsync().AsTask());
            Require(tabs.SelectedItem == last && region.Current == last && !region.CanGoBack,
                "A late superseded selection overwrote the latest tab or built history.");
            Require(committed.SequenceEqual([last]) && calls == 2, "The superseded tab committed or its guard overlapped the next one.");
            Require(fixture.Logs.Count(1087) == 0, "Ordinary selection supersession was logged as an exception.");
        }
        finally { first.TrySetResult(false); window.Close(); }
    }

    [Test, DisplayName("selector unload and remount"), NotInParallel("WpfSuite", Order = 23)]
    public void UnloadCancelsAndRemounts()
    {
        using var fixture = new NavFixture();
        var calls = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var home = new GuardedPage("home", () => { calls++; return new(gate.Task); });
        var other = new Page("other");
        var region = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Borrow<Page>(home));
        var tabs = new TabControl { ItemsSource = new Page[] { home, other } };
        NavigationSelector.SetRegion(tabs, region);
        var window = ShowWindow(tabs, scope => AddTemplates(scope, typeof(Page)));
        try
        {
            tabs.SelectedItem = other;
            PumpUntil(() => calls == 1, "the pending selection");
            window.Content = null;
            Drain();
            gate.SetResult(true);
            Pump(fixture.Navigator.WhenIdleAsync().AsTask());
            Require(region.Current == home, "Unloading allowed a pending selector request to commit.");
            window.Content = tabs;
            Drain();
            Require(tabs.SelectedItem == home, "Remount did not restore committed selection.");
            tabs.SelectedItem = other;
            PumpUntil(() => region.Current == other && !region.IsTransitioning, "selection after remount");
            Require(calls == 2, "Remount attached duplicate selection handlers.");
        }
        finally { gate.TrySetResult(false); window.Close(); }
    }

    [Test, DisplayName("selecting current cancels without replacing"), NotInParallel("WpfSuite", Order = 24)]
    public void SelectingCurrentCancelsWithoutReplacing()
    {
        using var fixture = new NavFixture();
        var calls = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var home = new GuardedPage("home", () => { calls++; return new(gate.Task); });
        var other = new Page("other");
        var region = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Borrow<Page>(home));
        var initialEntry = region.CurrentEntry;
        var tabs = new TabControl { ItemsSource = new Page[] { home, other } };
        NavigationSelector.SetRegion(tabs, region);
        var window = ShowWindow(tabs, scope => AddTemplates(scope, typeof(Page)));
        try
        {
            tabs.SelectedItem = other;
            PumpUntil(() => calls == 1, "the selection guard");
            tabs.SelectedItem = home;
            gate.SetResult(true);
            Pump(fixture.Navigator.WhenIdleAsync().AsTask());
            Drain();
            Require(tabs.SelectedItem == home && region.CurrentEntry == initialEntry && calls == 1,
                "Selecting the committed tab did not cancel the pending choice or replaced its entry.");
        }
        finally { gate.TrySetResult(false); window.Close(); }
    }

    [Test, DisplayName("invalid selection restores and logs"), NotInParallel("WpfSuite", Order = 25)]
    public void InvalidSelectionRestoresAndLogs()
    {
        using var fixture = new NavFixture();
        var home = new Page("home");
        var wrong = new object();
        var region = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Borrow(home));
        var list = new ListBox { ItemsSource = new object[] { home, wrong } };
        NavigationSelector.SetRegion(list, region);
        var window = ShowWindow(list);
        try
        {
            list.SelectedItem = wrong;
            Drain();
            Require(list.SelectedItem == home && region.Current == home && fixture.Logs.Count(1087) == 1,
                "Invalid selected content was not rejected, restored and observed.");
            Require(Throws<ArgumentException>(() => NavigationSelector.SetRegion(new ListBox { SelectionMode = SelectionMode.Multiple }, region)),
                "Multiple selection was accepted as a navigation selection.");
        }
        finally { window.Close(); }
    }

    // A selection that ends after its dispatcher shut down skips the UI reset and disposes its token
    // source. Detaching later must not cancel that disposed source.
    [Test, DisplayName("selector detach after dispatcher shutdown"), NotInParallel("WpfSuite", Order = 26)]
    public void DetachAfterShutdownEndedSelection()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var fixture = new NavFixture();
                var calls = 0;
                var home = new GuardedPage("home", () => { calls++; return new(gate.Task); });
                var other = new Page("other");
                var region = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Borrow<Page>(home));
                var tabs = new TabControl { ItemsSource = new Page[] { home, other } };
                NavigationSelector.SetRegion(tabs, region);
                ShowWindow(tabs, scope => AddTemplates(scope, typeof(Page)));
                tabs.SelectedItem = other;
                PumpUntil(() => calls == 1, "the pending selection");
                var selection = NavigationSelector.SelectionCompletion(tabs);
                dispatcher.BeginInvoke(() => dispatcher.InvokeShutdown());
                Dispatcher.Run();
                gate.SetResult(true);
                Require(selection.Wait(WaitLimit), "The selection did not end after dispatcher shutdown.");
                NavigationSelector.SetRegion(tabs, null);
            }
            catch (Exception failure) { error = failure; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { Require(thread.Join(WaitLimit), "The secondary UI thread did not stop."); }
        finally { gate.TrySetResult(false); }
        if (error is not null) throw new InvalidOperationException("Detaching after shutdown failed.", error);
    }

    // #648: static TabItems map to the selection's regions by position; the default content template presents the
    // selected region, and a programmatic SelectAsync selects its item.
    [Test, DisplayName("selection with static tab items"), NotInParallel("WpfSuite", Order = 67)]
    public void SelectionWithStaticTabItems()
    {
        using var fixture = new NavFixture();
        var page = new TabsPage("page", fixture.Navigator, new Page("first"), new Page("second"));
        var selection = page.Selection;
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "First" });
        tabs.Items.Add(new TabItem { Header = "Second" });
        NavigationSelector.SetSelection(tabs, selection);
        var window = ShowWindow(tabs, scope => AddTemplates(scope, typeof(Page)));
        try
        {
            Require(tabs.SelectedIndex == 0 && tabs.ContentTemplate is not null && NavigationSelector.GetSelection(tabs) == selection,
                "The selector did not follow the initial selection or set a default content template.");
            Require(Presented(tabs) == (page.Tabs[0], "first"), $"The default tab template presented {Presented(tabs)}.");

            tabs.SelectedIndex = 1;
            PumpUntil(() => selection.SelectedIndex == 1 && !selection.IsTransitioning, "the second tab");
            Drain();
            Require(Presented(tabs) == (page.Tabs[1], "second") && page.Tabs[0].Current is Page { Name: "first" },
                $"Selecting the second tab presented {Presented(tabs)} or changed the first tab's stack.");

            Pump(selection.SelectAsync(page.Tabs[0]).AsTask());
            Drain();
            Require(tabs.SelectedIndex == 0 && Presented(tabs) == (page.Tabs[0], "first"),
                "A programmatic selection did not select its tab.");
            Require(fixture.Logs.Count(1087) == 0, "An ordinary selection was logged as a failure.");

            NavigationSelector.SetSelection(tabs, null);
            Require(tabs.ContentTemplate is null, "Detaching kept the default content template.");
        }
        finally { window.Close(); }
    }

    // #648: items bound to Regions map one to one and follow Add and RemoveAsync without a selection of their own.
    [Test, DisplayName("selection items bound to Regions"), NotInParallel("WpfSuite", Order = 68)]
    public void SelectionItemsBoundToRegions()
    {
        using var fixture = new NavFixture();
        var guards = 0;
        var page = new TabsPage("page", fixture.Navigator,
            new GuardedPage("first", () => { guards++; return ValueTask.FromResult(true); }), new Page("second"));
        var selection = page.Selection;
        var tabs = new TabControl();
        tabs.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(NavigationSelection.Regions)) { Source = selection });
        NavigationSelector.SetSelection(tabs, selection);
        var window = ShowWindow(tabs, scope => AddTemplates(scope, typeof(Page), typeof(GuardedPage)));
        try
        {
            Require(tabs.Items.Count == 2 && tabs.SelectedItem == page.Tabs[0] && Presented(tabs) == (page.Tabs[0], "first"),
                "The selector did not present the initially selected region.");
            tabs.SelectedItem = page.Tabs[1];
            PumpUntil(() => selection.Selected == page.Tabs[1] && !selection.IsTransitioning, "the second region");
            Drain();
            Require(guards == 1 && Presented(tabs) == (page.Tabs[1], "second"), "Selecting a region item did not ask once and present it.");

            var third = fixture.Navigator.CreateRegion(page, NavigationTarget.Own<object>(new Page("third")));
            selection.Add(third);
            Drain();
            Require(tabs.Items.Count == 3 && tabs.SelectedItem == page.Tabs[1] && selection.Selected == page.Tabs[1] && !selection.IsBusy,
                "Adding a region changed the selected item or started a selection.");

            // The removed region was selected: the selection selects the next one, and the control follows it.
            var removed = Pump(selection.RemoveAsync(page.Tabs[1]));
            Pump(fixture.Navigator.WhenIdleAsync().AsTask());
            Drain();
            Require(removed is NavigationResult<object>.Committed && selection.Selected == third && tabs.Items.Count == 2
                    && tabs.SelectedItem == third && Presented(tabs) == (third, "third"),
                $"Removing the selected region gave {removed}; the control selects {tabs.SelectedItem}, the selection {selection.Selected}.");
            Require(guards == 1 && fixture.Logs.Count(1087) == 0, "Following the removal started a selection of the control's own.");
        }
        finally { window.Close(); }
    }

    // #648: a veto restores the control to the committed selection; the region left keeps its page.
    [Test, DisplayName("selection veto restores the tab"), NotInParallel("WpfSuite", Order = 69)]
    public void SelectionVetoRestores()
    {
        using var fixture = new NavFixture();
        var kinds = new List<NavigationDepartureKind>();
        var allow = false;
        var editor = new DeparturePage("editor", (departure, _) =>
        {
            kinds.Add(departure.Kind);
            return ValueTask.FromResult(allow);
        });
        var page = new TabsPage("page", fixture.Navigator, editor, new Page("second"));
        var selection = page.Selection;
        var tabs = new TabControl { ItemsSource = new[] { "Editor", "Second" } };
        NavigationSelector.SetSelection(tabs, selection);
        var window = ShowWindow(tabs, scope => AddTemplates(scope, typeof(Page), typeof(DeparturePage)));
        try
        {
            tabs.SelectedIndex = 1;
            PumpUntil(() => !selection.IsBusy && tabs.SelectedIndex == 0, "the vetoed switch");
            Drain();
            Require(selection.SelectedIndex == 0 && kinds is [NavigationDepartureKind.Retain] && page.Tabs[0].Current == editor
                    && Presented(tabs) == (page.Tabs[0], "editor"),
                $"A veto changed the selection or the page ({string.Join(", ", kinds)}).");

            allow = true;
            tabs.SelectedIndex = 1;
            PumpUntil(() => selection.SelectedIndex == 1 && !selection.IsBusy, "the allowed switch");
            Drain();
            Require(tabs.SelectedIndex == 1 && kinds is [NavigationDepartureKind.Retain, NavigationDepartureKind.Retain]
                    && page.Tabs[0].Current == editor && Presented(tabs) == (page.Tabs[1], "second"),
                "The allowed switch didn't select the tab or left the page's stack.");
            Require(fixture.Logs.Count(1087) == 0, "A veto was logged as a failure.");
        }
        finally { window.Close(); }
    }

    // #648: the latest click wins; a superseded switch whose guard answers late commits nothing.
    [Test, DisplayName("selection rapid clicks"), NotInParallel("WpfSuite", Order = 70)]
    public void SelectionRapidClicks()
    {
        using var fixture = new NavFixture();
        var calls = 0;
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = new TabsPage("page", fixture.Navigator,
            new GuardedPage("home", () => ++calls == 1 ? new(first.Task) : ValueTask.FromResult(true)), new Page("middle"), new Page("last"));
        var selection = page.Selection;
        var committed = new List<int>();
        selection.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NavigationSelection.SelectedIndex)) committed.Add(selection.SelectedIndex);
        };
        var tabs = new TabControl { ItemsSource = new[] { "Home", "Middle", "Last" } };
        NavigationSelector.SetSelection(tabs, selection);
        var window = ShowWindow(tabs, scope => AddTemplates(scope, typeof(Page), typeof(GuardedPage)));
        try
        {
            tabs.SelectedIndex = 1;
            PumpUntil(() => calls == 1, "the first switch's guard");
            tabs.SelectedIndex = 2;
            Require(tabs.SelectedIndex == 2 && selection.SelectedIndex == 0 && calls == 1,
                "Selecting the latest tab bypassed the still-running guard or lost the user's choice.");
            first.SetResult(true);
            PumpUntil(() => selection.SelectedIndex == 2 && !selection.IsTransitioning, "the latest selected tab");
            Pump(fixture.Navigator.WhenIdleAsync().AsTask());
            Drain();
            Require(tabs.SelectedIndex == 2 && committed.SequenceEqual([2]) && calls == 2,
                $"The superseded switch committed or its guard overlapped the next one ({string.Join(", ", committed)}; {calls} guards).");
            Require(fixture.Logs.Count(1087) == 0, "Ordinary selection supersession was logged as an exception.");
        }
        finally { first.TrySetResult(false); window.Close(); }
    }

    // #648: unloading cancels a switch whose guard runs; loading again shows the committed selection, also one made
    // while unloaded, without duplicate handlers.
    [Test, DisplayName("selection unload and reload"), NotInParallel("WpfSuite", Order = 71)]
    public void SelectionUnloadAndReload()
    {
        using var fixture = new NavFixture();
        var calls = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = new TabsPage("page", fixture.Navigator, new GuardedPage("home", () => { calls++; return new(gate.Task); }), new Page("other"));
        var selection = page.Selection;
        var tabs = new TabControl { ItemsSource = new[] { "Home", "Other" } };
        NavigationSelector.SetSelection(tabs, selection);
        var window = ShowWindow(tabs, scope => AddTemplates(scope, typeof(Page), typeof(GuardedPage)));
        try
        {
            tabs.SelectedIndex = 1;
            PumpUntil(() => calls == 1, "the pending switch");
            window.Content = null;
            Drain();
            gate.SetResult(true);
            Pump(fixture.Navigator.WhenIdleAsync().AsTask());
            Require(selection.SelectedIndex == 0, "Unloading allowed a pending switch to commit.");

            var programmatic = Pump(selection.SelectAsync(page.Tabs[1]));
            Require(programmatic is NavigationResult<object>.Committed && calls == 2, $"The switch while unloaded gave {programmatic}.");
            window.Content = tabs;
            Drain();
            Require(tabs.SelectedIndex == 1, "Reloading did not show the committed selection.");

            tabs.SelectedIndex = 0;
            PumpUntil(() => selection.SelectedIndex == 0 && !selection.IsBusy, "the first tab after reload");
            tabs.SelectedIndex = 1;
            PumpUntil(() => selection.SelectedIndex == 1 && !selection.IsBusy, "the second tab after reload");
            Require(calls == 3, $"Reloading attached duplicate selection handlers ({calls} guards).");
        }
        finally { gate.TrySetResult(false); window.Close(); }
    }

    // #648: a control takes Region or Selection, not both; the rejected value doesn't detach the other adapter.
    [Test, DisplayName("selector Region and Selection together throw"), NotInParallel("WpfSuite", Order = 72)]
    public void RegionAndSelectionTogetherThrow()
    {
        using var fixture = new NavFixture();
        var page = new TabsPage("page", fixture.Navigator, new Page("first"), new Page("second"));
        var selection = page.Selection;
        var region = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Borrow(new Page("borrowed")));
        var tabs = new TabControl { ItemsSource = new[] { "First", "Second" } };
        NavigationSelector.SetSelection(tabs, selection);
        Require(Throws<InvalidOperationException>(() => NavigationSelector.SetRegion(tabs, region)),
            "Region was accepted on a control with a Selection.");
        NavigationSelector.SetRegion(tabs, null);
        var list = new ListBox();
        NavigationSelector.SetRegion(list, region);
        Require(Throws<InvalidOperationException>(() => NavigationSelector.SetSelection(list, selection)),
            "Selection was accepted on a control with a Region.");
        Require(Throws<ArgumentException>(() => NavigationSelector.SetSelection(new ListBox { SelectionMode = SelectionMode.Multiple }, selection)),
            "Multiple selection was accepted as a navigation selection.");

        var window = ShowWindow(tabs, scope => AddTemplates(scope, typeof(Page)));
        try
        {
            tabs.SelectedIndex = 1;
            PumpUntil(() => selection.SelectedIndex == 1 && !selection.IsBusy, "the switch after the rejected Region");
            Require(tabs.SelectedIndex == 1 && region.Current is { Name: "borrowed" }, "The rejected Region detached the selection.");
        }
        finally { window.Close(); }
    }

    // The region and the page that the TabControl's default content template presents.
    private static (NavigationRegion? Region, string? Page) Presented(TabControl tabs)
    {
        tabs.UpdateLayout();
        var host = Descendant<NavigationHost>(tabs);
        return (host?.Region, host is not null && ViewOf(host) is TextBox { DataContext: Page page } ? page.Name : null);
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (Descendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
