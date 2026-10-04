using System.Diagnostics;

namespace Runic.Application.Views;

// Full snapshots supersede earlier snapshots. Delivery is deliberately outside
// the model turn: a host may synchronously wait for its native/UI dispatcher.
// Operations and interaction messages never use this coalescing queue.
internal sealed class BridgeSnapshotDelivery(IBridgeTransport transport, string route, IBridgeModelTurn modelTurn) : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<string> _pending = new();
    private int _pendingLength;
    private bool _running;
    private bool _disposed;

    public void Enqueue(string snapshot)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending.Clear();
            _pending.Enqueue(snapshot);
            _pendingLength = snapshot.Length;
            if (_running) return;
            _running = true;
            _ = Task.Run(DrainAsync);
        }
    }

    // Deltas depend on every preceding frame. Bound retention and ask the
    // caller for a full recovery snapshot when a slow host falls behind.
    public bool EnqueueDelta(string delta)
    {
        lock (_gate)
        {
            if (_disposed) return true;
            if (_pending.Count >= 64 || _pendingLength + delta.Length > 1024 * 1024) return false;
            _pending.Enqueue(delta);
            _pendingLength += delta.Length;
            if (!_running)
            {
                _running = true;
                _ = Task.Run(DrainAsync);
            }
            return true;
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            // Cross the originating turn before entering native delivery.
            try { modelTurn.Run(static () => { }); }
            catch (ObjectDisposedException) { Dispose(); }
            string snapshot;
            lock (_gate)
            {
                if (_disposed || _pending.Count == 0)
                {
                    _running = false;
                    return;
                }
                snapshot = _pending.Dequeue();
                _pendingLength -= snapshot.Length;
            }
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
            _pending.Clear();
            _pendingLength = 0;
        }
    }
}
