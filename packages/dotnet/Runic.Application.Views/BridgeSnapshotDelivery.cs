using System.Buffers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

// Full states supersede everything queued before them. Delivery is
// deliberately outside the model turn: a host may synchronously wait for its
// native/UI dispatcher. Operations and interaction messages never use this
// coalescing queue.
//
// A full state is normally requested, not captured: the drain captures it in
// the model turn when the host can take it, so a burst of changes behind a
// busy host costs one serialization per delivered state. While a requested
// state waits, the producer folds frames into it instead of queuing them.
// A recovery state is captured at once (Enqueue) so frames can queue behind it;
// if one cannot, the queued state reverts to a request.
//
// Every request and frame takes a sequence number. A reply that must follow
// the frames its call produced (BridgeReplyOrder) waits until the drain has
// settled the latest sequence: delivered it, or found nothing to deliver.
internal sealed class BridgeSnapshotDelivery(IBridgeTransport transport, string route, IBridgeModelTurn modelTurn,
    Func<string?> captureState, string model, ILogger? logger = null) : IDisposable
{
    private const int MaximumPendingFrames = 64;
    private const int MaximumPendingLength = 1024 * 1024;
    private readonly object _gate = new();
    private readonly Queue<(string Frame, string Kind, long Sequence)> _pending = new();
    private readonly ILogger _logger = logger ?? TraceFallbackLogger.Instance;
    private readonly BridgeReplyOrder _replyOrder = BridgeReplyOrder.For(transport);
    // Protocol-3 states travel through the dispatcher's legacy routes until the producer speaks protocol 4.
    private readonly BridgeLegacyRoutes _routes = BridgeDispatcher.For(transport).Legacy;
    private List<(long Sequence, TaskCompletionSource Settled)>? _waiters;
    // The last request or frame handed to the delivery, and the last one it settled.
    private long _produced;
    private long _settled;
    private int _pendingLength;
    private bool _stateRequested;
    private bool _stateQueued;
    private bool _running;
    private bool _disposed;

    public BridgeSnapshotDelivery(IBridgeTransport transport, string route, IBridgeModelTurn modelTurn, string model)
        : this(transport, route, modelTurn, static () => null, model)
    {
    }

    // Whether the queue holds a captured full state.
    public bool StateQueued
    {
        get { lock (_gate) return _stateQueued; }
    }

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

    public void Enqueue(string snapshot)
    {
        lock (_gate)
        {
            if (_disposed) return;
            ClearPending();
            _pending.Enqueue((snapshot, "state", ++_produced));
            BridgeTelemetry.AddQueuedFrames(1);
            _pendingLength = snapshot.Length;
            _stateQueued = true;
            Start();
        }
    }

    // Deltas depend on every preceding frame. Bound retention and ask the
    // caller for a full recovery snapshot when a slow host falls behind.
    public bool EnqueueDelta(string delta)
    {
        lock (_gate)
        {
            if (_disposed) return true;
            if (_stateRequested || _pending.Count >= MaximumPendingFrames
                || _pendingLength + delta.Length > MaximumPendingLength) return false;
            _pending.Enqueue((delta, "delta", ++_produced));
            BridgeTelemetry.AddQueuedFrames(1);
            _pendingLength += delta.Length;
            Start();
            return true;
        }
    }

    // Tells the client why the route publishes nothing for now (see
    // ViewModelBridge.ReportRejectedKeys). It supersedes queued frames, which
    // the client cannot use without the full state that follows.
    public void EnqueueFailure(string failure)
    {
        lock (_gate)
        {
            if (_disposed) return;
            ClearPending();
            _pending.Enqueue((failure, "failure", ++_produced));
            BridgeTelemetry.AddQueuedFrames(1);
            _pendingLength = failure.Length;
            Start();
        }
    }

    // Runs in the model turn, so no producer can request or fold in between
    // taking the request and capturing the state. Returns the sequence the
    // captured state settles (every change so far), or 0 without a request.
    private long TakeStateRequest()
    {
        lock (_gate)
        {
            if (_disposed || !_stateRequested) return 0;
            _stateRequested = false;
            return _produced;
        }
    }

    // Completes when every request and frame handed to the delivery so far
    // was delivered, failed, found nothing to deliver, or the delivery was
    // disposed. Null when nothing is outstanding.
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
        if (released is not null)
            foreach (var waiter in released) waiter.TrySetResult();
    }

    private bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    private void ClearPending()
    {
        if (_pending.Count != 0) BridgeTelemetry.AddQueuedFrames(-_pending.Count);
        _pending.Clear();
        _pendingLength = 0;
        _stateRequested = false;
        _stateQueued = false;
    }

    private void Start()
    {
        if (_running) return;
        _running = true;
        _replyOrder.Add(this);
        _ = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            bool capture;
            long idle;
            lock (_gate)
            {
                if (_disposed || _pending.Count == 0 && !_stateRequested)
                {
                    _running = false;
                    _replyOrder.Remove(this);
                    // Nothing is outstanding: everything produced so far settled.
                    idle = _produced;
                }
                else idle = -1;
                capture = _pending.Count == 0;
            }
            if (idle >= 0)
            {
                Settle(idle);
                return;
            }

            string? snapshot = null;
            long sequence = 0;
            var started = false;
            try
            {
                // Cross the originating turn before entering native delivery.
                // A requested state is captured in that same turn.
                snapshot = modelTurn.Run(() =>
                {
                    started = true;
                    if (!capture || (sequence = TakeStateRequest()) == 0) return null;
                    return captureState();
                });
            }
            // Only a turn that could not start means the model context is
            // gone. An ObjectDisposedException from a getter or snapshot
            // writer is an ordinary capture failure.
            catch (ObjectDisposedException) when (!started) { Dispose(); }
            catch (Exception error) when (capture)
            {
                // There is no PropertyChanged raiser to report to here. The
                // producer still requires a full state, so its next change
                // requests another capture.
                BridgeTelemetry.RecordFailure("snapshot.capture", model, null, error);
                ViewsLog.SnapshotCaptureFailed(_logger, error, model, route, BridgeTelemetry.ErrorType(error));
            }
            var kind = "state";
            if (!capture)
                lock (_gate)
                {
                    if (_disposed || _pending.Count == 0) continue;
                    (snapshot, kind, sequence) = _pending.Dequeue();
                    BridgeTelemetry.AddQueuedFrames(-1);
                    _pendingLength -= snapshot.Length;
                    _stateQueued = false;
                }
            // A bridge released meanwhile publishes nothing more.
            if (snapshot is null || IsDisposed)
            {
                Settle(sequence);
                continue;
            }
            var timestamp = BridgeTelemetry.Timestamp();
            try
            {
                await PublishUtf8Async(_routes, route, snapshot).ConfigureAwait(false);
                BridgeTelemetry.RecordFrame(model, kind, snapshot, timestamp);
            }
            // The bridge was released while this frame was on its way, and its
            // host disposed the transport, as a closing window does (I-174).
            // There is no page left to deliver to.
            catch (ObjectDisposedException) when (IsDisposed) { }
            catch (Exception error)
            {
                BridgeTelemetry.RecordFailure("snapshot.delivery", model, null, error);
                ViewsLog.SnapshotDeliveryFailed(_logger, error, model, route, BridgeTelemetry.ErrorType(error));
            }
            Settle(sequence);
        }
    }

    // The bytes are Encoding.UTF8.GetBytes(snapshot). The pooled buffer is returned once the publication completes.
    private static async ValueTask PublishUtf8Async(BridgeLegacyRoutes routes, string route, string snapshot)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(snapshot));
        try
        {
            var length = Encoding.UTF8.GetBytes(snapshot, buffer);
            await routes.PublishAsync(route, buffer.AsMemory(0, length)).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        List<(long Sequence, TaskCompletionSource Settled)>? waiters;
        lock (_gate)
        {
            _disposed = true;
            ClearPending();
            waiters = _waiters;
            _waiters = null;
            _replyOrder.Remove(this);
        }
        if (waiters is not null)
            foreach (var waiter in waiters) waiter.Settled.TrySetResult();
    }
}
