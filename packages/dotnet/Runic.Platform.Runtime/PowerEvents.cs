using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Runic.Platform.Runtime;

// A provider's native power and session events: WM_POWERBROADCAST and WTS session notifications on Windows,
// NSWorkspace and IOKit on macOS, logind and UPower on Linux.
internal interface IPowerEventSource
{
    // The current state.
    ValueTask<PlatformResult<DesktopPowerState>> ReadAsync(CancellationToken cancellationToken);

    // Reports the current state as StateChanged, then each event with the state after it, until the subscription is
    // disposed. A source that goes away reports Unavailable and, once it is back, the current state again. Calls to
    // report may come from any thread, also during the call.
    ValueTask<IAsyncDisposable> SubscribeAsync(Action<PlatformResult<DesktopPowerEvent>> report, CancellationToken cancellationToken);
}

// IDesktopPowerEvents over a source: the watch semantics every provider shares. Each watch subscribes on its own.
// Events queue (up to a bound, then the oldest go), so a slow reader still sees each sleep and wake; a StateChanged that
// leaves the state as it was and a repeated event are suppressed.
internal sealed class PowerEventsService(IPowerEventSource source) : IDesktopPowerEvents, IDesktopServiceStatus
{
    private const int Queue = 64;
    private readonly CancellationTokenSource _disposed = new();

    public CapabilityStatus Status { get; } = new CapabilityStatus.Available();

    public async ValueTask<PlatformResult<DesktopPowerState>> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
        cancellationToken.ThrowIfCancellationRequested();
        return await source.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<PlatformResult<DesktopPowerEvent>> WatchAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
        return WatchCoreAsync(cancellationToken);
    }

    private async IAsyncEnumerable<PlatformResult<DesktopPowerEvent>> WatchCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        var token = linked.Token;
        var updates = Channel.CreateBounded<PlatformResult<DesktopPowerEvent>>(
            new BoundedChannelOptions(Queue) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        IAsyncDisposable? subscription = null;
        try
        {
            try { subscription = await source.SubscribeAsync(result => updates.Writer.TryWrite(result), token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // Without a source the watch reports it once and waits.
                updates.Writer.TryWrite(new PlatformResult<DesktopPowerEvent>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
            }
            PlatformResult<DesktopPowerEvent>? previous = null;
            while (true)
            {
                var next = await updates.Reader.ReadAsync(token).ConfigureAwait(false);
                if (Repeats(previous, next)) continue;
                previous = next;
                yield return next;
            }
        }
        finally
        {
            if (subscription is not null) await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    // A result equal to the previous one, or a StateChanged whose state the previous event already reported.
    internal static bool Repeats(PlatformResult<DesktopPowerEvent>? previous, PlatformResult<DesktopPowerEvent> next) =>
        previous is not null && (Equals(previous, next)
            || previous is PlatformResult<DesktopPowerEvent>.Success { Value.State: var before }
                && next is PlatformResult<DesktopPowerEvent>.Success { Value: { Kind: DesktopPowerEventKind.StateChanged, State: var after } }
                && Equals(before, after));

    public ValueTask DisposeAsync()
    {
        _disposed.Cancel();
        return ValueTask.CompletedTask;
    }
}

// The state a source keeps between native notifications, and the events they make. Not thread-safe: a source calls it
// from one thread or under its own lock.
internal sealed class PowerStateTracker(Action<PlatformResult<DesktopPowerEvent>> report)
{
    // The last of Suspending and Resumed reported, so a repeated one counts once; null before the first.
    private DesktopPowerEventKind? _sleep;

    internal DesktopPowerState State { get; private set; } = new();

    // The current state, as the watch's first item.
    internal void Start(DesktopPowerState state)
    {
        State = state;
        report(Success(DesktopPowerEventKind.StateChanged));
    }

    internal void Update(DesktopPowerState state)
    {
        if (Equals(state, State)) return;
        State = state;
        report(Success(DesktopPowerEventKind.StateChanged));
    }

    // Several native notifications can announce one sleep or wake (Windows' broadcast and its suspend-resume
    // registration); each counts once.
    internal void Suspending() => Sleep(DesktopPowerEventKind.Suspending);

    internal void Resumed() => Sleep(DesktopPowerEventKind.Resumed);

    private void Sleep(DesktopPowerEventKind kind)
    {
        if (_sleep == kind) return;
        _sleep = kind;
        report(Success(kind));
    }

    internal void Locked(bool locked)
    {
        if (State.IsLocked == locked) return;
        State = State with { IsLocked = locked };
        report(Success(locked ? DesktopPowerEventKind.Locked : DesktopPowerEventKind.Unlocked));
    }

    private PlatformResult<DesktopPowerEvent> Success(DesktopPowerEventKind kind) =>
        new PlatformResult<DesktopPowerEvent>.Success(new DesktopPowerEvent(kind, State));
}
