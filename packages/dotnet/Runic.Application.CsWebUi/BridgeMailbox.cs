using System.Text;
using Runic.Application.Bridge;

namespace Runic.Application.CsWebUi;

// No native events are retained: each poll drains through its own live callback.
internal sealed class BridgeMailbox : IAsyncDisposable
{
    private readonly ApplicationBridgeSession _session;
    private readonly BridgeLimits _limits;
    private readonly BridgeConnectionAdmission _admission = new();
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    private readonly object _gate = new();
    private readonly List<BridgeHostEnvelope> _frames = [];
    private readonly CancellationTokenSource _shutdown = new();
    private int _bytes;
    private bool _overflow;
    private int _polling;
    private int _disposed;
    private TaskCompletionSource? _disposeCompletion;

    internal BridgeMailbox(ApplicationBridgeSession session, BridgeLimits limits)
    {
        _session = session;
        _limits = limits;
        session.EventProduced += OnEvent;
    }

    internal async ValueTask<string> DispatchAsync(ulong client, ulong connection, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!ApplicationBridgeCodec.TryDecodeClient(bytes.Span, out var envelope, _limits))
            throw new InvalidOperationException("Invalid application frame.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _dispatch.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!_admission.CanAccept(client, connection, envelope!)) throw new InvalidOperationException("Connection not admitted.");
            if (envelope!.Kind == "initialize")
                lock (_gate) { _frames.Clear(); _bytes = 0; _overflow = false; }
            var response = await _session.DispatchAsync(envelope, linked.Token).ConfigureAwait(false);
            _admission.Accept(client, connection, envelope, response);
            Enqueue(response);
            return Drain();
        }
        finally { _dispatch.Release(); }
    }

    internal async ValueTask<string> PollAsync(ulong client, ulong connection, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _polling, 1) != 0) throw new InvalidOperationException("Only one event poll may be outstanding.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await _dispatch.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    if (!_admission.Owns(client, connection)) throw new InvalidOperationException("Connection not admitted.");
                    lock (_gate) if (_frames.Count != 0 || _overflow) return Drain();
                }
                finally { _dispatch.Release(); }
                await Task.Delay(10, linked.Token).ConfigureAwait(false);
            }
            return "[]";
        }
        finally { Volatile.Write(ref _polling, 0); }
    }

    internal async ValueTask DisconnectAsync(ulong client, ulong connection)
    {
        await _dispatch.WaitAsync().ConfigureAwait(false);
        try { _admission.Disconnect(client, connection); }
        finally { _dispatch.Release(); }
    }

    private void OnEvent(object? sender, BridgeHostEnvelope frame) => Enqueue(frame);

    private void Enqueue(BridgeHostEnvelope frame)
    {
        lock (_gate)
        {
            if (_disposed != 0 || _overflow) return;
            int bytes;
            try { bytes = ApplicationBridgeCodec.EncodeHost(frame, _limits).Length; }
            catch { _overflow = true; return; }
            if (_frames.Count >= 64 || bytes > 1_048_576 - _bytes) { _overflow = true; _frames.Clear(); _bytes = 0; return; }
            _frames.Add(frame);
            _bytes += bytes;
        }
    }

    private string Drain()
    {
        lock (_gate)
        {
            if (_overflow) throw new InvalidOperationException("Application event buffer exceeded its limit; reconnect required.");
            _frames.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));
            string result = "[" + string.Join(",", _frames.Select(frame => Encoding.UTF8.GetString(ApplicationBridgeCodec.EncodeHost(frame, _limits)))) + "]";
            _frames.Clear(); _bytes = 0;
            return result;
        }
    }

    public ValueTask DisposeAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _disposeCompletion, completion, null) is { } existing)
            return new(existing.Task);
        Volatile.Write(ref _disposed, 1);
        _ = CompleteDisposeAsync(completion);
        return new(completion.Task);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try { await DisposeCoreAsync().ConfigureAwait(false); completion.SetResult(); }
        catch (Exception error) { completion.SetException(error); }
    }

    private async Task DisposeCoreAsync()
    {
        _session.EventProduced -= OnEvent;
        Exception? cancellationFailure = null;
        try { await _shutdown.CancelAsync().ConfigureAwait(false); }
        catch (Exception error) { cancellationFailure = error; }
        // Stop owner work before waiting for a command that may itself await a picker.
        try { await _session.StopPresentationAsync().ConfigureAwait(false); }
        catch { /* Session disposal reports the retained stop failure after cleanup. */ }
        await _dispatch.WaitAsync().ConfigureAwait(false);
        try { await _session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (cancellationFailure is not null) { throw new AggregateException(cancellationFailure, error); }
        finally { _dispatch.Release(); }
        if (cancellationFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancellationFailure).Throw();
        // Native callbacks may still be unwinding; do not dispose their synchronization primitives.
    }
}
