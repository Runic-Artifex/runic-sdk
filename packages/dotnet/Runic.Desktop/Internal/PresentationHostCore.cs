using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    private WebApplication? _application;
    private Uri? _baseUrl;
    private int _disposed;

    internal PresentationHostCore(PresentationHostCoreOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(options.Port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Port, ushort.MaxValue);
        _options = options;
    }

    internal int Port => _baseUrl?.Port ?? _options.Port;

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

            var scope = _application!.Services.CreateScope();
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

            if (_application is not null)
            {
                try
                {
                    await _application.StopAsync().ConfigureAwait(false);
                }
                finally
                {
                    await _application.DisposeAsync().ConfigureAwait(false);
                    _application = null;
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
        if (_application is not null)
        {
            return;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        _options.ConfigureServices?.Invoke(builder.Services);
        builder.WebHost.ConfigureKestrel(options =>
        {
            var address = _options.NetworkExposure == PresentationNetworkExposure.AllInterfaces
                ? IPAddress.Any
                : IPAddress.Loopback;
            options.Listen(address, _options.Port);
        });

        var application = builder.Build();
        application.UseWebSockets();
        application.Run(DispatchAsync);
        await application.StartAsync(cancellationToken).ConfigureAwait(false);

        var addresses = application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.SingleOrDefault()
            ?? throw new InvalidOperationException("Kestrel did not publish a listening address.");
        var boundAddress = new Uri(address, UriKind.Absolute);
        _baseUrl = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), boundAddress.Port).Uri;
        _application = application;
    }

    private async Task DispatchAsync(HttpContext context)
    {
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
