using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static Runic.Navigation.Wpf.Tests.Ui;

namespace Runic.Navigation.Wpf.Tests;

internal static class SelectorTests
{
    public static void GuardVetoAndExternalSelection()
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

    public static void RapidSelectionAndLateGuard()
    {
        using var fixture = new NavFixture();
        var calls = 0;
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var home = new GuardedPage("home", () => ++calls == 1 ? new(first.Task) : ValueTask.FromResult(true));
        var middle = new Page("middle");
        var last = new Page("last");
        var region = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Borrow<Page>(home));
        var tabs = new TabControl { ItemsSource = new Page[] { home, middle, last } };
        NavigationSelector.SetRegion(tabs, region);
        var window = ShowWindow(tabs, scope => AddTemplates(scope, typeof(Page)));
        try
        {
            tabs.SelectedItem = middle;
            PumpUntil(() => calls == 1, "the first selection guard");
            tabs.SelectedItem = last;
            PumpUntil(() => region.Current == last && !region.IsTransitioning, "the latest selected tab");
            first.SetResult(true); // A superseded consumer hook ignores its token and answers late.
            Pump(fixture.Navigator.WhenIdleAsync().AsTask());
            Require(tabs.SelectedItem == last && region.Current == last && !region.CanGoBack,
                "A late superseded selection overwrote the latest tab or built history.");
            Require(fixture.Logs.Count(1087) == 0, "Ordinary selection supersession was logged as an exception.");
        }
        finally { first.TrySetResult(false); window.Close(); }
    }

    public static void UnloadCancelsAndRemounts()
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

    public static void SelectingCurrentCancelsWithoutReplacing()
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

    public static void InvalidSelectionRestoresAndLogs()
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
