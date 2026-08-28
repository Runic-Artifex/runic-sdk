using System.Collections.Concurrent;
using Runic.Desktop.Internal;

namespace Runic.Desktop;

/// <summary>Owns listener resources and a set of isolated presentation surfaces.</summary>
public sealed class DesktopHost : IAsyncDisposable
{
    private readonly DesktopHostOptions _options;
    private readonly PresentationHostCore _core;
    private readonly ConcurrentDictionary<Guid, DesktopSurface> _surfaces = new();
    private int _disposed;

    private DesktopHost(DesktopHostOptions options)
    {
        ValidateOptions(options);
        _options = options;
        _core = CreateCore(options, options.Port);
    }

    /// <summary>Creates a host. Its listener binds when the first surface starts.</summary>
    public static ValueTask<DesktopHost> StartAsync(
        DesktopHostOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new DesktopHost(options ?? new DesktopHostOptions()));
    }

    /// <summary>Gets the bound port, or the configured port before the first surface starts.</summary>
    public int Port => _core.Port;

    /// <summary>Creates and starts one isolated surface namespace.</summary>
    public async ValueTask<DesktopSurface> CreateSurfaceAsync(
        DesktopSurfaceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var configured = options ?? new DesktopSurfaceOptions();
        var rootFolder = Path.GetFullPath(configured.RootFolder);
        if (!Directory.Exists(rootFolder))
        {
            throw new DirectoryNotFoundException($"The Desktop content root does not exist: {rootFolder}");
        }

        var id = Guid.NewGuid();
        var path = configured.Path ?? $"surface-{id:N}";
        var isolatedCore = configured.UseIsolatedListener ? CreateCore(_options, port: 0) : null;
        var core = isolatedCore ?? _core;
        var security = ToCorePolicy(configured.Security ?? _options.Security);
        IWebUiEmbeddedHostFactory embeddedFactory = _options.WindowHostFactory is null
            ? WebUiEmbeddedHostFactory.Instance
            : new DesktopWindowHostFactoryAdapter(_options.WindowHostFactory);
        var runtime = new PresentationSurfaceRuntimeOptions(
            rootFolder,
            NormalizeOptionalPath(_options.BrowserFolder),
            embeddedFactory,
            _options.WaitForConnection,
            _options.ConnectionTimeout);
        var engine = new WebUiWindow(core, path, security, runtime);
        var surface = new DesktopSurface(this, id, engine, isolatedCore);
        if (!_surfaces.TryAdd(id, surface))
        {
            await engine.DisposeAsync().ConfigureAwait(false);
            if (isolatedCore is not null)
            {
                await isolatedCore.DisposeAsync().ConfigureAwait(false);
            }
            throw new InvalidOperationException("The surface identifier could not be reserved.");
        }

        try
        {
            if (configured.ContentHandler is { } handler)
            {
                engine.SetRequestHandler(async (context, requestPath, token) =>
                {
                    context.Items.TryGetValue(typeof(PresentationRequestCancellation), out var cancellation);
                    var request = new ContentRequest(
                        requestPath,
                        context.Request.Method,
                        context.RequestServices,
                        cancellation as PresentationRequestCancellation);
                    var response = await handler(request, token).ConfigureAwait(false);
                    return response?.ToCompatibilityContent();
                });
            }
            await engine.StartServerAsync(configured.Content, cancellationToken).ConfigureAwait(false);
            return surface;
        }
        catch
        {
            _surfaces.TryRemove(id, out _);
            await surface.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        var calledFromCallback = _surfaces.Values.Any(static surface => surface.IsExecutingCallback);
        var dispose = DisposeCoreAsync();
        GC.SuppressFinalize(this);
        return calledFromCallback ? ValueTask.CompletedTask : new ValueTask(dispose);
    }

    private async Task DisposeCoreAsync()
    {
        foreach (var surface in _surfaces.Values.ToArray())
        {
            await surface.DisposeFromHostAsync().ConfigureAwait(false);
        }
        _surfaces.Clear();
        await _core.DisposeAsync().ConfigureAwait(false);
    }

    internal void Detach(Guid id) => _surfaces.TryRemove(id, out _);

    internal void Report(DesktopDiagnostic diagnostic) => _options.DiagnosticSink?.Invoke(diagnostic);

    private static PresentationHostCore CreateCore(DesktopHostOptions options, int port) => new(
        new PresentationHostCoreOptions(
            port,
            options.NetworkExposure == DesktopNetworkExposure.AllInterfaces
                ? PresentationNetworkExposure.AllInterfaces
                : PresentationNetworkExposure.Loopback,
            options.ConfigureServices));

    private static PresentationSecurityPolicy ToCorePolicy(DesktopSecurityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var origins = policy.AdditionalOrigins
            .Select(PresentationSecurityPolicy.CanonicalOrigin)
            .ToHashSet(StringComparer.Ordinal);
        return new PresentationSecurityPolicy(
            policy.RequireSessionCredential,
            policy.AllowMissingOrigin,
            policy.ClientAdmission == DesktopClientAdmission.Multiple,
            UseClientCookies: true,
            origins);
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    private static void ValidateOptions(DesktopHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Port, ushort.MaxValue);
        if (options.ConnectionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ConnectionTimeout must be positive.");
        }
        if (options.NetworkExposure == DesktopNetworkExposure.AllInterfaces &&
            ReferenceEquals(options.Security, DesktopSecurityPolicy.Default))
        {
            throw new ArgumentException(
                "Non-loopback binding requires an explicit security policy.",
                nameof(options));
        }
    }
}
