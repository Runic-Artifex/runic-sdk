using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace Runic.Platform.Runtime;

// Delivers open requests to Opened handlers on a worker thread, one at a time and in order. Requests posted before
// the first handler subscribes wait for it. Sources post from any thread; disposal drops later requests.
internal sealed class OpenRequestQueue : IDesktopOpenRequests
{
    private readonly Channel<DesktopOpenRequest> _requests = Channel.CreateUnbounded<DesktopOpenRequest>(new() { SingleReader = true });
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private readonly List<Func<ValueTask>> _detach = [];
    private EventHandler<DesktopOpenRequest>? _opened;
    private int _disposed;

    internal OpenRequestQueue() => _ = Task.Run(PumpAsync);

    public event EventHandler<DesktopOpenRequest>? Opened
    {
        add
        {
            lock (_gate) _opened += value;
            if (value is not null) _subscribed.TrySetResult();
        }
        remove { lock (_gate) _opened -= value; }
    }

    internal void Post(DesktopOpenRequest request)
    {
        if (Volatile.Read(ref _disposed) == 0) _requests.Writer.TryWrite(request);
    }

    // A source to detach when the queue is disposed, such as an instance subscription.
    internal void OnDispose(Func<ValueTask> detach) { lock (_gate) _detach.Add(detach); }

    private async Task PumpAsync()
    {
        try { await _subscribed.Task.WaitAsync(_stopping.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        await foreach (var request in _requests.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            EventHandler<DesktopOpenRequest>? handlers;
            lock (_gate) handlers = _opened;
            if (handlers is null || Volatile.Read(ref _disposed) != 0) continue;
            // As with an event raised on the thread pool, a throwing handler fails the process.
            try { handlers(this, request); }
            catch (Exception error) { ThreadPool.QueueUserWorkItem(static state => ((ExceptionDispatchInfo)state!).Throw(), ExceptionDispatchInfo.Capture(error)); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _requests.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);
        Func<ValueTask>[] detach;
        lock (_gate) { detach = [.. _detach]; _detach.Clear(); }
        foreach (var action in detach) await action().ConfigureAwait(false);
        _stopping.Dispose();
    }
}
