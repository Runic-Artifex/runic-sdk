using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

// The delivery queue of one route (specs/application/state-changes.md, "Delivery and recovery"). It hands the route's
// state, frame and failure pushes to the window's dispatcher, which broadcasts them, in order and one at a time.
// Delivery is deliberately outside the model turn: a host may synchronously wait for its native/UI dispatcher.
//
// - Frames do not coalesce; full states supersede everything queued before them.
// - A route retains at most 64 undelivered entries or 1 MiB of them; the entry being delivered does not count. A frame
//   that does not fit is refused (EnqueueFrame returns false) and the producer captures a recovery state (EnqueueState),
//   or requests one when a captured state is already queued.
// - A requested state is captured when the transport takes it, in a model turn, with the revision current then.
// - A publication unit can be requested too (RequestUnit): the drain runs it in a model turn before it takes the next
//   entry, so a burst of notifications publishes one frame.
//
// Every request and push takes a sequence number. A reply that must follow the pushes its request produced
// (BridgeReplyOrder) waits until the drain has settled the latest sequence: delivered it, or found nothing to deliver.
internal sealed class BridgeSnapshotDelivery : IDisposable
{
    internal const int MaximumPendingEntries = 64;
    internal const int MaximumPendingBytes = 1024 * 1024;
    private readonly object _gate = new();
    private readonly Queue<Entry> _pending = new();
    private readonly BridgeDispatcher _dispatcher;
    private readonly string _route;
    private readonly IBridgeModelTurn _modelTurn;
    private readonly Func<byte[]?> _captureState;
    private readonly Action _runUnit;
    private readonly string _model;
    private readonly ILogger _logger;
    private readonly BridgeReplyOrder _replyOrder;
    private List<(long Sequence, TaskCompletionSource Settled)>? _waiters;
    // The last request or push handed to the delivery, and the last one it settled.
    private long _produced;
    private long _settled;
    private int _pendingBytes;
    private bool _stateRequested;
    private bool _stateQueued;
    private bool _unitRequested;
    private bool _running;
    private bool _disposed;

    public BridgeSnapshotDelivery(IBridgeTransport transport, string route, IBridgeModelTurn modelTurn,
        Func<byte[]?> captureState, Action runUnit, string model, ILogger? logger = null)
    {
        _dispatcher = BridgeDispatcher.For(transport);
        _replyOrder = BridgeReplyOrder.For(transport);
        _route = route;
        _modelTurn = modelTurn;
        _captureState = captureState;
        _runUnit = runUnit;
        _model = model;
        _logger = logger ?? TraceFallbackLogger.Instance;
    }

    // The route this delivery publishes.
    public string Route => _route;

    // Whether the queue holds a captured full state.
    public bool StateQueued
    {
        get { lock (_gate) return _stateQueued; }
    }

    // Whether a full state waits to be captured.
    public bool StateRequested
    {
        get { lock (_gate) return _stateRequested; }
    }

    // Asks for a full state, captured when the transport takes it. It supersedes everything queued.
    public void RequestState()
    {
        lock (_gate)
        {
            if (_disposed) return;
            ClearPending();
            _stateRequested = true;
            _produced++;
            Start();
        }
    }

    // Asks the drain to run a publication unit in a model turn before it takes the next entry.
    public void RequestUnit()
    {
        lock (_gate)
        {
            if (_disposed || _unitRequested) return;
            _unitRequested = true;
            _produced++;
            Start();
        }
    }

    // Queues a captured full state, which supersedes everything queued before it.
    public void EnqueueState(byte[] state)
    {
        lock (_gate)
        {
            if (_disposed) return;
            ClearPending();
            _pending.Enqueue(new(state, "state", ++_produced));
            BridgeTelemetry.AddQueuedFrames(1);
            _pendingBytes = state.Length;
            _stateQueued = true;
            Start();
        }
    }

    // Queues a frame; false when it does not fit, so the producer recovers with a full state.
    public bool EnqueueFrame(byte[] frame)
    {
        lock (_gate)
        {
            if (_disposed) return true;
            if (_stateRequested || _pending.Count >= MaximumPendingEntries
                || _pendingBytes + frame.Length > MaximumPendingBytes) return false;
            _pending.Enqueue(new(frame, "frame", ++_produced));
            BridgeTelemetry.AddQueuedFrames(1);
            _pendingBytes += frame.Length;
            Start();
            return true;
        }
    }

    // Tells the client why the route publishes nothing for now. It supersedes queued frames, which the client cannot
    // use without the frame that follows once the keys are valid again.
    public void EnqueueFailure(byte[] failure)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var requested = _stateRequested;
            ClearPending();
            _stateRequested = requested;
            _pending.Enqueue(new(failure, "failure", ++_produced));
            BridgeTelemetry.AddQueuedFrames(1);
            _pendingBytes = failure.Length;
            Start();
        }
    }

    // Completes when every request and push handed to the delivery so far was delivered, failed, found nothing to
    // deliver, or the delivery was disposed. Null when nothing is outstanding. The drain does not wait for the reply
    // that waits for this task: the next push, which a later request can have caused, may reach the transport before
    // that reply does (specs/application/README.md, "Reply order").
    //
    // The task's continuations never run on the drain: a reply that resumed inside Settle held the drain until its
    // caller yielded, and a caller that then waited synchronously for its next reply waited for the drain it blocked,
    // which left a requested unit unrun and every later reply waiting for ReplyWaitLimit.
    internal Task? WhenSettled()
    {
        lock (_gate)
        {
            if (_disposed || _settled >= _produced) return null;
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            (_waiters ??= []).Add((_produced, settled));
            return settled.Task;
        }
    }

    private void Settle(long sequence)
    {
        List<TaskCompletionSource>? released = null;
        lock (_gate)
        {
            if (sequence <= _settled) return;
            _settled = sequence;
            if (_waiters is null) return;
            for (var index = _waiters.Count - 1; index >= 0; index--)
            {
                if (_waiters[index].Sequence > sequence) continue;
                (released ??= []).Add(_waiters[index].Settled);
                _waiters.RemoveAt(index);
            }
        }
        if (released is null) return;
        for (var index = released.Count - 1; index >= 0; index--) released[index].TrySetResult();
    }

    private bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    private void ClearPending()
    {
        if (_pending.Count != 0) BridgeTelemetry.AddQueuedFrames(-_pending.Count);
        _pending.Clear();
        _pendingBytes = 0;
        _stateRequested = false;
        _stateQueued = false;
    }

    // Starts the drain. The gate is held. A queued entry is taken at once, so the entry being delivered is known as
    // soon as it is queued and does not count toward the bounds.
    private void Start()
    {
        if (_running) return;
        _running = true;
        _replyOrder.Add(this);
        Entry? first = null;
        if (!_unitRequested && !_stateRequested && _pending.Count > 0) first = Take();
        _ = Task.Run(() => DrainAsync(first));
    }

    private Entry Take()
    {
        var entry = _pending.Dequeue();
        BridgeTelemetry.AddQueuedFrames(-1);
        _pendingBytes -= entry.Message.Length;
        if (entry.Kind == "state") _stateQueued = false;
        return entry;
    }

    private async Task DrainAsync(Entry? first)
    {
        var next = first;
        while (true)
        {
            if (next is null)
            {
                bool unit, capture;
                long idle = -1;
                lock (_gate)
                {
                    unit = !_disposed && _unitRequested;
                    capture = !_disposed && !unit && _pending.Count == 0 && _stateRequested;
                    if (!unit && !capture)
                    {
                        if (_disposed || _pending.Count == 0)
                        {
                            _running = false;
                            _replyOrder.Remove(this);
                            // Nothing is outstanding: everything produced so far settled.
                            idle = _produced;
                        }
                        else next = Take();
                    }
                }
                if (idle >= 0)
                {
                    Settle(idle);
                    return;
                }
                if (unit)
                {
                    lock (_gate) _unitRequested = false;
                    // What the unit queues takes later sequences; the unit's own sequence settles with them, or when
                    // the drain finds nothing more to deliver.
                    RunInTurn(_runUnit, "publication");
                    continue;
                }
                if (capture)
                {
                    long sequence = 0;
                    byte[]? state = null;
                    RunInTurn(() =>
                    {
                        lock (_gate)
                        {
                            if (_disposed || !_stateRequested) return;
                            _stateRequested = false;
                            sequence = _produced;
                        }
                        state = _captureState();
                    }, "snapshot.capture");
                    if (state is null)
                    {
                        if (sequence != 0) Settle(sequence);
                        continue;
                    }
                    next = new(state, "state", sequence);
                }
                if (next is null) continue;
            }

            var entry = next.Value;
            next = null;
            if (IsDisposed)
            {
                Settle(entry.Sequence);
                continue;
            }
            var timestamp = BridgeTelemetry.Timestamp();
            try
            {
                await _dispatcher.BroadcastAsync(entry.Message).ConfigureAwait(false);
                BridgeTelemetry.RecordFrame(_model, entry.Kind, entry.Message.Length, timestamp);
            }
            // The bridge was released while this push was on its way, and its host disposed the transport, as a
            // closing window does (I-174). There is no page left to deliver to.
            catch (ObjectDisposedException) when (IsDisposed) { }
            catch (Exception error)
            {
                BridgeTelemetry.RecordFailure("snapshot.delivery", _model, null, error);
                ViewsLog.SnapshotDeliveryFailed(_logger, error, _model, _route, BridgeTelemetry.ErrorType(error));
            }
            Settle(entry.Sequence);
        }
    }

    // Runs work of the producer in a model turn. A turn that cannot start means the model context is gone; any other
    // failure is logged, because there is no notification raiser to report it to.
    private void RunInTurn(Action work, string kind)
    {
        var started = false;
        try
        {
            _modelTurn.Run(() =>
            {
                started = true;
                work();
            });
        }
        catch (ObjectDisposedException) when (!started) { Dispose(); }
        catch (Exception error)
        {
            BridgeTelemetry.RecordFailure(kind, _model, null, error);
            ViewsLog.SnapshotCaptureFailed(_logger, error, _model, _route, BridgeTelemetry.ErrorType(error));
        }
    }

    public void Dispose()
    {
        List<(long Sequence, TaskCompletionSource Settled)>? waiters;
        lock (_gate)
        {
            _disposed = true;
            ClearPending();
            _unitRequested = false;
            waiters = _waiters;
            _waiters = null;
            _replyOrder.Remove(this);
        }
        if (waiters is not null)
            foreach (var waiter in waiters) waiter.Settled.TrySetResult();
    }

    private readonly record struct Entry(byte[] Message, string Kind, long Sequence);
}
