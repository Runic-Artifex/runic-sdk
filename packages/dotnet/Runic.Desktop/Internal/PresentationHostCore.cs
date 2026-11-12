using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace Runic.Desktop.Internal;

internal enum PresentationNetworkExposure
{
    Loopback,
    AllInterfaces,
}

internal sealed record PresentationHostCoreOptions(
    int Port,
    PresentationNetworkExposure NetworkExposure,
    Action<IServiceCollection>? ConfigureServices = null);

internal sealed class PresentationHostCore : IAsyncDisposable
{
    private readonly PresentationHostCoreOptions _options;
    private readonly ConcurrentDictionary<string, PresentationSurfaceRegistration> _surfaces =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private IPresentationServer? _server;
    private Uri? _baseUrl;
    // The IPv6 loopback listener that serves the frames of regions whose host page has an IPv4 loopback origin, started
    // on the first such frame (GetIpv6LoopbackUrlAsync); whether [::1] could not be bound.
    private IPresentationServer? _ipv6Server;
    private Uri? _ipv6BaseUrl;
    private bool _ipv6Unavailable;
    private int _disposed;

    internal PresentationHostCore(PresentationHostCoreOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(options.Port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Port, ushort.MaxValue);
        _options = options;
    }

    internal int Port => _baseUrl?.Port ?? _options.Port;

    internal IServiceProvider? Services => _server?.Services;

    internal bool IsPublic => _options.NetworkExposure == PresentationNetworkExposure.AllInterfaces;

    internal CancellationToken ShutdownToken => _shutdown.Token;

    internal async ValueTask<PresentationSurfaceRegistration> AttachSurfaceAsync(
        string pathBase,
        Func<PresentationExchange, Task> handler,
        Func<ValueTask> hostClosing,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(hostClosing);
        var normalizedPathBase = NormalizePathBase(pathBase);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await StartUnderGateAsync(cancellationToken).ConfigureAwait(false);
            if ((normalizedPathBase.Length == 0 && !_surfaces.IsEmpty) ||
                (normalizedPathBase.Length != 0 && _surfaces.ContainsKey(string.Empty)))
            {
                throw new InvalidOperationException("A root surface cannot share a listener with path-based surfaces.");
            }

            var scope = _server!.Services.CreateScope();
            var registration = new PresentationSurfaceRegistration(
                this,
                normalizedPathBase,
                CreateSurfaceUrl(normalizedPathBase),
                handler,
                hostClosing,
                scope);
            if (!_surfaces.TryAdd(normalizedPathBase, registration))
            {
                scope.Dispose();
                throw new InvalidOperationException($"A presentation surface is already registered at '{normalizedPathBase}'.");
            }

            return registration;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    // The URL of a surface on the IPv6 loopback listener, which starts with the first call, or null when [::1] cannot be
    // bound, such as with IPv6 turned off; a failed bind is not retried. A page of an IPv4 loopback origin that frames a
    // page from it frames another site: the frame receives none of that page's cookies, and Chromium renders it in a
    // process of its own (region mode, D059.1).
    internal async ValueTask<Uri?> GetIpv6LoopbackUrlAsync(PresentationSurfaceRegistration registration, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsPublic) throw new InvalidOperationException("A listener on every interface serves no loopback frames.");
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_ipv6BaseUrl is null && !_ipv6Unavailable)
            {
                var server = PresentationServers.Create(_options.ConfigureServices);
                try
                {
                    var port = await server.StartAsync(IPAddress.IPv6Loopback, 0, exchange => DispatchAsync(exchange, Volatile.Read(ref _ipv6BaseUrl)),
                        cancellationToken).ConfigureAwait(false);
                    _ipv6Server = server;
                    Volatile.Write(ref _ipv6BaseUrl, new Uri($"http://[::1]:{port}/"));
                }
                catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    _ipv6Unavailable = true;
                }
                catch
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            return _ipv6BaseUrl is { } baseUrl ? CreateSurfaceUrl(baseUrl, registration.PathBase) : null;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal void Detach(PresentationSurfaceRegistration registration)
    {
        if (_surfaces.TryGetValue(registration.PathBase, out var current) && ReferenceEquals(current, registration))
        {
            _surfaces.TryRemove(registration.PathBase, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _shutdown.Cancel();
            foreach (var surface in _surfaces.Values.ToArray())
            {
                await surface.DisposeFromHostAsync().ConfigureAwait(false);
            }
            _surfaces.Clear();

            try
            {
                if (_ipv6Server is not null) await _ipv6Server.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _ipv6Server = null;
                _ipv6BaseUrl = null;
                if (_server is not null)
                {
                    try
                    {
                        await _server.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        _server = null;
                        _baseUrl = null;
                    }
                }
            }
        }
        finally
        {
            _shutdown.Dispose();
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    private async Task StartUnderGateAsync(CancellationToken cancellationToken)
    {
        if (_server is not null)
        {
            return;
        }

        var address = _options.NetworkExposure == PresentationNetworkExposure.AllInterfaces
            ? IPAddress.Any
            : IPAddress.Loopback;
        var server = PresentationServers.Create(_options.ConfigureServices);
        int port;
        try
        {
            port = await server.StartAsync(address, _options.Port, exchange => DispatchAsync(exchange, Volatile.Read(ref _baseUrl)),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        // Neither UriBuilder, which lowercases the scheme through TextInfo, nor CultureInfo.InvariantCulture: both load
        // ICU (≈1.9 ms and ≈4.8 MiB) before the first window opens (S1, D053.25). A port formats the same in every
        // culture, and the integer formatter needs none for it.
        Volatile.Write(ref _baseUrl, new Uri($"http://127.0.0.1:{port}/"));
        _server = server;
    }

    // listener is the base URL of the listener the request arrived on, null while it is starting.
    private async Task DispatchAsync(PresentationExchange exchange, Uri? listener)
    {
        if (listener is null || !AcceptsHost(exchange.GetHost(), listener.Port))
        {
            exchange.StatusCode = PresentationStatus.MisdirectedRequest;
            return;
        }
        exchange.Listener = listener;

        var registration = FindSurface(exchange.Path, out var remainingPath);
        if (registration is null)
        {
            exchange.StatusCode = PresentationStatus.NotFound;
            return;
        }

        if (registration.PathBase.Length != 0)
        {
            exchange.PathBase += registration.PathBase;
            exchange.Path = remainingPath.Length != 0 ? remainingPath : "/";
        }

        using var requestCancellation = new PresentationRequestCancellation(
            exchange.RequestAborted,
            registration.ClosingToken,
            _shutdown.Token);
        exchange.Cancellation = requestCancellation;
        exchange.RequestAborted = requestCancellation.Token;
        await registration.HandleAsync(exchange).ConfigureAwait(false);
    }

    // A DNS-rebinding page reaches the loopback listener under its own host name
    // and would otherwise read credential-bearing bootstrap scripts same-origin.
    // A non-loopback listener is reachable by any network peer and is protected
    // only by its explicit origin and credential policy.
    private bool AcceptsHost((string Host, int? Port) host, int port)
    {
        if (IsPublic)
        {
            return true;
        }

        if (host.Port != port)
        {
            return false;
        }

        var name = host.Host;
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(name, out var address) && IPAddress.IsLoopback(address));
    }

    private PresentationSurfaceRegistration? FindSurface(string requestPath, out string remainingPath)
    {
        if (_surfaces.TryGetValue(string.Empty, out var root))
        {
            remainingPath = requestPath;
            return root;
        }

        foreach (var registration in _surfaces.Values)
        {
            if (PresentationExchange.StartsWithSegments(requestPath, registration.PathBase, out remainingPath))
            {
                return registration;
            }
        }

        remainingPath = string.Empty;
        return null;
    }

    private Uri CreateSurfaceUrl(string pathBase) => CreateSurfaceUrl(_baseUrl!, pathBase);

    private static Uri CreateSurfaceUrl(Uri baseUrl, string pathBase) =>
        pathBase.Length == 0
            ? baseUrl
            : new Uri(baseUrl, $"{pathBase.TrimStart('/')}/");

    private static string NormalizePathBase(string pathBase)
    {
        ArgumentNullException.ThrowIfNull(pathBase);
        if (pathBase.Length == 0 || pathBase == "/")
        {
            return string.Empty;
        }

        var segment = pathBase.Trim('/');
        if (segment.Length == 0 || segment.Contains('/') ||
            segment.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("A surface path must contain one ASCII letter, digit, hyphen, or underscore segment.", nameof(pathBase));
        }

        return $"/{segment}";
    }
}

internal sealed class PresentationSurfaceRegistration : IAsyncDisposable
{
    private readonly PresentationHostCore _host;
    private readonly Func<PresentationExchange, Task> _handler;
    private readonly Func<ValueTask> _hostClosing;
    private readonly IServiceScope _scope;
    private readonly CancellationTokenSource _closing = new();
    private readonly CancellationToken _closingToken;
    private readonly object _requestGate = new();
    private TaskCompletionSource _requestsDrained = CompletedSource();
    private int _activeRequests;
    private int _disposed;

    internal PresentationSurfaceRegistration(
        PresentationHostCore host,
        string pathBase,
        Uri url,
        Func<PresentationExchange, Task> handler,
        Func<ValueTask> hostClosing,
        IServiceScope scope)
    {
        _host = host;
        PathBase = pathBase;
        Url = url;
        _handler = handler;
        _hostClosing = hostClosing;
        _scope = scope;
        _closingToken = _closing.Token;
    }

    internal string PathBase { get; }

    internal Uri Url { get; }

    internal CancellationToken ClosingToken => _closingToken;

    internal async Task HandleAsync(PresentationExchange exchange)
    {
        TaskCompletionSource drained;
        lock (_requestGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                exchange.StatusCode = PresentationStatus.NotFound;
                return;
            }

            if (_activeRequests++ == 0)
            {
                _requestsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            drained = _requestsDrained;
        }

        try
        {
            using var requestScope = _scope.ServiceProvider.CreateScope();
            exchange.RequestServices = requestScope.ServiceProvider;
            await _handler(exchange).ConfigureAwait(false);
        }
        finally
        {
            lock (_requestGate)
            {
                if (--_activeRequests == 0)
                {
                    drained.TrySetResult();
                }
            }
        }
    }

    public ValueTask DisposeAsync() => DisposeCoreAsync(detach: true);

    internal ValueTask DisposeFromHostAsync() => DisposeCoreAsync(detach: false);

    private async ValueTask DisposeCoreAsync(bool detach)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _closing.Cancel();
        if (detach)
        {
            _host.Detach(this);
        }
        else
        {
            await _hostClosing().ConfigureAwait(false);
        }
        Task requestsDrained;
        lock (_requestGate)
        {
            requestsDrained = _requestsDrained.Task;
        }
        await requestsDrained.ConfigureAwait(false);
        _scope.Dispose();
        _closing.Dispose();
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
