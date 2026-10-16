using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TUnit.Core;
using TUnit.Core.Executors;
using static Runic.Navigation.Wpf.Tests.Ui;

namespace Runic.Navigation.Wpf.Tests;

// W250-047: GuardWindowClose belongs to the window and lists regions, so it outlives an unloaded or discarded host.
[TestExecutor<WpfDispatcherExecutor>]
internal sealed class WindowCloseGuardTests
{
    // N1: a guarding host on a TabControl tab unloads when another tab is selected, and the window still asks its region.
    // A region of another owner that a tab only refers to is not the tab's: selecting another tab asks no guard (W260-009).
    [Test, DisplayName("GuardWindowClose on an unselected tab"), NotInParallel("WpfSuite", Order = 43)]
    public void UnselectedTab()
    {
        using var fixture = new NavFixture();
        var pages = fixture.Region(NavigationTarget.Own<object>(new Page("list")));
        var answers = new Queue<TaskCompletionSource<bool>>();
        var guards = 0;
        var editor = new GuardedPage("editor", () =>
        {
            guards++;
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            answers.Enqueue(answer);
            return new ValueTask<bool>(answer.Task);
        });
        Pump(pages.PushAsync(NavigationTarget.Own<object>(editor)));
        var customers = new PagesTab("customers", pages);
        var reports = new Page("reports");
        var tab = fixture.Navigator.CreateRegion<object>(new object(), NavigationTarget.Borrow<object>(customers));
        // As in the trial's shell: headers show the name, so only the selected tab's content uses the tab's template.
        var tabs = new TabControl { ItemsSource = new object[] { customers, reports }, DisplayMemberPath = nameof(PagesTab.Name) };
        NavigationSelector.SetRegion(tabs, tab);
        var window = ShowWindow(tabs, window =>
        {
            AddTemplates(window, typeof(Page), typeof(GuardedPage));
            window.Resources.Add(new DataTemplateKey(typeof(PagesTab)), GuardedPagesTemplate());
        });
        try
        {
            var hosts = HostsOf(window, pages);
            Require(hosts is [{ IsLoaded: true }] && ViewOf(hosts[0]) is TextBox { DataContext: GuardedPage },
                $"The Customers tab doesn't present its page region: {hosts.Count} host(s), loaded: {string.Join(", ", hosts.Select(host => host.IsLoaded))}, "
                + $"view: {(hosts.Count > 0 ? ViewOf(hosts[0])?.DataContext : null)}.");
            var shown = hosts[0];

            Select(tabs, tab, reports);
            Require(guards == 0 && ReferenceEquals(pages.Current, editor) && pages.CanGoBack,
                "Selecting another tab asked the page's guard or left the page.");
            Require(!shown.IsLoaded && HostsOf(window, pages).Count == 0, "The Customers tab's host is still loaded; the case needs it unloaded.");
            Require(WindowCloseGuard.Lists(window, pages), "The window stopped guarding the unselected tab's region.");

            // Keep: the window stays open with the draft.
            window.Close();
            Require(IsOpen(window), "The window closed before the unselected tab's guard answered.");
            PumpUntil(() => answers.Count == 1, "the unselected tab's guard");
            answers.Dequeue().SetResult(false);
            PumpUntil(() => !pages.IsTransitioning, "the kept draft");
            Drain();
            Require(IsOpen(window) && ReferenceEquals(pages.Current, editor) && pages.CanGoBack && guards == 1,
                "Keeping the draft closed the window or lost the page.");

            // Back on the tab, a new host presents the kept page; leaving it again asks nothing.
            Select(tabs, tab, customers);
            Require(HostsOf(window, pages) is [{ IsLoaded: true } again] && !ReferenceEquals(again, shown)
                    && ViewOf(again) is TextBox { DataContext: GuardedPage page } && ReferenceEquals(page, editor),
                "Returning to the tab didn't present the kept page.");
            Select(tabs, tab, reports);
            Require(guards == 1 && WindowCloseGuard.CountOf(window) == 1, "Switching tabs asked a guard or listed the region twice.");

            // Discard: the region is cleared and the window closes.
            window.Close();
            PumpUntil(() => answers.Count == 1, "the second guard");
            answers.Dequeue().SetResult(true);
            PumpUntil(() => !IsOpen(window), "the window to close after the discard");
            Require(pages.CurrentEntry is null && !pages.CanGoBack && guards == 2, "The window closed without clearing the unselected tab's region.");
            Require(!WindowCloseGuard.IsListed(pages), "A closed window still lists its region.");
        }
        finally
        {
            WindowCloseGuard.Remove(pages);
            if (IsOpen(window)) window.Close();
        }
    }

    // W260-009 (F13): selecting another tab asks the current page of each region the tab's model owns, as a retained
    // departure. A page that keeps its draft keeps the tab selected; one that allows leaves the page in its region, and
    // the window's close still asks it.
    [Test, DisplayName("Tab switch asks the tab's page regions"), NotInParallel("WpfSuite", Order = 42)]
    public void TabSwitchAsksPages()
    {
        using var fixture = new NavFixture();
        var customers = new PagesTab("customers", fixture.Navigator, NavigationTarget.Own<object>(new Page("list")));
        var pages = customers.Pages;
        var asked = new List<(NavigationDepartureKind Kind, CancellationToken Token, TaskCompletionSource<bool> Answer)>();
        var editor = new DeparturePage("editor", (departure, token) =>
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => answer.TrySetCanceled(token));
            asked.Add((departure.Kind, token, answer));
            return new ValueTask<bool>(answer.Task);
        });
        Pump(pages.PushAsync(NavigationTarget.Own<object>(editor)));
        var reports = new Page("reports");
        var settings = new Page("settings");
        var tab = fixture.Navigator.CreateRegion<object>(new object(), NavigationTarget.Borrow<object>(customers));
        var tabs = new TabControl { ItemsSource = new object[] { customers, reports, settings }, DisplayMemberPath = nameof(PagesTab.Name) };
        NavigationSelector.SetRegion(tabs, tab);
        var window = ShowWindow(tabs, window =>
        {
            AddTemplates(window, typeof(Page), typeof(DeparturePage));
            window.Resources.Add(new DataTemplateKey(typeof(PagesTab)), GuardedPagesTemplate());
        });
        try
        {
            Require(HostsOf(window, pages) is [{ IsLoaded: true } shown] && ViewOf(shown) is TextBox { DataContext: DeparturePage },
                "The Customers tab doesn't present its page region.");

            // Keep: the tab stays selected, with its page and its loaded host.
            tabs.SelectedItem = reports;
            PumpUntil(() => asked.Count == 1, "the page's guard on the tab switch");
            Require(asked[0].Kind == NavigationDepartureKind.Retain && tab.IsTransitioning && ReferenceEquals(tab.Current, customers),
                $"The tab switch asked the page as {asked[0].Kind} or changed the tab before it answered.");
            asked[0].Answer.SetResult(false);
            PumpUntil(() => !tab.IsTransitioning && ReferenceEquals(tabs.SelectedItem, customers), "the kept tab");
            Drain();
            Require(ReferenceEquals(tab.Current, customers) && ReferenceEquals(pages.Current, editor) && pages.CanGoBack
                    && HostsOf(window, pages) is [{ IsLoaded: true } kept] && ViewOf(kept) is TextBox { DataContext: DeparturePage },
                "A page that kept its draft didn't keep the tab selected and presented.");

            // A later selection supersedes the pending one and cancels its guard; the latest choice is asked again.
            tabs.SelectedItem = reports;
            PumpUntil(() => asked.Count == 2, "the guard of the first selection");
            tabs.SelectedItem = settings;
            PumpUntil(() => asked.Count == 3, "the guard of the later selection");
            Require(asked[1].Token.IsCancellationRequested && asked[1].Answer.Task.IsCanceled && ReferenceEquals(tab.Current, customers),
                "The later selection didn't cancel the superseded guard.");

            // Allow: the switch commits, and the page stays in its region with its draft.
            asked[2].Answer.SetResult(true);
            PumpUntil(() => ReferenceEquals(tab.Current, settings) && !tab.IsTransitioning, "the allowed tab switch");
            Drain();
            Require(ReferenceEquals(tabs.SelectedItem, settings) && ReferenceEquals(pages.Current, editor) && pages.CanGoBack
                    && HostsOf(window, pages).Count == 0 && WindowCloseGuard.Lists(window, pages),
                "The allowed switch didn't select the tab, left the page, or stopped the window guarding it.");

            // The window's close clears the region: the page retires, and its guard is asked again.
            window.Close();
            PumpUntil(() => asked.Count == 4, "the page's guard on the window's close");
            Require(asked[3].Kind == NavigationDepartureKind.Retire && IsOpen(window), "The window's close didn't ask the page to retire.");
            asked[3].Answer.SetResult(true);
            PumpUntil(() => !IsOpen(window), "the window to close");
            Require(pages.CurrentEntry is null && !pages.CanGoBack,
                "The window closed without clearing the tab's region.");
        }
        finally
        {
            WindowCloseGuard.Remove(pages);
            foreach (var (_, _, answer) in asked) answer.TrySetResult(false);
            if (IsOpen(window)) window.Close();
        }
    }

    // The window holds no host and holds its regions weakly; a region holds nothing of the window.
    [Test, DisplayName("GuardWindowClose keeps nothing alive"), NotInParallel("WpfSuite", Order = 44)]
    public void KeepsNothingAlive()
    {
        using var fixture = new NavFixture();
        var region = fixture.Region(NavigationTarget.Own<object>(new DocumentViewModel("kept")));
        var window = ShowWindow(null, window => window.Resources.Add(new DataTemplateKey(typeof(DocumentViewModel)), BorderTemplate()));
        try
        {
            // A discarded host is collected, and the window still guards its region.
            var host = MountAndDiscard(window, region);
            Collect(() => !host.IsAlive);
            Require(!host.IsAlive, "The window kept a discarded guarding host alive.");
            Require(WindowCloseGuard.Lists(window, region), "The window stopped guarding the discarded host's region.");

            // A child region that ends while the window is open is collected and no longer listed.
            var parent = new Page("parent");
            Pump(region.PushAsync(NavigationTarget.Own<object>(parent)));
            var child = MountChild(window, fixture.Navigator, parent);
            Pump(region.BackAsync());
            Collect(() => !child.IsAlive);
            Require(!child.IsAlive, "The window kept a child region alive after it closed.");
            Require(WindowCloseGuard.CountOf(window) == 1, "The window still lists a region that ended.");
        }
        finally
        {
            WindowCloseGuard.Remove(region);
            if (IsOpen(window)) window.Close();
        }

        // A closed window is collected while its region lives on, and no window lists the region.
        var closed = ShowAndClose(region);
        Collect(() => !closed.IsAlive);
        Require(!closed.IsAlive, "A guarded region kept its closed window alive.");
        Require(!WindowCloseGuard.IsListed(region), "A closed window still lists its region.");
        GC.KeepAlive(region);
    }

    // A host that moves to another window takes its region along; regions are asked one after another, and a veto stops.
    [Test, DisplayName("GuardWindowClose follows the host and asks regions in order"), NotInParallel("WpfSuite", Order = 45)]
    public void FollowsHostAndAsksInOrder()
    {
        using var fixture = new NavFixture();
        var asked = new List<string>();
        var allow = false;
        var region = fixture.Region(NavigationTarget.Own<object>(new GuardedPage("moved", () =>
        {
            asked.Add("moved");
            return ValueTask.FromResult(true);
        })));
        var other = fixture.Region(NavigationTarget.Own<object>(new GuardedPage("other", () =>
        {
            asked.Add("other");
            return ValueTask.FromResult(allow);
        })));
        var host = new NavigationHost { Region = region, GuardWindowClose = true };
        var first = ShowWindow(host, window => AddTemplates(window, typeof(GuardedPage)));
        var panel = new StackPanel();
        var second = ShowWindow(panel, window => AddTemplates(window, typeof(GuardedPage)));
        try
        {
            panel.Children.Add(new NavigationHost { Region = other, GuardWindowClose = true });
            Drain();
            Require(WindowCloseGuard.Lists(first, region) && WindowCloseGuard.Lists(second, other), "The hosts' windows don't guard their regions.");
            first.Content = null;
            panel.Children.Add(host);
            Drain();
            Require(!WindowCloseGuard.Lists(first, region) && WindowCloseGuard.Lists(second, region),
                "The region didn't follow its host to the other window.");
            first.Close();
            Require(!IsOpen(first) && asked.Count == 0, "Closing the window the host left asked its region.");

            // Loaded first, asked first; the veto keeps the window and the later region.
            second.Close();
            PumpUntil(() => asked.Count == 1 && !other.IsTransitioning, "the first region's guard");
            Drain();
            Require(IsOpen(second) && asked is ["other"] && region.CurrentEntry is not null && other.CurrentEntry is not null,
                $"A veto closed the window or asked the next region: {string.Join(", ", asked)}.");

            allow = true;
            second.Close();
            PumpUntil(() => !IsOpen(second), "the window to close");
            Require(asked is ["other", "other", "moved"] && region.CurrentEntry is null && other.CurrentEntry is null,
                $"The regions were asked as {string.Join(", ", asked)}.");
        }
        finally
        {
            WindowCloseGuard.Remove(region);
            WindowCloseGuard.Remove(other);
            if (IsOpen(first)) first.Close();
            if (IsOpen(second)) second.Close();
        }
    }

    private static void Select(TabControl tabs, NavigationRegion<object> tab, object item)
    {
        tabs.SelectedItem = item;
        PumpUntil(() => ReferenceEquals(tab.Current, item) && !tab.IsTransitioning, $"the {item} tab");
        Drain();
    }

    // #358: once a vetoed clear stops transitioning, the region is idle, but the check that ran the clear ends only when
    // its continuation reaches the dispatcher. A close in that gap (here from the IsTransitioning notification, which is
    // posted before the clear's result) used to join the finished check and was dropped: cancelled, with no question.
    [Test, DisplayName("GuardWindowClose asks again after a vetoed clear"), NotInParallel("WpfSuite", Order = 46)]
    public void CloseAfterVetoedClear()
    {
        using var fixture = new NavFixture();
        var region = fixture.Region(NavigationTarget.Own<object>(new Page("home")));
        var answers = new Queue<TaskCompletionSource<bool>>();
        var guards = 0;
        var editor = new GuardedPage("editor", () =>
        {
            guards++;
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            answers.Enqueue(answer);
            return new ValueTask<bool>(answer.Task);
        });
        Pump(region.PushAsync(NavigationTarget.Own<object>(editor)));
        var host = new NavigationHost { Region = region, GuardWindowClose = true };
        var window = ShowWindow(host, window => AddTemplates(window, typeof(Page), typeof(GuardedPage)));
        var closedWhenIdle = false;
        void CloseWhenIdle(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(INavigationRegion.IsTransitioning) || region.IsTransitioning || closedWhenIdle) return;
            closedWhenIdle = true;
            window.Close();
        }
        try
        {
            window.Close();
            PumpUntil(() => answers.Count == 1, "the first guard");
            region.PropertyChanged += CloseWhenIdle;
            answers.Dequeue().SetResult(false);
            PumpUntil(() => closedWhenIdle, "the close from the end of the vetoed clear");
            region.PropertyChanged -= CloseWhenIdle;
            PumpUntil(() => answers.Count == 1, "the guard of the close after the vetoed clear",
                () => $"guards asked: {guards}, open: {IsOpen(window)}, transitioning: {region.IsTransitioning}");
            Require(IsOpen(window) && guards == 2 && ReferenceEquals(region.Current, editor), "The vetoed clear lost the window or its entries.");
            answers.Dequeue().SetResult(true);
            PumpUntil(() => !IsOpen(window), "the window to close after the clear");
            Require(region.CurrentEntry is null && guards == 2, $"The window closed without clearing its region once ({guards} guards asked).");
        }
        finally
        {
            region.PropertyChanged -= CloseWhenIdle;
            host.GuardWindowClose = false;
            if (IsOpen(window)) window.Close();
        }
    }

    // The Customers tab's view: a host of its page region that guards the window's close.
    private static DataTemplate GuardedPagesTemplate()
    {
        var host = new FrameworkElementFactory(typeof(NavigationHost));
        host.SetBinding(NavigationHost.RegionProperty, new Binding(nameof(PagesTab.Pages)));
        host.SetValue(NavigationHost.GuardWindowCloseProperty, true);
        var template = new DataTemplate(typeof(PagesTab)) { VisualTree = host };
        template.Seal();
        return template;
    }

    private static DataTemplate BorderTemplate()
    {
        var template = new DataTemplate(typeof(DocumentViewModel)) { VisualTree = new FrameworkElementFactory(typeof(Border)) };
        template.Seal();
        return template;
    }

    private static List<NavigationHost> HostsOf(DependencyObject root, INavigationRegion region)
    {
        List<NavigationHost> hosts = [];
        Visit(root);
        return hosts;

        void Visit(DependencyObject node)
        {
            if (node is NavigationHost host && ReferenceEquals(host.Region, region)) hosts.Add(host);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
    }

    // WPF releases layout, input and command bookkeeping in idle operations; let them run between collections.
    private static void Collect(Func<bool> collected)
    {
        for (var attempt = 0; attempt < 30 && !collected(); attempt++)
        {
            Drain();
            Pump(Task.Delay(10), "a short delay");
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference MountAndDiscard(Window window, INavigationRegion region)
    {
        var host = new NavigationHost { Region = region, GuardWindowClose = true };
        window.Content = host;
        Drain();
        Require(host.IsLoaded && ViewOf(host) is Border && WindowCloseGuard.Lists(window, region), "The window doesn't guard the host's region.");
        window.Content = null;
        Drain();
        return new WeakReference(host);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference MountChild(Window window, RunicNavigator navigator, object parent)
    {
        var child = navigator.CreateRegion<object>(parent, NavigationTarget.Own<object>(new DocumentViewModel("child")));
        var host = new NavigationHost { Region = child, GuardWindowClose = true };
        window.Content = host;
        Drain();
        Require(host.IsLoaded && WindowCloseGuard.CountOf(window) == 2 && WindowCloseGuard.Lists(window, child), "The window doesn't guard the child region.");
        window.Content = null;
        Drain();
        return new WeakReference(child);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ShowAndClose(INavigationRegion region)
    {
        var window = ShowWindow(new NavigationHost { Region = region, GuardWindowClose = true },
            window => window.Resources.Add(new DataTemplateKey(typeof(DocumentViewModel)), BorderTemplate()));
        Require(WindowCloseGuard.Lists(window, region), "The window doesn't guard its host's region.");
        window.Close();
        PumpUntil(() => !IsOpen(window), "the guarded window to close");
        // The suite's record of loaded windows would keep it alive.
        LoadedWindows.Remove(window);
        return new WeakReference(window);
    }
}
