using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Runic.Desktop.Internal;

namespace Runic.Desktop;

/// <summary>Owns listener resources and a set of isolated presentation surfaces.</summary>
public sealed class DesktopHost : IAsyncDisposable
{
    private readonly DesktopHostOptions _options;
    private readonly PresentationHostCore _core;
    private readonly ConcurrentDictionary<Guid, DesktopSurface> _surfaces = new();
    private readonly ILogger? _logger;
    private int _disposed;

    private DesktopHost(DesktopHostOptions options)
    {
        ValidateOptions(options);
        _options = options;
        _core = CreateCore(options, options.Port);
        _logger = options.LoggerFactory?.CreateLogger(DesktopLog.Category);
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

    /// <summary>Inspects presentations available under this host's browser and embedded-host configuration.</summary>
    public DesktopAvailabilityResult GetAvailability()
    {
        var availability = DesktopPlatform.GetAvailability(_options.BrowserFolder, _options.Linux);
        if (_options.WindowHostFactory is null)
        {
            return availability;
        }

        var selectionDiagnostic = OperatingSystem.IsLinux() && _options.WindowHostFactory is ILinuxDesktopWindowHostFactory
            ? DesktopPlatform.GetLinuxSelectionDiagnostic(_options.Linux, _options.WindowHostFactory)
            : null;
        var customHostAvailable = selectionDiagnostic is null && _options.WindowHostFactory.IsSupported;
        var presentations = availability.Presentations
            .Where(static presentation => presentation.Browser != BrowserKind.Embedded)
            .Append(new DesktopPresentationAvailability(
                BrowserKind.Embedded,
                customHostAvailable,
                ExecutablePath: null,
                customHostAvailable
                    ? _options.WindowHostFactory.Capabilities
                    : DesktopWindowCapabilities.None,
                customHostAvailable
                    ? null
                    : selectionDiagnostic ?? new DesktopDiagnostic(
                        DesktopErrorCategory.Unavailable,
                        "custom-window-host-unavailable",
                        "The configured embedded-window host is unavailable.",
                        Retryable: false,
                        Remediation: "Install its platform prerequisites or select an installed browser.")))
            .ToArray();
        return availability with { Presentations = presentations };
    }

    /// <summary>Evaluates one requested presentation and its explicit fallback policy without starting a browser or WebView.</summary>
    /// <remarks>
    /// <see cref="DesktopPresentationPreflight.OptionDiagnostics"/> reports each window option and permission grant
    /// the preferred presentation or its fallback rejects or ignores.
    /// </remarks>
    public DesktopPresentationPreflight GetPresentationPreflight(DesktopWindowOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var configured = options ?? new DesktopWindowOptions();
        DesktopSurface.ValidateWindowOptions(configured);
        var availability = GetAvailability();
        var preferredBrowser = configured.PresentationPolicy == DesktopPresentationPolicy.EmbeddedThenBrowser
            ? BrowserKind.Embedded
            : configured.Browser;
        var preferred = FindAvailability(availability, preferredBrowser) ?? Unavailable(preferredBrowser);
        var optionDiagnostics = new List<DesktopDiagnostic>();
        DesktopWindowOptionValidation.AddPairChecks(configured, optionDiagnostics);
        if (preferredBrowser == BrowserKind.Embedded)
        {
            DesktopWindowOptionValidation.AddEmbeddedChecks(
                configured, _options.WindowHostFactory, _options.Linux.EmbeddedBackend, optionDiagnostics);
        }
        else if (DesktopWindowOptionValidation.IsLaunchable(preferredBrowser))
        {
            DesktopWindowOptionValidation.AddBrowserChecks(
                configured, ConcreteBrowser(preferredBrowser, preferred), isFallback: false, optionDiagnostics);
        }

        DesktopPresentationAvailability? fallback = null;
        if (configured.PresentationPolicy == DesktopPresentationPolicy.EmbeddedThenBrowser)
        {
            var fallbackBrowser = configured.Browser == BrowserKind.Embedded ? BrowserKind.Any : configured.Browser;
            fallback = FindAvailability(availability, fallbackBrowser) ?? Unavailable(fallbackBrowser);
            if (DesktopWindowOptionValidation.IsLaunchable(fallbackBrowser))
            {
                DesktopWindowOptionValidation.AddBrowserChecks(
                    configured, ConcreteBrowser(fallbackBrowser, fallback), isFallback: true, optionDiagnostics);
            }
        }

        return new DesktopPresentationPreflight(
            configured.Browser,
            configured.PresentationPolicy,
            preferred,
            fallback)
        {
            OptionDiagnostics = optionDiagnostics,
        };
    }

    /// <summary>
    /// Checks a window request before it opens: the presentation and its prerequisites, the window options, and the
    /// permission grants. Each diagnostic is also logged through <see cref="DesktopHostOptions.LoggerFactory"/>.
    /// </summary>
    /// <remarks>
    /// Call it at startup and use <see cref="DesktopValidationResult.ThrowIfInvalid"/> to stop before any window
    /// opens. Opening the window repeats the checks it depends on; validation does not start a browser or WebView.
    /// </remarks>
    public DesktopValidationResult Validate(DesktopWindowOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        List<DesktopDiagnostic> diagnostics = [];
        try
        {
            var preflight = GetPresentationPreflight(options);
            if (preflight.Diagnostic is { } unavailable)
            {
                diagnostics.Add(unavailable);
            }
            diagnostics.AddRange(preflight.OptionDiagnostics);
        }
        catch (ArgumentException error)
        {
            diagnostics.Add(DesktopWindowOptionValidation.Invalid(error.Message));
        }

        if (_logger is { } logger)
        {
            foreach (var diagnostic in diagnostics)
            {
                DesktopLog.Diagnostic(logger, diagnostic);
            }
        }
        return new DesktopValidationResult(diagnostics);
    }

    private static BrowserKind? ConcreteBrowser(BrowserKind requested, DesktopPresentationAvailability availability) =>
        requested is BrowserKind.Any or BrowserKind.ChromiumBased
            ? availability.IsAvailable ? availability.Browser : null
            : requested;

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
            ? new WebUiEmbeddedHostFactory(_options.Linux.EmbeddedBackend)
            : new DesktopWindowHostFactoryAdapter(_options.WindowHostFactory);
        var runtime = new PresentationSurfaceRuntimeOptions(
            rootFolder,
            NormalizeOptionalPath(_options.BrowserFolder),
            embeddedFactory,
            _options.WaitForConnection,
            _options.ConnectionTimeout,
            _options.DiagnosticSink,
            _logger);
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
                        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                            context.Request.Headers.ToDictionary(
                                static header => header.Key,
                                static header => header.Value.ToString(),
                                StringComparer.OrdinalIgnoreCase)),
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

    // Reports a configuration limitation found while opening, to the sink and the Desktop logger.
    internal void ReportConfiguration(DesktopDiagnostic diagnostic)
    {
        Report(diagnostic);
        if (_logger is { } logger) DesktopLog.Diagnostic(logger, diagnostic);
        else System.Diagnostics.Trace.TraceWarning($"{diagnostic.Code}: {diagnostic.Message}");
    }

    internal DesktopPresentationAvailability? FindAvailability(BrowserKind browser)
    {
        return FindAvailability(GetAvailability(), browser);
    }

    private static DesktopPresentationAvailability? FindAvailability(
        DesktopAvailabilityResult availability,
        BrowserKind browser)
    {
        var presentations = availability.Presentations;
        if (browser == BrowserKind.Any)
        {
            return presentations.FirstOrDefault(static candidate =>
                candidate.Browser != BrowserKind.Embedded && candidate.IsAvailable);
        }
        if (browser == BrowserKind.ChromiumBased)
        {
            return presentations.FirstOrDefault(static candidate =>
                candidate.IsAvailable && candidate.Browser is BrowserKind.Chrome or BrowserKind.Edge or
                    BrowserKind.Chromium or BrowserKind.Brave or BrowserKind.Vivaldi or BrowserKind.Epic or
                    BrowserKind.Yandex);
        }
        return presentations.FirstOrDefault(candidate => candidate.Browser == browser);
    }

    internal static DesktopPresentationAvailability Unavailable(BrowserKind browser) => new(
        browser,
        IsAvailable: false,
        ExecutablePath: null,
        DesktopWindowCapabilities.None,
        !DesktopWindowOptionValidation.IsLaunchable(browser)
            ? DesktopWindowOptionValidation.BrowserUnsupported(browser)
            : new DesktopDiagnostic(
            DesktopErrorCategory.Unavailable,
            "presentation-unavailable",
            $"No supported presentation was discovered for {browser}.",
            Retryable: false,
            Remediation: "Install a supported browser or WebView runtime and run DesktopPlatform.GetAvailability()."));

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
        ArgumentNullException.ThrowIfNull(options.Linux);
        if (options.Linux.EmbeddedBackend is { } backend && !Enum.IsDefined(backend))
            throw new ArgumentOutOfRangeException(nameof(options), "Linux.EmbeddedBackend contains an undefined value; select Gtk3WebKit41 or Gtk4WebKit6.");
        if (options.WindowHostFactory is ILinuxDesktopWindowHostFactory linux && options.Linux.EmbeddedBackend != linux.Backend)
            throw new ArgumentException("Linux.EmbeddedBackend must match the configured Linux window host factory.", nameof(options));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Port, ushort.MaxValue);
        if (options.ConnectionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ConnectionTimeout must be positive.");
        }
        // The application must choose a policy; which instance it chose is irrelevant.
        if (options.NetworkExposure == DesktopNetworkExposure.AllInterfaces && !options.HasExplicitSecurity)
        {
            throw new ArgumentException(
                "Non-loopback binding requires an explicit security policy.",
                nameof(options));
        }
    }
}
