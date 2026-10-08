using System.Diagnostics.CodeAnalysis;

namespace Runic.Navigation;

// The reentrancy marker of a running hook (W230 §19 deviation 15, W240-001 §4.4). While a hook runs,
// a request from the hook's own code into its region, or a related one, is Rejected(Reentrant): the
// hook would otherwise wait on a transition that waits on it.
//
// The marker flows with the hook's ExecutionContext (an AsyncLocal). That alone is too wide: a hook
// may pump messages, for example a guard that shows a modal dialog, and a UI host then dispatches
// input handlers nested inside the hook's frame, on the same ExecutionContext. Those handlers are not
// the hook's code. A host does install its own SynchronizationContext for every message it dispatches
// (WPF installs a fresh DispatcherSynchronizationContext for each window message and in each
// PushFrame), so the marker applies only to code that runs under the SynchronizationContext the hook
// runs with. The hook runs under a HookSynchronizationContext that wraps the context it started on and
// re-establishes itself around the hook's posted continuations. Code with no SynchronizationContext,
// such as a hook's continuations on the thread pool, still counts as the hook's code.
[Experimental(RunicNavigator.DiagnosticId)]
internal sealed class NavigationHookMarker
{
    private static readonly AsyncLocal<NavigationHookMarker?> Current = new();

    private NavigationHookMarker(NavigationTransition transition) => Transition = transition;

    public NavigationTransition Transition { get; }

    // The transition whose hook the calling code belongs to, or null.
    public static NavigationTransition? Active
    {
        get
        {
            var marker = Current.Value;
            if (marker is null) return null;
            var context = SynchronizationContext.Current;
            return context is null || context is HookSynchronizationContext hook && ReferenceEquals(hook.Marker, marker)
                ? marker.Transition
                : null;
        }
    }

    // Marks the calling code as the hook of `transition` until the scope is disposed; null clears the
    // marker (cleanup operations and admissions on behalf of no hook). Dispose on the same thread, in
    // the same synchronous frame.
    public static Scope Enter(NavigationTransition? transition)
    {
        var previousMarker = Current.Value;
        var previousContext = SynchronizationContext.Current;
        var marker = transition is null ? null : new NavigationHookMarker(transition);
        Current.Value = marker;
        var wrapped = marker is not null && previousContext is not null;
        if (wrapped) SynchronizationContext.SetSynchronizationContext(new HookSynchronizationContext(previousContext!, marker!));
        return new Scope(previousMarker, previousContext, wrapped);
    }

    public readonly struct Scope(NavigationHookMarker? previousMarker, SynchronizationContext? previousContext, bool wrapped) : IDisposable
    {
        public void Dispose()
        {
            if (wrapped) SynchronizationContext.SetSynchronizationContext(previousContext);
            Current.Value = previousMarker;
        }
    }

    // Forwards to the context the hook started on, and runs each callback under itself, so the hook's
    // continuations keep the marker while other work dispatched by that context does not.
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
                SetSynchronizationContext(owner);
                try { callback(state); }
                finally { SetSynchronizationContext(previous); }
            }
        }
    }
}
