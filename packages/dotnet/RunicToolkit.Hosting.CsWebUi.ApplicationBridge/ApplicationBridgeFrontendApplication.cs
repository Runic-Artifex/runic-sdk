using CsWebUi;
using RunicToolkit.ApplicationBridge;
using RunicToolkit.Hosting.CsWebUi;
using RunicToolkit.Hosting.WebUi;

namespace RunicToolkit.Hosting.CsWebUi.ApplicationBridge;

/// <summary>Configures one generated-contract Application Bridge frontend.</summary>
public sealed record ApplicationBridgeFrontendApplicationOptions
{
    /// <summary>Creates complete native Application Bridge options.</summary>
    public ApplicationBridgeFrontendApplicationOptions(
        IFrontendAssetProvider assets,
        CsWebUiAdapterOptions adapter,
        BrowserHostOptions browserHost,
        BrowserWindowOptions browserWindow,
        Func<ApplicationBridgeSession> createSession,
        CsWebUiApplicationBridgeOptions? bridge = null,
        TimeSpan? sessionCloseTimeout = null,
        TimeSpan? windowCloseTimeout = null)
    {
        Assets = assets ?? throw new ArgumentNullException(nameof(assets));
        Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        BrowserHost = browserHost ?? throw new ArgumentNullException(nameof(browserHost));
        BrowserWindow = browserWindow ?? throw new ArgumentNullException(nameof(browserWindow));
        CreateSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
        Bridge = bridge ?? new();
        Bridge.Validate();
        SessionCloseTimeout = sessionCloseTimeout ?? TimeSpan.FromSeconds(5);
        WindowCloseTimeout = windowCloseTimeout ?? TimeSpan.FromSeconds(5);
        _ = new WebUiModeOptions(BrowserHost, BrowserWindow, SessionCloseTimeout, WindowCloseTimeout);
    }

    /// <summary>Gets immutable frontend assets.</summary>
    public IFrontendAssetProvider Assets { get; }

    /// <summary>Gets native adapter policy.</summary>
    public CsWebUiAdapterOptions Adapter { get; }

    /// <summary>Gets browser-host identity.</summary>
    public BrowserHostOptions BrowserHost { get; }

    /// <summary>Gets native-window configuration.</summary>
    public BrowserWindowOptions BrowserWindow { get; }

    /// <summary>Gets the factory for an isolated logical session.</summary>
    public Func<ApplicationBridgeSession> CreateSession { get; }

    /// <summary>Gets the fixed bridge-channel policy.</summary>
    public CsWebUiApplicationBridgeOptions Bridge { get; }

    /// <summary>Gets the root-session close bound.</summary>
    public TimeSpan SessionCloseTimeout { get; }

    /// <summary>Gets the native-window close bound.</summary>
    public TimeSpan WindowCloseTimeout { get; }
}

/// <summary>Owns the root factory registered for one native frontend.</summary>
public sealed class ApplicationBridgeFrontendApplication : IAsyncDisposable
{
    private ApplicationBridgeFrontendRoot? _root;

    internal ApplicationBridgeFrontendApplication(ApplicationBridgeFrontendRoot root) => _root = root;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        ApplicationBridgeFrontendRoot? root = Interlocked.Exchange(ref _root, null);
        if (root is not null) await root.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class ApplicationBridgeFrontendRoot : IRootSessionFactory, IAsyncDisposable
{
    private readonly Func<ApplicationBridgeSession> _createSession;
    private readonly CsWebUiApplicationBridgeOptions _options;
    private WebUiWindow? _window;
    private int _disposed;

    internal ApplicationBridgeFrontendRoot(
        Func<ApplicationBridgeSession> createSession,
        CsWebUiApplicationBridgeOptions options)
    {
        _createSession = createSession;
        _options = options;
    }

    internal void AttachWindow(WebUiWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (Interlocked.CompareExchange(ref _window, window, null) is not null)
        {
            throw new InvalidOperationException("An Application Bridge frontend supports one native root window.");
        }
    }

    public ValueTask<IRootSession> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        WebUiWindow window = _window ?? throw new InvalidOperationException(
            "CS-WebUI must create the native window before opening the Application Bridge root session.");
        ApplicationBridgeSession session = _createSession() ?? throw new InvalidOperationException(
            "The Application Bridge session factory returned null.");
        try
        {
            return ValueTask.FromResult<IRootSession>(
                new RootSession(CsWebUiApplicationBridge.Attach(window, session, _options)));
        }
        catch
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _window = null;
        return ValueTask.CompletedTask;
    }

    private sealed class RootSession(CsWebUiApplicationBridge bridge) : IRootSession
    {
        private CsWebUiApplicationBridge? _bridge = bridge;

        public ValueTask ActivateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DeactivateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            CsWebUiApplicationBridge? owned = Interlocked.Exchange(ref _bridge, null);
            if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
        }
    }
}
