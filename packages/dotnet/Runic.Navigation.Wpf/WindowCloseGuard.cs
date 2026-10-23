using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace Runic.Navigation.Wpf;

// The window side of NavigationHost.GuardWindowClose (W250-047). The guard belongs to the window and lists regions, not
// hosts: a host on an unselected TabControl tab is unloaded and its view discarded, while its region and the draft in it
// live on. A region stays listed in the window where a guarding host last loaded with it, until the region is collected,
// the window closes, a guarding host loads with it in another window, or a host that presents it turns the guard off.
// The window holds its regions weakly and a region holds nothing of the window, so neither keeps the other alive.
internal sealed class WindowCloseGuard
{
    private static readonly DependencyProperty GuardProperty = DependencyProperty.RegisterAttached("WindowCloseGuard",
        typeof(WindowCloseGuard), typeof(WindowCloseGuard), new PropertyMetadata(null));

    // The guard that lists each region: one window per region.
    private static readonly ConditionalWeakTable<NavigationRegion, WeakReference<WindowCloseGuard>> Owners = new();

    private static readonly NavigationRequestOptions SupersedeOptions = new() { Admission = NavigationAdmission.Supersede };

    private readonly Window _window;
    // In the order their hosts first loaded.
    private readonly List<WeakReference<NavigationRegion>> _regions = [];
    private bool _checking;
    // The running check, for WindowCloseConfirmation; guards may read it on another thread.
    private volatile CloseCheck? _check;
    // The region the running check clears, and whether a close came after that clear stopped transitioning.
    private NavigationRegion? _clearing;
    private bool _closeAgain;
    private bool _approved;
    private bool _closed;

    private WindowCloseGuard(Window window)
    {
        _window = window;
        window.Closing += OnClosing;
        window.Closed += OnClosed;
    }

    // Lists the region in the window, and takes it from the window that listed it before.
    public static void Add(Window window, NavigationRegion region)
    {
        var guard = (WindowCloseGuard?)window.GetValue(GuardProperty);
        if (guard is null)
        {
            guard = new WindowCloseGuard(window);
            window.SetValue(GuardProperty, guard);
        }
        if (Owners.TryGetValue(region, out var owner) && owner.TryGetTarget(out var previous))
        {
            if (ReferenceEquals(previous, guard)) return;
            previous.Forget(region);
        }
        Owners.AddOrUpdate(region, new WeakReference<WindowCloseGuard>(guard));
        _ = guard.Regions(); // Drops the collected regions first, so the list doesn't grow with them.
        guard._regions.Add(new WeakReference<NavigationRegion>(region));
    }

    // Removes the region from the window that lists it.
    public static void Remove(NavigationRegion region)
    {
        if (!Owners.TryGetValue(region, out var owner)) return;
        Owners.Remove(region);
        if (owner.TryGetTarget(out var guard)) guard.Forget(region);
    }

    // The close check of the window that lists the region, while that check clears the region; otherwise null. Any thread.
    public static CloseCheck? ClearingCheck(NavigationRegion region)
    {
        if (!Owners.TryGetValue(region, out var owner) || !owner.TryGetTarget(out var guard)) return null;
        return guard._check is { } check && ReferenceEquals(check.Clearing, region) ? check : null;
    }

    // Whether the window lists the region; for tests.
    internal static bool Lists(Window window, NavigationRegion region) =>
        window.GetValue(GuardProperty) is WindowCloseGuard guard && guard.Regions().Contains(region);

    // The number of live regions the window lists; for tests.
    internal static int CountOf(Window window) => window.GetValue(GuardProperty) is WindowCloseGuard guard ? guard.Regions().Count : 0;

    // Whether any window lists the region; for tests.
    internal static bool IsListed(NavigationRegion region) =>
        Owners.TryGetValue(region, out var owner) && owner.TryGetTarget(out var guard) && !guard._closed;

    // The live regions, in order; drops the collected ones.
    private List<NavigationRegion> Regions()
    {
        List<NavigationRegion> live = new(_regions.Count);
        _regions.RemoveAll(reference =>
        {
            if (!reference.TryGetTarget(out var region)) return true;
            live.Add(region);
            return false;
        });
        return live;
    }

    private void Forget(NavigationRegion region)
    {
        // A navigator, and so its regions, belongs to one UI thread; this is for a window of another thread all the same.
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Forget(region));
            return;
        }
        _regions.RemoveAll(reference => !reference.TryGetTarget(out var listed) || ReferenceEquals(listed, region));
    }

    private static bool HasEntries(NavigationRegion region) => region.CurrentEntry is not null || region.CanGoBack;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (e.Cancel || _approved || _closed || !Regions().Exists(HasEntries)) return;
        e.Cancel = true;
        if (_checking)
        {
            // A close while a clear runs joins it, so a second click during the question asks nothing more. Once that
            // clear's region stops transitioning, its outcome is decided while the check's own continuation may still
            // be on its way: a close then is a new request, and is asked again when the check ends without closing.
            if (_clearing is { IsTransitioning: false }) _closeAgain = true;
            return;
        }
        _checking = true;
        Check();
    }

    // Posted: WPF ignores the cancel during Application.Shutdown, and the window is gone when this runs.
    private void Check() => _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => _ = ClearThenCloseAsync());

    private async Task ClearThenCloseAsync()
    {
        NavigationRegion? clearing = null;
        var check = new CloseCheck();
        try
        {
            if (PresentationSource.FromVisual(_window) is null) return;
            _check = check;
            // One region after another. A region that is empty by its turn, such as a child region that an earlier
            // clear closed, is skipped; a clear that doesn't commit keeps the window open and asks no further regions.
            foreach (var region in Regions())
            {
                if (!HasEntries(region)) continue;
                clearing = _clearing = check.Clearing = region;
                _closeAgain = false;
                // The plan of any other clear: every entry's guard and the guards of the child regions the entries own.
                // Supersede: a close is never refused because the region is busy, also under RejectWhileBusy.
                var result = await region.ClearAsync(SupersedeOptions);
                if (result is not NavigationResult<object>.Committed) return;
            }
            await _window.Dispatcher.InvokeAsync(() =>
            {
                if (PresentationSource.FromVisual(_window) is null) return;
                _approved = true;
                try { _window.Close(); }
                finally { _approved = false; }
            });
        }
        catch (ObjectDisposedException) { }
        catch (Exception error)
        {
            WpfNavigationLog.WindowCloseGuardFailed(WpfNavigationLog.For(clearing?.Navigator.Services), error, WpfNavigationLog.ErrorType(error));
        }
        finally
        {
            var again = _closeAgain && !_closed;
            check.Clearing = null;
            _check = null;
            _clearing = null;
            _closeAgain = false;
            if (again) Check();
            else _checking = false;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _window.Closing -= OnClosing;
        _window.Closed -= OnClosed;
        _window.ClearValue(GuardProperty);
        foreach (var region in Regions())
        {
            if (Owners.TryGetValue(region, out var owner) && owner.TryGetTarget(out var guard) && ReferenceEquals(guard, this))
                Owners.Remove(region);
        }
        _regions.Clear();
    }
}

// One check of a window's close: the regions it clears one after another, one at a time.
internal sealed class CloseCheck
{
    private volatile NavigationRegion? _clearing;

    // The region whose clear runs; null between clears and after the check.
    public NavigationRegion? Clearing
    {
        get => _clearing;
        set => _clearing = value;
    }
}
