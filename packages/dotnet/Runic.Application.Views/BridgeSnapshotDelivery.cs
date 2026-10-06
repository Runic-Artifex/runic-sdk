using System.Diagnostics;

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
internal sealed class BridgeSnapshotDelivery(IBridgeTransport transport, string route, IBridgeModelTurn modelTurn,
    Func<string?> captureState) : IDisposable
{
    private const int MaximumPendingFrames = 64;
    private const int MaximumPendingLength = 1024 * 1024;
    private readonly object _gate = new();
    private readonly Queue<string> _pending = new();
    private int _pendingLength;
    private bool _stateRequested;
    private bool _stateQueued;
    private bool _running;
    private bool _disposed;

    public BridgeSnapshotDelivery(IBridgeTransport transport, string route, IBridgeModelTurn modelTurn)
        : this(transport, route, modelTurn, static () => null)
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
            Start();
        }
    }

    public void Enqueue(string snapshot)
    {
        lock (_gate)
        {
            if (_disposed) return;
            ClearPending();
            _pending.Enqueue(snapshot);
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
            _pending.Enqueue(delta);
            _pendingLength += delta.Length;
            Start();
            return true;
        }
    }

    // Runs in the model turn, so no producer can request or fold in between
    // taking the request and capturing the state.
    private bool TakeStateRequest()
    {
        lock (_gate)
        {
            if (_disposed || !_stateRequested) return false;
            _stateRequested = false;
            return true;
        }
    }

    private void ClearPending()
    {
        _pending.Clear();
        _pendingLength = 0;
        _stateRequested = false;
        _stateQueued = false;
    }

    private void Start()
    {
        if (_running) return;
        _running = true;
        _ = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            bool capture;
            lock (_gate)
            {
                if (_disposed || _pending.Count == 0 && !_stateRequested)
                {
                    _running = false;
                    return;
                }
                capture = _pending.Count == 0;
            }

            string? snapshot = null;
            var started = false;
            try
            {
                // Cross the originating turn before entering native delivery.
                // A requested state is captured in that same turn.
                snapshot = modelTurn.Run(() =>
                {
                    started = true;
                    return capture && TakeStateRequest() ? captureState() : null;
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
                Trace.TraceError($"Bridge snapshot capture for {route} failed: {error}");
            }
            if (!capture)
                lock (_gate)
                {
                    if (_disposed || _pending.Count == 0) continue;
                    snapshot = _pending.Dequeue();
                    _pendingLength -= snapshot.Length;
                    _stateQueued = false;
                }
            if (snapshot is null) continue;
            try
            {
                if (transport is IAsyncBridgeTransport asynchronous)
                    await asynchronous.PublishAsync(route, snapshot).ConfigureAwait(false);
                else transport.Publish(route, snapshot);
            }
            catch (Exception error) { Trace.TraceError($"Bridge snapshot delivery for {route} failed: {error}"); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            ClearPending();
        }
    }
}
