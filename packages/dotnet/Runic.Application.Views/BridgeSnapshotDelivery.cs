using System.Diagnostics;

namespace Runic.Application.Views;

// Full snapshots supersede earlier snapshots. Delivery is deliberately outside
// the model turn: a host may synchronously wait for its native/UI dispatcher.
// Operations and interaction messages never use this coalescing queue.
internal sealed class BridgeSnapshotDelivery(IBridgeTransport transport, string route, IBridgeModelTurn modelTurn) : IDisposable
{
    private readonly object _gate = new();
    private string? _pending;
    private bool _running;
    private bool _disposed;

    public void Enqueue(string snapshot)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = snapshot;
            if (_running) return;
            _running = true;
            ThreadPool.QueueUserWorkItem(static state => ((BridgeSnapshotDelivery)state!).Drain(), this);
        }
    }

    private void Drain()
    {
        while (true)
        {
            // Cross the originating turn before entering native delivery.
            try { modelTurn.Run(static () => { }); }
            catch (ObjectDisposedException) { Dispose(); }
            string snapshot;
            lock (_gate)
            {
                if (_disposed || _pending is null)
                {
                    _running = false;
                    return;
                }
                snapshot = _pending;
                _pending = null;
            }
            try { transport.Publish(route, snapshot); }
            catch (Exception error) { Trace.TraceError($"Bridge snapshot delivery for {route} failed: {error}"); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending = null;
        }
    }
}
