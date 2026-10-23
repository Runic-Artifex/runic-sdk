using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TUnit.Core;
using TUnit.Core.Executors;
using static Runic.Navigation.Wpf.Tests.Ui;
using DisplayNameAttribute = TUnit.Core.DisplayNameAttribute;

namespace Runic.Navigation.Wpf.Tests;

// #418: NavigationHost.RetainViews keeps the view of each retained entry and shows it again when the entry returns.
[TestExecutor<WpfDispatcherExecutor>]
internal sealed class RetainViewsTests
{
    // Back and BackTo show the same view with its state; the locator is asked once per entry; retired entries' views are
    // dropped; a view is never shown for two entries.
    [Test, DisplayName("RetainViews keeps retained entries' views"), NotInParallel("WpfSuite", Order = 56)]
    public void KeepsRetainedViews()
    {
        using var fixture = new NavFixture();
        var region = fixture.Region();
        var locator = new AskCounter();
        var host = new NavigationHost { Region = region, RetainViews = true, ViewLocator = locator, EmptyContent = "empty" };
        var window = ShowWindow(host, window => AddTemplates(window, typeof(DocumentViewModel)));
        try
        {
            var a = new DocumentViewModel("a");
            Pump(region.PushAsync(NavigationTarget.Own<object>(a)));
            var aEntry = region.CurrentEntry!.Id;
            var viewA = (TextBox)ViewOf(host)!;
            var loads = 0;
            viewA.Loaded += (_, _) => loads++;
            viewA.Text = "typed text";
            viewA.Select(2, 4);

            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("b"))));
            var viewB = (TextBox)ViewOf(host)!;
            Drain();
            Require(!ReferenceEquals(viewA, viewB) && host.KeptViewCount == 1 && !viewA.IsLoaded,
                $"Pushing b gave {host.KeptViewCount} kept views, a loaded: {viewA.IsLoaded}.");

            Pump(region.BackAsync());
            Drain();
            Require(ReferenceEquals(ViewOf(host), viewA) && viewA.Text == "typed text" && viewA.SelectionStart == 2
                && viewA.SelectionLength == 4 && ReferenceEquals(viewA.DataContext, a),
                "Back did not show a's view again with its text, selection and DataContext.");
            Require(locator.Asks(aEntry) == 1 && host.KeptViewCount == 0 && loads == 1 && viewA.IsLoaded,
                $"Back asked the locator {locator.Asks(aEntry)} times for a, kept {host.KeptViewCount} views, raised Loaded {loads} times.");

            // BackTo over two entries: their views are dropped with them.
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("c"))));
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("d"))));
            Require(host.KeptViewCount == 2, $"Two retained entries kept {host.KeptViewCount} views.");
            Pump(region.BackToAsync(aEntry));
            Require(ReferenceEquals(ViewOf(host), viewA) && host.KeptViewCount == 0 && locator.Asks(aEntry) == 1,
                $"BackTo kept {host.KeptViewCount} views or asked the locator again.");

            // One instance pushed twice: two entries, two views; Back shows the first one's.
            var shared = new DocumentViewModel("shared");
            Pump(region.PushAsync(NavigationTarget.Borrow<object>(shared)));
            var firstShared = ViewOf(host);
            Pump(region.PushAsync(NavigationTarget.Borrow<object>(shared)));
            var secondShared = ViewOf(host);
            Require(firstShared is TextBox && secondShared is TextBox && !ReferenceEquals(firstShared, secondShared),
                "One instance pushed twice shared a view.");
            Pump(region.BackAsync());
            Require(ReferenceEquals(ViewOf(host), firstShared), "Back to the first push of the instance showed another view.");
            // Push again: the retired second entry's view is not reused.
            Pump(region.PushAsync(NavigationTarget.Borrow<object>(shared)));
            Require(ViewOf(host) is TextBox third && !ReferenceEquals(third, secondShared) && !ReferenceEquals(third, firstShared),
                "A new entry of the instance reused a retired entry's view.");

            // Replace retires the current entry only; ClearHistory and Clear drop every kept view.
            Pump(region.ReplaceAsync(NavigationTarget.Own<object>(new DocumentViewModel("replaced"))));
            Require(host.KeptViewCount == 2, $"Replace left {host.KeptViewCount} kept views.");
            Pump(region.ClearHistoryAsync());
            Require(host.KeptViewCount == 0, $"ClearHistory left {host.KeptViewCount} kept views.");
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("e"))));
            Require(host.KeptViewCount == 1, $"A push kept {host.KeptViewCount} views.");
            Pump(region.ClearAsync());
            Require(Equals(host.Content, "empty") && host.KeptViewCount == 0, $"Clear left {host.KeptViewCount} kept views.");
        }
        finally { window.Close(); }
    }

    // Set on the window, the option reaches the hosts inside a page's view. A host that loads again with the same region
    // and current entry keeps its view and its own kept views, so the page's child regions keep their state.
    [Test, DisplayName("RetainViews keeps nested hosts' views"), NotInParallel("WpfSuite", Order = 57)]
    public void KeepsNestedHostViews()
    {
        using var fixture = new NavFixture();
        var region = fixture.Region();
        var shell = new ShellViewModel();
        var child = fixture.Navigator.CreateRegion<object>(shell);
        var locator = new ShellLocator();
        var host = new NavigationHost { Region = region, ViewLocator = locator };
        var window = ShowWindow(host, window =>
        {
            AddTemplates(window, typeof(DocumentViewModel));
            NavigationHost.SetRetainViews(window, true);
        });
        try
        {
            Require(host.RetainViews && NavigationHost.GetRetainViews(window), "The host did not inherit RetainViews from the window.");
            Pump(region.PushAsync(NavigationTarget.Own<object>(shell)));
            var shellView = (ShellView)ViewOf(host)!;
            var inner = shellView.Inner;
            inner.Region = child;
            Drain();
            Pump(child.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("c1"))));
            var c1 = (TextBox)ViewOf(inner)!;
            c1.Text = "first";
            Pump(child.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("c2"))));
            var c2 = (TextBox)ViewOf(inner)!;
            c2.Text = "second";
            Require(inner.RetainViews && inner.KeptViewCount == 1, $"The inner host kept {inner.KeptViewCount} views.");

            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("other"))));
            Drain();
            Require(!inner.IsLoaded && host.KeptViewCount == 1, "The page's view was not kept while another page showed.");

            Pump(region.BackAsync());
            Drain();
            Require(ReferenceEquals(ViewOf(host), shellView) && ReferenceEquals(shellView.Inner, inner) && inner.IsLoaded
                && locator.Created == 1, $"Back did not show the kept page view ({locator.Created} page views created).");
            Require(ReferenceEquals(ViewOf(inner), c2) && c2.Text == "second" && inner.KeptViewCount == 1,
                "The inner host built a new view after it loaded again with the same entry.");
            Pump(child.BackAsync());
            Require(ReferenceEquals(ViewOf(inner), c1) && c1.Text == "first", "The inner host lost its kept view of c1.");
        }
        finally { window.Close(); }
    }

    // An unloaded host doesn't follow its region and keeps its views; when it loads again it drops the views of entries
    // that retired meanwhile, and shows the same view for an unchanged current entry.
    [Test, DisplayName("RetainViews across unload"), NotInParallel("WpfSuite", Order = 58)]
    public void UnloadedHostKeepsAndPrunes()
    {
        using var fixture = new NavFixture();
        var region = fixture.Region();
        var host = new NavigationHost { Region = region, RetainViews = true };
        var window = ShowWindow(host, window => AddTemplates(window, typeof(DocumentViewModel)));
        try
        {
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("a"))));
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("b"))));
            var viewB = ViewOf(host);
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("c"))));
            var viewC = ViewOf(host);

            window.Content = null;
            Drain();
            window.Content = host;
            Drain();
            Require(host.IsLoaded && ReferenceEquals(ViewOf(host), viewC) && host.KeptViewCount == 2,
                "A host that loaded again with the same entry built a new view or dropped its kept views.");

            window.Content = null;
            Drain();
            Pump(region.BackAsync());
            Require(host.KeptViewCount == 2, "An unloaded host followed its region.");
            window.Content = host;
            Drain();
            Require(ReferenceEquals(ViewOf(host), viewB) && host.KeptViewCount == 1,
                $"After loading again the host showed {ViewOf(host)} with {host.KeptViewCount} kept views.");
        }
        finally { window.Close(); }
    }

    // A discarded host with kept views is collected.
    [Test, DisplayName("RetainViews host is collectable"), NotInParallel("WpfSuite", Order = 59)]
    public void UnloadedRetainingHostIsCollectable()
    {
        using var fixture = new NavFixture();
        var region = fixture.Region(NavigationTarget.Own<object>(new DocumentViewModel("first")));
        var window = ShowWindow(null, window => AddTemplates(window, typeof(DocumentViewModel)));
        try
        {
            var host = Mount(window, region);
            for (var attempt = 0; attempt < 30 && host.IsAlive; attempt++)
            {
                Drain();
                Pump(Task.Delay(10), "a short delay");
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Require(!host.IsAlive, "An unloaded host with kept views was not collected.");
            Pump(region.BackAsync());
        }
        finally { window.Close(); }
    }

    // Turning the option off drops the kept views when the host next presents an entry; a new Region drops them all.
    [Test, DisplayName("RetainViews off and Region change"), NotInParallel("WpfSuite", Order = 60)]
    public void OffAndRegionChangeDrop()
    {
        using var fixture = new NavFixture();
        var region = fixture.Region();
        var host = new NavigationHost { Region = region, RetainViews = true };
        var window = ShowWindow(host, window => AddTemplates(window, typeof(DocumentViewModel)));
        try
        {
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("a"))));
            var viewA = ViewOf(host);
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("b"))));
            host.RetainViews = false;
            Require(host.KeptViewCount == 1, "Turning the option off dropped the views before the host presented.");
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("c"))));
            Require(host.KeptViewCount == 0, $"With the option off the host kept {host.KeptViewCount} views.");
            Pump(region.BackAsync());
            Pump(region.BackAsync());
            Require(ViewOf(host) is TextBox again && !ReferenceEquals(again, viewA), "With the option off Back showed a kept view.");

            host.RetainViews = true;
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("d"))));
            Require(host.KeptViewCount == 1, $"With the option on again the host kept {host.KeptViewCount} views.");
            var other = fixture.Region(NavigationTarget.Own<object>(new DocumentViewModel("other")));
            host.Region = other;
            Drain();
            Require(host.KeptViewCount == 0 && host.PresentedEntry == other.CurrentEntry!.Id,
                $"A new region left {host.KeptViewCount} kept views.");
        }
        finally { window.Close(); }
    }

    // Focus inside a kept view comes back with it, but never moves away from an element focused elsewhere meanwhile.
    [Test, DisplayName("RetainViews restores focus"), NotInParallel("WpfSuite", Order = 61)]
    public void RestoresFocus()
    {
        using var fixture = new NavFixture();
        var region = fixture.Region();
        var host = new NavigationHost { Region = region, RetainViews = true };
        var outside = new TextBox();
        var panel = new StackPanel();
        panel.Children.Add(outside);
        panel.Children.Add(host);
        var window = ShowWindow(panel, window => AddTemplates(window, typeof(DocumentViewModel)));
        try
        {
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("a"))));
            var viewA = (TextBox)ViewOf(host)!;
            Focus(window, viewA);
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("b"))));
            Drain();
            Pump(region.BackAsync());
            Drain();
            Require(ReferenceEquals(ViewOf(host), viewA) && IsFocused(window, viewA),
                $"Back did not focus the kept view's text box again (focused: {Focused(window)}).");

            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("c"))));
            Drain();
            Focus(window, outside);
            Pump(region.BackAsync());
            Drain();
            Require(ReferenceEquals(ViewOf(host), viewA) && IsFocused(window, outside),
                $"Back took focus from an element outside the host (focused: {Focused(window)}).");
        }
        finally { window.Close(); }
    }

    // Keyboard focus in an active window, the window's focused element otherwise.
    private static void Focus(Window window, UIElement element)
    {
        if (window.IsActive) element.Focus();
        else FocusManager.SetFocusedElement(window, element);
        Drain();
    }

    private static object? Focused(Window window) => window.IsActive ? Keyboard.FocusedElement : FocusManager.GetFocusedElement(window);

    private static bool IsFocused(Window window, UIElement element) => ReferenceEquals(Focused(window), element);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Mount(Window window, NavigationRegion<object> region)
    {
        var host = new NavigationHost { Region = region, RetainViews = true };
        window.Content = host;
        Drain();
        Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("second"))));
        Require(host.IsLoaded && ViewOf(host) is TextBox && host.KeptViewCount == 1, "The host did not keep the first view.");
        window.Content = null;
        Drain();
        return new WeakReference(host);
    }

    // Counts the host's requests per entry and falls back to the implicit template.
    private sealed class AskCounter : INavigationViewLocator
    {
        private readonly Dictionary<NavigationEntryId, int> _asks = [];

        public int Asks(NavigationEntryId id) => _asks.GetValueOrDefault(id);

        public FrameworkElement? ResolveView(NavigationEntry entry)
        {
            _asks[entry.Id] = Asks(entry.Id) + 1;
            return null;
        }
    }

    private sealed class ShellLocator : INavigationViewLocator
    {
        public int Created { get; private set; }

        public FrameworkElement? ResolveView(NavigationEntry entry)
        {
            if (entry.Content is not ShellViewModel) return null;
            Created++;
            return new ShellView();
        }
    }

    // I-031: NavigationRequestOptions.RetainView decides for one entry, in either direction, instead of RetainViews.
    [Test, DisplayName("RetainView per entry"), NotInParallel("WpfSuite", Order = 63)]
    public void RetainViewPerEntry()
    {
        using var fixture = new NavFixture();
        var region = fixture.Region();
        var host = new NavigationHost { Region = region };
        var window = ShowWindow(host, window => AddTemplates(window, typeof(DocumentViewModel)));
        try
        {
            // The host keeps no views; the entry pushed with RetainView = true keeps its own.
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("a")), new NavigationRequestOptions { RetainView = true }));
            var viewA = (TextBox)ViewOf(host)!;
            viewA.Text = "kept text";
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("b"))));
            var viewB = ViewOf(host);
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("c"))));
            Require(host.KeptViewCount == 1, $"Only a's view should be kept; the host kept {host.KeptViewCount}.");
            Pump(region.BackAsync());
            Require(ViewOf(host) is TextBox newB && !ReferenceEquals(newB, viewB), "b's view was kept without RetainView.");
            Pump(region.BackAsync());
            Drain();
            Require(ReferenceEquals(ViewOf(host), viewA) && viewA.Text == "kept text" && host.KeptViewCount == 0,
                "Back did not show a's kept view again.");

            // A host that loads again shows the kept current view; the entry's value is read on Loaded too.
            window.Content = null;
            Drain();
            window.Content = host;
            Drain();
            Require(ReferenceEquals(ViewOf(host), viewA), "A host that loaded again rebuilt the view of an entry that keeps it.");

            // The host keeps views; the entry pushed with RetainView = false gets a new view when it returns.
            host.RetainViews = true;
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("d")), new NavigationRequestOptions { RetainView = false }));
            var viewD = ViewOf(host);
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("e"))));
            var viewE = ViewOf(host);
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("f"))));
            Require(host.KeptViewCount == 2, $"a and e should be kept, d not; the host kept {host.KeptViewCount}.");
            Pump(region.BackAsync());
            Require(ReferenceEquals(ViewOf(host), viewE), "RetainViews did not keep e's view.");
            Pump(region.BackAsync());
            Require(ViewOf(host) is TextBox newD && !ReferenceEquals(newD, viewD), "d's view was kept despite RetainView = false.");

            // Turning RetainViews off keeps the views of entries that keep their own.
            host.RetainViews = false;
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("g"))));
            Require(host.KeptViewCount == 1, $"Only a's view should stay kept; the host kept {host.KeptViewCount}.");
            Pump(region.BackToAsync(region.History[0].Id));
            Require(ReferenceEquals(ViewOf(host), viewA), "BackTo did not show a's kept view.");
        }
        finally { window.Close(); }
    }

    // I-031 with B6: a kept view is dropped when its entry retires, also while a presentation holds back the content's
    // release for work it keeps (RunicOperationRetirement.Cancel or Drain); a retained entry is never forgotten.
    [Test, DisplayName("RetainView with a deferred release"), NotInParallel("WpfSuite", Order = 64)]
    public void RetainViewWithDeferredRelease()
    {
        using var fixture = new NavFixture();
        var presentation = new WorkingPresentation();
        using var attachment = fixture.Navigator.AttachPresentation(presentation);
        var region = fixture.Region(NavigationTarget.Own<object>(new DocumentViewModel("home")));
        var home = region.CurrentEntry!.Id;
        var host = new NavigationHost { Region = region };
        var window = ShowWindow(host, window => AddTemplates(window, typeof(DocumentViewModel)));
        try
        {
            var kept = new DocumentViewModel("kept");
            var released = new DocumentViewModel("released");
            Pump(region.PushAsync(NavigationTarget.Own<object>(kept), new NavigationRequestOptions { RetainView = true }));
            Pump(region.PushAsync(NavigationTarget.Own<object>(released), new NavigationRequestOptions { RetainView = false }));
            Pump(region.PushAsync(NavigationTarget.Own<object>(new DocumentViewModel("top"))));
            var keptWork = presentation.Keep(kept);
            var releasedWork = presentation.Keep(released);
            Require(host.KeptViewCount == 1 && !presentation.WasForgotten(kept) && !presentation.WasForgotten(released), "Retaining the entries forgot one, or kept the wrong views.");

            // BackTo retires the three entries; the host presents home and drops the view the retired entry kept.
            Pump(region.BackToAsync(home));
            PumpUntil(() => presentation.WasForgotten(kept) && presentation.WasForgotten(released), "the retired entries to be forgotten");
            Drain();
            Require(host.KeptViewCount == 0 && host.PresentedEntry == home && !keptWork.Task.IsCompleted,
                $"BackTo left {host.KeptViewCount} kept views while the release waited.");
            keptWork.SetResult();
            releasedWork.SetResult();
            Pump(fixture.Navigator.WhenIdleAsync().AsTask());
        }
        finally { window.Close(); }
    }

    private sealed class WorkingPresentation : INavigationPresentation
    {
        private readonly Dictionary<object, TaskCompletionSource> _work = new(ReferenceEqualityComparer.Instance);

        private readonly List<object> _forgotten = [];

        public TaskCompletionSource Keep(object content)
        {
            lock (_work) return _work[content] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public bool WasForgotten(object content)
        {
            lock (_forgotten) return _forgotten.Contains(content);
        }

        public void Forget(object content)
        {
            lock (_forgotten) _forgotten.Add(content);
        }

        public ValueTask ForgetAsync(object content)
        {
            Forget(content);
            lock (_work) return _work.TryGetValue(content, out var work) ? new(work.Task) : ValueTask.CompletedTask;
        }
    }
}

internal sealed class ShellViewModel;

internal sealed class ShellView : Border
{
    public ShellView() => Child = Inner;

    public NavigationHost Inner { get; } = new();
}
