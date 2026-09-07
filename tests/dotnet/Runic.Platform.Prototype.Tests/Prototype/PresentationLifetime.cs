namespace Runic.Platform.Prototype;

internal sealed class PresentationLifetime : IAsyncDisposable, Runic.Application.Bridge.IApplicationPresentationLifetime
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _closed;
    private int _operations;
    private int _picker;
    private bool _hasOwner;
    private readonly Func<bool>? _ownerAvailable;
    private readonly HashSet<PresentationLease> _leases = [];
    private Exception? _cleanupFailure;

    internal PresentationLifetime(Func<bool>? ownerAvailable = null) { Shutdown = _shutdown.Token; _ownerAvailable = ownerAvailable; }
    internal Guid Generation { get; } = Guid.NewGuid();
    internal CancellationToken Shutdown { get; }
    internal bool IsClosing { get { lock (_gate) return _closed is not null; } }
    internal bool HasOwner
    {
        get
        {
            bool attached;
            lock (_gate) { if (_closed is not null) return false; attached = _hasOwner; }
            return attached || _ownerAvailable?.Invoke() == true;
        }
    }

    internal bool Own(PresentationLease lease)
    {
        lock (_gate) { if (_closed is not null) return false; return _leases.Add(lease); }
    }
    internal void Forget(PresentationLease lease) { lock (_gate) _leases.Remove(lease); }

    // A native adapter must eventually supply verified identity and dispatch here.
    // This prototype only models the transition; it cannot certify ownership.
    internal void AttachTestOwner()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed is not null, this);
            if (_hasOwner) throw new InvalidOperationException("A generation cannot replace its owner.");
            _hasOwner = true;
        }
    }

    internal IDisposable? TryBeginOperation()
    {
        lock (_gate)
        {
            if (_closed is not null) return null;
            _operations++;
            return new Operation(this);
        }
    }

    internal IDisposable? TryBeginPicker() => Interlocked.CompareExchange(ref _picker, 1, 0) == 0
        ? new Picker(this) : null;

    internal void RecordCleanupFailure(Exception error)
    {
        lock (_gate) _cleanupFailure ??= error;
    }

    private void CompleteOperation()
    {
        lock (_gate)
        {
            if (--_operations == 0 && _closed is not null) _drained.TrySetResult();
        }
    }

    public ValueTask StopAsync() => DisposeAsync();

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource closed;
        lock (_gate)
        {
            if (_closed is not null) return new(_closed.Task);
            closed = _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_operations == 0) _drained.TrySetResult();
        }
        // Cancellation callbacks and native cleanup never run while holding _gate.
        _ = CloseAsync(closed);
        return new(closed.Task);
    }

    private async Task CloseAsync(TaskCompletionSource closed)
    {
        Exception? failure = null;
        try { await _shutdown.CancelAsync().ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        await _drained.Task.ConfigureAwait(false);
        PresentationLease[] leases;
        lock (_gate) leases = [.. _leases];
        // Start every release before joining; one provider may depend on another.
        var releases = leases.Select(lease => lease.DisposeAsync().AsTask()).ToArray();
        try { await Task.WhenAll(releases).ConfigureAwait(false); }
        catch { /* Each lease records its release failure below. */ }
        _shutdown.Dispose();
        lock (_gate)
        {
            if (_cleanupFailure is not null)
                failure = failure is null ? _cleanupFailure : new AggregateException(failure, _cleanupFailure);
        }
        if (failure is null) closed.TrySetResult();
        else closed.TrySetException(failure);
    }

    private sealed class Operation(PresentationLifetime lifetime) : IDisposable
    {
        private PresentationLifetime? _lifetime = lifetime;
        public void Dispose() => Interlocked.Exchange(ref _lifetime, null)?.CompleteOperation();
    }

    private sealed class Picker(PresentationLifetime lifetime) : IDisposable
    {
        private PresentationLifetime? _lifetime = lifetime;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _lifetime, null) is { } owner) Volatile.Write(ref owner._picker, 0);
        }
    }
}
