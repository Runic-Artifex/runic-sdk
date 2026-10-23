using Runic.Application.Views;

namespace Runic.Application.Views.CsWebUi;

/// <summary>
/// Keeps each native route for a window while Bridge attachments replace its
/// managed handler. Dispose this transport before disposing the window.
/// </summary>
internal sealed class RebindableBridgeTransport : IBridgeTransport, IDisposable
{
    // The reply to a call of an unbound route (Views protocol 3).
    private const string ReleasedReply =
        "{\"ok\":false,\"state\":null,\"error\":{\"kind\":\"released\",\"message\":\"This View was released.\"}}";

    private readonly object _gate = new();
    private readonly IBridgeTransport _inner;
    private readonly Dictionary<string, Route> _routes = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Wraps the native transport whose routes are kept.</summary>
    public RebindableBridgeTransport(IBridgeTransport inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The route already has an active bridge or changed between sync and async.</exception>
    public IDisposable Bind(string name, Func<IBridgeArguments, string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_routes.TryGetValue(name, out var existing))
            {
                if (existing.IsAsync) throw new InvalidOperationException($"Route {name} changed from async to sync.");
                if (existing.Sync is not null) throw new InvalidOperationException($"Route {name} already has an active Bridge.");
                existing.Sync = handler;
                return new Lease(this, existing, ++existing.Generation);
            }

            var route = new Route(isAsync: false) { Sync = handler, Generation = 1 };
            route.Native = _inner.Bind(name, arguments => DispatchSync(route, arguments));
            _routes.Add(name, route);
            return new Lease(this, route, route.Generation);
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The route already has an active bridge or changed between sync and async.</exception>
    public IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_routes.TryGetValue(name, out var existing))
            {
                if (!existing.IsAsync) throw new InvalidOperationException($"Route {name} changed from sync to async.");
                if (existing.Async is not null) throw new InvalidOperationException($"Route {name} already has an active Bridge.");
                existing.Async = handler;
                return new Lease(this, existing, ++existing.Generation);
            }

            var route = new Route(isAsync: true) { Async = handler, Generation = 1 };
            route.Native = _inner.BindAsync(name, (arguments, token) => DispatchAsync(route, arguments, token));
            _routes.Add(name, route);
            return new Lease(this, route, route.Generation);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A state published after disposal is dropped: the window's routes are gone, and a Bridge's delivery that was
    /// already on its way when the window closed must end silently (I-174).
    /// </remarks>
    public void Publish(string name, string stateJson)
    {
        lock (_gate) if (_disposed) return;
        // The window can be disposed right after this transport, while the state is being sent.
        try { _inner.Publish(name, stateJson); }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed)) { }
    }

    private string DispatchSync(Route route, IBridgeArguments arguments)
    {
        Func<IBridgeArguments, string>? active;
        lock (_gate) active = _disposed ? null : route.Sync;
        return active is null ? ReleasedReply : active(arguments);
    }

    private ValueTask<string> DispatchAsync(Route route, IBridgeArguments arguments, CancellationToken token)
    {
        Func<IBridgeArguments, CancellationToken, ValueTask<string>>? active;
        lock (_gate) active = _disposed ? null : route.Async;
        return active is null ? ValueTask.FromResult(ReleasedReply) : active(arguments, token);
    }

    private void Release(Route route, long generation)
    {
        lock (_gate)
        {
            if (_disposed || route.Generation != generation) return;
            route.Sync = null;
            route.Async = null;
        }
    }

    /// <summary>Unbinds every native route. Dispose this transport before its window.</summary>
    public void Dispose()
    {
        List<IDisposable> native;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            native = _routes.Values.Select(route => route.Native!).ToList();
            _routes.Clear();
        }
        foreach (var binding in native) binding.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class Route(bool isAsync)
    {
        public bool IsAsync { get; } = isAsync;
        public long Generation { get; set; }
        public IDisposable? Native { get; set; }
        public Func<IBridgeArguments, string>? Sync { get; set; }
        public Func<IBridgeArguments, CancellationToken, ValueTask<string>>? Async { get; set; }
    }

    private sealed class Lease(RebindableBridgeTransport owner, Route route, long generation) : IDisposable
    {
        private RebindableBridgeTransport? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(route, generation);
    }
}
