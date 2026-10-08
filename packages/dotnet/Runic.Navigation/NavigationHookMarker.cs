using System.Diagnostics.CodeAnalysis;

namespace Runic.Navigation;

// The reentrancy marker of a running hook (W230 §19 deviation 15, W240-001 §4.4). While a hook runs,
// a request from the hook's own code into its region, or a related one, is Rejected(Reentrant): the
// hook would otherwise wait on a transition that waits on it.
//
// The marker flows with the hook's ExecutionContext (an AsyncLocal), so it reaches every continuation
// of the hook, however it resumes: through the SynchronizationContext, on the pool, or in a new
// dispatcher operation (Dispatcher.Yield, BeginInvoke, ObserveOn).
//
// That alone is too wide in one case: a hook may pump messages, for example a guard that shows a
// modal dialog, and a UI host then dispatches input handlers nested inside the hook's synchronous
// frame, on the same ExecutionContext. Those handlers are not the hook's code. The marker therefore
// tracks, per thread, the frame in which the hook's code runs: the hook's own synchronous call (Enter
// to Dispose) and each continuation posted through the hook's HookSynchronizationContext. Inside such
// a frame, only code under the context the frame runs with counts as the hook; anything else was
// dispatched by a nested pump (UI hosts install their own SynchronizationContext for every message
// they dispatch). Outside such a frame, a marker in the ExecutionContext always means the hook.
//
// The marker holds its transition weakly: user code may keep the hook's SynchronizationContext (for
// example Progress<T> or ObserveOn(SynchronizationContext.Current)), and that must not keep the
// transition, its entries and region alive.
[Experimental(RunicNavigator.DiagnosticId)]
internal sealed class NavigationHookMarker
{
    private static readonly AsyncLocal<NavigationHookMarker?> Current = new();

    // The marker whose frame runs on this thread, and whether that frame runs under the marker's
    // HookSynchronizationContext (false: under no context).
    [ThreadStatic] private static NavigationHookMarker? t_frame;
    [ThreadStatic] private static bool t_frameWrapped;

    private readonly WeakReference<NavigationTransition> _transition;

    private NavigationHookMarker(NavigationTransition transition) => _transition = new(transition);

    public NavigationTransition? Transition => _transition.TryGetTarget(out var transition) ? transition : null;

    // The transition whose hook the calling code belongs to, or null.
    public static NavigationTransition? Active
    {
        get
        {
            var marker = Current.Value;
            if (marker is null) return null;
            if (ReferenceEquals(t_frame, marker))
            {
                // Inside the hook's frame: code under another context was dispatched by a nested pump.
                var context = SynchronizationContext.Current;
                var hookContext = t_frameWrapped
                    ? context is HookSynchronizationContext hook && ReferenceEquals(hook.Marker, marker)
                    : context is null;
                if (!hookContext) return null;
            }
            return marker.Transition;
        }
    }

    // Marks the calling code as the hook of `transition` until the scope is disposed; null clears the
    // marker (cleanup operations and admissions on behalf of no hook). Dispose on the same thread, in
    // the same synchronous frame.
    public static Scope Enter(NavigationTransition? transition)
    {
        var previousMarker = Current.Value;
        var previousContext = SynchronizationContext.Current;
        var previousFrame = t_frame;
        var previousFrameWrapped = t_frameWrapped;
        var marker = transition is null ? null : new NavigationHookMarker(transition);
        Current.Value = marker;
        var wrapped = marker is not null && previousContext is not null;
        if (wrapped) SynchronizationContext.SetSynchronizationContext(new HookSynchronizationContext(previousContext!, marker!));
        t_frame = marker;
        t_frameWrapped = wrapped;
        return new Scope(previousMarker, previousContext, wrapped, previousFrame, previousFrameWrapped);
    }

    public readonly struct Scope(NavigationHookMarker? previousMarker, SynchronizationContext? previousContext, bool wrapped,
        NavigationHookMarker? previousFrame, bool previousFrameWrapped) : IDisposable
    {
        public void Dispose()
        {
            if (wrapped) SynchronizationContext.SetSynchronizationContext(previousContext);
            Current.Value = previousMarker;
            t_frame = previousFrame;
            t_frameWrapped = previousFrameWrapped;
        }
    }

    // Forwards to the context the hook started on, and runs each callback under itself as a frame of
    // the hook, so the hook's continuations keep the marker while other work dispatched by that
    // context does not.
    private sealed class HookSynchronizationContext : SynchronizationContext
    {
        private readonly SynchronizationContext _inner;

        public HookSynchronizationContext(SynchronizationContext inner, NavigationHookMarker marker)
        {
            _inner = inner;
            Marker = marker;
            if (inner.IsWaitNotificationRequired()) SetWaitNotificationRequired();
        }

        public NavigationHookMarker Marker { get; }

        public override void Post(SendOrPostCallback d, object? state) =>
            _inner.Post(static state => ((Callback)state!).Invoke(), new Callback(this, d, state));

        public override void Send(SendOrPostCallback d, object? state) =>
            _inner.Send(static state => ((Callback)state!).Invoke(), new Callback(this, d, state));

        public override SynchronizationContext CreateCopy() => new HookSynchronizationContext(_inner.CreateCopy(), Marker);

        public override void OperationStarted() => _inner.OperationStarted();

        public override void OperationCompleted() => _inner.OperationCompleted();

        public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout) =>
            _inner.Wait(waitHandles, waitAll, millisecondsTimeout);

        private sealed class Callback(HookSynchronizationContext owner, SendOrPostCallback callback, object? state)
        {
            public void Invoke()
            {
                var previous = SynchronizationContext.Current;
                var previousFrame = t_frame;
                var previousFrameWrapped = t_frameWrapped;
                SetSynchronizationContext(owner);
                t_frame = owner.Marker;
                t_frameWrapped = true;
                try { callback(state); }
                finally
                {
                    t_frame = previousFrame;
                    t_frameWrapped = previousFrameWrapped;
                    SetSynchronizationContext(previous);
                }
            }
        }
    }
}
