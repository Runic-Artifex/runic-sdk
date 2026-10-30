using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        Func<HttpContext, Task> handler,
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
            port = await server.StartAsync(address, _options.Port, DispatchAsync, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _baseUrl = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), port).Uri;
        _server = server;
    }

    private async Task DispatchAsync(HttpContext context)
    {
        if (!AcceptsHost(context.Request.Host))
        {
            context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }

        var registration = FindSurface(context.Request.Path, out var remainingPath);
        if (registration is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (registration.PathBase.Length != 0)
        {
            context.Request.PathBase = context.Request.PathBase.Add(registration.PathBase);
            context.Request.Path = remainingPath.HasValue ? remainingPath : new PathString("/");
        }

        using var requestCancellation = new PresentationRequestCancellation(
            context.RequestAborted,
            registration.ClosingToken,
            _shutdown.Token);
        context.Items[typeof(PresentationRequestCancellation)] = requestCancellation;
        context.RequestAborted = requestCancellation.Token;
        await registration.HandleAsync(context).ConfigureAwait(false);
    }

    // A DNS-rebinding page reaches the loopback listener under its own host name
    // and would otherwise read credential-bearing bootstrap scripts same-origin.
    // A non-loopback listener is reachable by any network peer and is protected
    // only by its explicit origin and credential policy.
    private bool AcceptsHost(HostString host)
    {
        if (IsPublic)
        {
            return true;
        }

        if (host.Port != Port)
        {
            return false;
        }

        var name = host.Host;
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(name, out var address) && IPAddress.IsLoopback(address));
    }

    private PresentationSurfaceRegistration? FindSurface(PathString requestPath, out PathString remainingPath)
    {
        if (_surfaces.TryGetValue(string.Empty, out var root))
        {
            remainingPath = requestPath;
            return root;
        }

        foreach (var registration in _surfaces.Values)
        {
            if (requestPath.StartsWithSegments(registration.PathBase, out remainingPath))
            {
                return registration;
            }
        }

        remainingPath = default;
        return null;
    }

    private Uri CreateSurfaceUrl(string pathBase) =>
        pathBase.Length == 0
            ? _baseUrl!
            : new Uri(_baseUrl!, $"{pathBase.TrimStart('/')}/");

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
    private readonly Func<HttpContext, Task> _handler;
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
        Func<HttpContext, Task> handler,
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

    internal async Task HandleAsync(HttpContext context)
    {
        TaskCompletionSource drained;
        lock (_requestGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
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
            context.RequestServices = requestScope.ServiceProvider;
            await _handler(context).ConfigureAwait(false);
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

// The application, not its embedded presentation server, owns process signals.
internal sealed class ProcessOwnedLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
