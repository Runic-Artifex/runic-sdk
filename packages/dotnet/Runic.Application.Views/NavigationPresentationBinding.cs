using System.ComponentModel;
using System.Runtime.CompilerServices;
using Runic.Navigation;

namespace Runic.Application.Views;

// Binds the window session that presents a navigator's regions, so retiring owned
// content forgets its routes. Bridges bind when they first observe a region slot.
// One session per navigator: binding is idempotent for the same session. Disposing
// the session detaches it and removes it from the table, so neither the table nor
// the navigator keeps a closed window's session reachable. The session links the
// binding only weakly, so a live session does not keep a navigator reachable either:
// the table entry, and with it the binding, dies with the navigator. A closed
// navigator binds nothing and never throws for a second session.
internal static class NavigationPresentationBinding
{
    private static readonly ConditionalWeakTable<RunicNavigator, Slot> Slots = new();

    // A Bridge slot presents Current. It binds the window session and republishes
    // when Current changes. Disposing the subscription leaves the binding in place.
    public static IDisposable Observe(NavigationRegion region, WindowContentSession? session, Action changed)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(changed);
        if (session is not null) Bind(region.Navigator, session);
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(NavigationRegion.Current)) changed();
        };
        region.PropertyChanged += handler;
        return new Subscription(region, handler);
    }

    // A Bridge selection slot presents SelectedIndex and republishes when it changes, as a region slot does for
    // Current. It binds no session: the selection's regions are slots of their own, and those bind it.
    public static IDisposable Observe(NavigationSelection selection, Action changed)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(changed);
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(NavigationSelection.SelectedIndex)) changed();
        };
        selection.PropertyChanged += handler;
        return new Subscription(selection, handler);
    }

    // A Bridge region collection (region-collections/1) presents every region of the selection and the selected index.
    // It binds the window session, as a region slot does, and republishes when the selection's regions or selected index
    // change, or when the Current of one of its regions changes. It follows the regions that Add and RemoveAsync change.
    public static IDisposable ObserveCollection(NavigationSelection selection, WindowContentSession? session, Action changed)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(changed);
        if (session is not null) Bind(selection.Navigator, session);
        return new CollectionSubscription(selection, changed);
    }

    /// <exception cref="InvalidOperationException">
    /// The session uses a different model context, or another live session is already bound.
    /// </exception>
    public static void Bind(RunicNavigator navigator, WindowContentSession session)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(session.ModelContext, navigator.ModelContext))
            throw new InvalidOperationException(
                "A navigator can be presented only by a window session that shares its model context. Create the session with the navigator's IRunicModelContext.");
        var slot = Slots.GetValue(navigator, static _ => new Slot());
        lock (slot)
        {
            if (session.IsDisposed) return;
            if (slot.Binding is { } bound)
            {
                if (ReferenceEquals(bound.Session, session)) return;
                // Disposing a session unbinds it, so a bound session is live here unless its
                // disposal is still running. A closed navigator presents nothing, so a second
                // session is not a conflict.
                if (!bound.Session.IsDisposed && !navigator.IsClosed)
                    throw new InvalidOperationException(
                        "This navigator is already presented by another window session. Use one navigator per window.");
                slot.Binding = null;
                bound.Detach();
                bound.Session.UnlinkDisposal(bound);
            }
            if (navigator.IsClosed) return;
            var binding = new Binding(slot, session);
            binding.Attachment = navigator.AttachPresentation(binding);
            slot.Binding = binding;
            // navigation-path/1: the window's dispatcher serves the navigator's Back and route path.
            BridgeDispatcher.For(session.Transport).Navigator = navigator;
            // A session disposed after the IsDisposed check above unbinds at once.
            if (!session.TryLinkDisposal(binding)) binding.Dispose();
        }
    }

    private sealed class Slot
    {
        public Binding? Binding { get; set; }
    }

    // The session bound to a navigator, or null; for tests.
    internal static WindowContentSession? BoundSession(RunicNavigator navigator)
    {
        if (!Slots.TryGetValue(navigator, out var slot)) return null;
        lock (slot) return slot.Binding?.Session;
    }

    // Forget is a no-op once the session is disposed. Disposing the binding, which the
    // session does when it is disposed, detaches it from the navigator and the table.
    private sealed class Binding(Slot slot, WindowContentSession session) : INavigationPresentation, IDisposable
    {
        public WindowContentSession Session { get; } = session;

        private IDisposable? _attachment;

        public IDisposable? Attachment
        {
            get => Volatile.Read(ref _attachment);
            set => Volatile.Write(ref _attachment, value);
        }

        public void Forget(object content) => Session.Forget(content);

        // Waits for the content's operations declared RunicOperationRetirement.Cancel or Drain, so the navigator
        // disposes the content after them.
        public ValueTask ForgetAsync(object content) => new(Session.ForgetAsync(content));

        // Drops the attachment too, since it references the navigator.
        public void Detach() => Interlocked.Exchange(ref _attachment, null)?.Dispose();

        public void Dispose()
        {
            lock (slot)
            {
                // A binding that is no longer the slot's was replaced in Bind, which already
                // detached it; detaching again would be harmless but must not clear the
                // replacement's slot.
                if (!ReferenceEquals(slot.Binding, this)) return;
                slot.Binding = null;
            }
            Detach();
            Session.UnlinkDisposal(this);
        }
    }

    // Observes a selection and the Current of each of its regions. The selection raises its changes in model turns, so
    // the regions are resubscribed in the turn that changed them; the lock only orders that against Dispose.
    private sealed class CollectionSubscription : IDisposable
    {
        private readonly NavigationSelection _selection;
        private readonly Action _changed;
        private readonly PropertyChangedEventHandler _selectionHandler;
        private readonly PropertyChangedEventHandler _regionHandler;
        private readonly object _gate = new();
        private IReadOnlyList<NavigationRegion> _regions = [];
        private bool _disposed;

        public CollectionSubscription(NavigationSelection selection, Action changed)
        {
            _selection = selection;
            _changed = changed;
            _regionHandler = (_, args) =>
            {
                if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(NavigationRegion.Current)) _changed();
            };
            _selectionHandler = (_, args) =>
            {
                if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(NavigationSelection.Regions))
                {
                    Follow();
                    _changed();
                }
                else if (args.PropertyName == nameof(NavigationSelection.SelectedIndex)) _changed();
            };
            selection.PropertyChanged += _selectionHandler;
            Follow();
        }

        // Subscribes the regions the selection has now and unsubscribes the ones it no longer has.
        private void Follow()
        {
            lock (_gate)
            {
                if (_disposed) return;
                var next = _selection.Regions;
                if (ReferenceEquals(next, _regions)) return;
                foreach (var region in _regions)
                    if (!Contains(next, region)) region.PropertyChanged -= _regionHandler;
                foreach (var region in next)
                    if (!Contains(_regions, region)) region.PropertyChanged += _regionHandler;
                _regions = next;
            }
        }

        private static bool Contains(IReadOnlyList<NavigationRegion> regions, NavigationRegion region)
        {
            foreach (var candidate in regions)
                if (ReferenceEquals(candidate, region)) return true;
            return false;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _selection.PropertyChanged -= _selectionHandler;
                foreach (var region in _regions) region.PropertyChanged -= _regionHandler;
                _regions = [];
            }
        }
    }

    private sealed class Subscription(INotifyPropertyChanged source, PropertyChangedEventHandler handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) source.PropertyChanged -= handler;
        }
    }
}
