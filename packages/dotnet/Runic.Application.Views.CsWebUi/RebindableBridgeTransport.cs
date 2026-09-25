using Runic.Application.Views;

namespace Runic.Application.Views.CsWebUi;

/// <summary>
/// Keeps each native route for a window while Bridge attachments replace its
/// managed handler. Dispose this transport before disposing the window.
/// </summary>
public sealed class RebindableBridgeTransport : IBridgeTransport, IDisposable
{
    private const string DisconnectedReply =
        "{\"ok\":false,\"state\":null,\"error\":{\"kind\":\"disconnected\",\"message\":\"This view is no longer connected.\"}}";

    private readonly object _gate = new();
    private readonly IBridgeTransport _inner;
    private readonly Dictionary<string, Route> _routes = new(StringComparer.Ordinal);
    private bool _disposed;

    public RebindableBridgeTransport(IBridgeTransport inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

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

    public void Publish(string name, string stateJson)
    {
        lock (_gate) ThrowIfDisposed();
        _inner.Publish(name, stateJson);
    }

    private string DispatchSync(Route route, IBridgeArguments arguments)
    {
        Func<IBridgeArguments, string>? active;
        lock (_gate) active = _disposed ? null : route.Sync;
        return active is null ? DisconnectedReply : active(arguments);
    }

    private ValueTask<string> DispatchAsync(Route route, IBridgeArguments arguments, CancellationToken token)
    {
        Func<IBridgeArguments, CancellationToken, ValueTask<string>>? active;
        lock (_gate) active = _disposed ? null : route.Async;
        return active is null ? ValueTask.FromResult(DisconnectedReply) : active(arguments, token);
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
        if (_disposed) throw new ObjectDisposedException(nameof(RebindableBridgeTransport));
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
