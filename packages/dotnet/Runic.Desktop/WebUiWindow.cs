using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Runic.Desktop.Internal;
using Microsoft.Extensions.Logging;

namespace Runic.Desktop;

/// <summary>Owns one Runic Desktop server and its browser connections.</summary>
internal sealed class WebUiWindow : IDisposable, IAsyncDisposable
{
    private const string BridgePath = "/webui.js";
    private const string DesktopBootstrapPath = "/runic-desktop.js";
    // Released names are remembered in groups: the names released in one run, with no binding registered in between,
    // such as all routes of a content tree a navigation released. A group is forgotten whole, oldest first, once more
    // than ReleasedGroupMemory groups or ReleasedNameMemory names are remembered; the newest group is always kept.
    internal const int ReleasedGroupMemory = 256;
    internal const int ReleasedNameMemory = 16_384;
    // The reply to a late call of a recently released capability (Views protocol 3).
    internal const string ReleasedCapabilityReply =
        "{\"ok\":false,\"state\":null,\"error\":{\"kind\":\"released\",\"message\":\"This View was released.\"}}";
    internal const string WebSocketPath = "/_webui_ws_connect";
    // blobs/1: the window's blobs (GET and HEAD of /_runic/blobs/{token}) and uploads (POST /_runic/upload), which a
    // Bridge transport serves (SetBlobHandler).
    internal const string BlobsPath = "/_runic/blobs";
    internal const string UploadPath = "/_runic/upload";
    // Trusted component mode (region-component/1): the component build of the surface's content (the `component` target
    // of @runic-artifex/vite-plugin-runic writes it to _runic_component/), which the embedding host page loads into its
    // own document from another origin (ServeComponentAsync).
    internal const string ComponentPath = "/_runic_component";
    private const string ContentSecurityPolicyHeader = "Content-Security-Policy";
    private const string AuthCookieName = "webui_auth";
    private const string DocumentBootstrapProperty = "globalThis[Symbol.for(\"runic-desktop.bootstrap\")]";
    private const string NoCache = "no-cache, no-store, must-revalidate, private, max-age=0";
    private const string DefaultIcon = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"500\" viewBox=\"0 0 375 375\" height=\"500\"><path fill=\"#2a6699\" d=\"M22 22h330v330H22z\"/></svg>";
    private static readonly AsyncLocal<WebUiWindow?> CallbackWindow = new();

    private readonly ConcurrentDictionary<string, BindingRegistration> _bindings = new(StringComparer.Ordinal);
    // Bindings released lately, by name, and the group each was last released in. Only a call that races a release
    // matters, so the memory is bounded; a late call to a forgotten name is reported as not admitted.
    private readonly object _releasedGate = new();
    private readonly Dictionary<string, ReleasedGroup> _releasedBindings = new(StringComparer.Ordinal);
    private readonly Queue<ReleasedGroup> _releasedGroups = new();
    private ReleasedGroup? _openReleasedGroup;
    // Names recorded by the remembered groups, including names released again in a newer group.
    private int _releasedNameCount;
    // Connection observers are not bindings: the page never learns about them, so
    // observing connections does not turn on the bridge's all-events mode.
    private readonly object _connectionObserverGate = new();
    private Action<WebUiEvent>[] _connectionObservers = [];
    private readonly ConcurrentDictionary<Guid, WebUiSession> _sessions = new();
    // The sessions whose Connected event was dispatched and whose Disconnected event was not.
    private readonly ConcurrentDictionary<Guid, byte> _connectedSessions = new();
    private readonly ConcurrentDictionary<string, nuint> _clients = new(StringComparer.Ordinal);
    // The installed browser this window launched, and the client that opened its launch code (SEC-01).
    private readonly BrowserLaunchBinding _launchBinding = new();
    // runic-host/1 region mode: the frame of a host page that shows this surface, and the page it admits.
    private readonly FrameLaunchBinding _frameBinding = new();
    // runic-host/1 region-component/1: the host page that loads this surface's component, and the token it admits.
    private readonly ComponentBinding _componentBinding = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _presentationGate = new(1, 1);
    private readonly TaskCompletionSource _disposeCompletion = NewCompletionSource();
    private readonly object _connectionGate = new();
    private CancellationTokenSource _shutdown = new();
    private readonly uint _token = CreateToken();

    private PresentationHostCore? _host;
    private PresentationSurfaceRegistration? _surface;
    private readonly string _surfacePathBase;
    private readonly bool _ownsHost;
    private readonly PresentationSecurityPolicy? _securityPolicy;
    private readonly PresentationSurfaceRuntimeOptions? _runtimeOptions;
    private readonly string _sessionCredential;
    private string? _frameAncestorsPolicy;
    private Process? _browserProcess;
    private Task _browserOutputClosed = Task.CompletedTask;
    private IWebUiEmbeddedHost? _embeddedHost;
    private Func<CancellationToken, ValueTask<bool>>? _confirmClose;
    private WindowCloseController? _closeController;

    private TaskCompletionSource _browserConnected = NewCompletionSource();
    // How far the current presentation got before authentication, for timeout reports.
    private int _connectionProgress;
    // Requests the current presentation received that did not prove they came from its page (IsFromPage), and the
    // first of them.
    private int _otherRequests;
    private string? _firstOtherRequest;
    // The current presentation's connection deadline: its first request and its WebView's startup progress.
    private ConnectionDeadline? _connectionDeadline;
    // What the server saw of the current presentation's handshake, for timeout reports.
    private BridgeHandshakeLog _handshakeLog = new();
    // Null when the content serves no local files.
    private string? _rootFolder;
    private string? _profileName;
    private string? _profilePath;
    private string? _generatedProfilePath;
    private string? _proxyServer;
    private IReadOnlyList<string> _browserArgumentList = [];
    private string? _browserArguments;
    private DesktopPermissionGrant _allowedPermissions;
    private DesktopWebContentPolicy _webContent = DesktopWebContentPolicy.Default;
    private string? _embeddedHtml;
    private string? _entryFile;
    private Uri? _externalUrl;
    // runic-host/1: the key of the host session's ephemeral data store when this surface's window shows a hosted
    // application's page (DesktopHost.CreateHostedSurfaceAsync).
    private string? _hostedDataStore;
    private WebUiFileHandler? _fileHandler;
    private Func<PresentationExchange, string, CancellationToken, ValueTask<WebUiContent?>>? _requestHandler;
    // DesktopSurfaceOptions.Resources: asked for what the content does not serve, before 404 (#313).
    private Func<PresentationExchange, string, CancellationToken, ValueTask<WebUiContent?>>? _resourceHandler;
    // blobs/1: serves an admitted blob request (with its token) or upload (with null).
    private Func<PresentationExchange, string?, Task>? _blobHandler;
    // region-component/1: the canonical origin of the host page that may load the component build, or null.
    private string? _componentEmbedder;
    private bool _allowIndexFallback;
    private bool _isPublic;
    private bool _profileConfigured;
    private bool _kiosk;
    private string? _monitor;
    private Runic.Platform.DesktopLayoutDirection _layoutDirection;
    private string? _language;
    private MacOSWindowOptions _macOS = new();
    // The next embedded window's owner (DesktopWindowOptions.Owner, resolved to its native handle) and modality.
    private nint _ownerHandle;
    private DesktopWindowModality _modality;
    private bool _hidden;
    private bool _resizable = true;
    private bool _frameless;
    private bool _transparent;
    // The served bootstrap scripts, as UTF-8, for the inputs they were built from.
    private BootstrapScript? _bridgeScript;
    private BootstrapScript? _desktopBootstrapScript;
    private bool _centered;
    private bool? _highContrast;
    private uint? _width;
    private uint? _height;
    private uint? _minimumWidth;
    private uint? _minimumHeight;
    private int? _x;
    private int? _y;
    private DesktopWindowPlacement? _placement;
    private string? _iconFile;
    private WebUiBrowser _currentBrowser;
    private int _requestedPort;
    private int _activeConnections;
    // The side panel's page connection slot (sdk#864) and its one connection, beside the page's (_activeConnections).
    private readonly PanelConnectionSlot _panelSlot = new();
    private int _activePanelConnections;
    private long _nextClientId;
    private long _nextConnectionId;
    private long _nextEventNumber;
    private long _nextRegistrationId;
    private int _disposed;

    /// <summary>Creates a managed window using the current application default root folder.</summary>
    public WebUiWindow() : this(host: null, pathBase: string.Empty, securityPolicy: null, runtimeOptions: null)
    {
    }

    internal WebUiWindow(
        PresentationHostCore? host,
        string pathBase,
        PresentationSecurityPolicy? securityPolicy = null,
        PresentationSurfaceRuntimeOptions? runtimeOptions = null)
    {
        _host = host;
        _surfacePathBase = pathBase;
        _ownsHost = host is null;
        _securityPolicy = securityPolicy;
        _runtimeOptions = runtimeOptions;
        _sessionCredential = securityPolicy?.RequireSessionCredential == true
            ? Convert.ToBase64String(OsRandom.GetBytes(32))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_')
            : string.Empty;
        _rootFolder = runtimeOptions is null ? WebUiApplication.GetDefaultRootFolder() : runtimeOptions.RootFolder;
    }

    /// <summary>Gets the local server URL after the window has started.</summary>
    public Uri? Url { get; private set; }

    /// <summary>Gets the configured or bound HTTP port.</summary>
    public nuint Port => checked((nuint)(Url?.Port ?? _requestedPort));

    /// <summary>Gets whether the server accepts connections on non-loopback interfaces.</summary>
    public bool IsPublic => _isPublic;

    /// <summary>Gets the number of sessions between their Connected and Disconnected events.</summary>
    internal int ConnectedSessionCount => _connectedSessions.Count;

    /// <summary>Gets whether this window currently owns a browser process or authenticated client.</summary>
    public bool IsShown =>
        (_browserProcess is { HasExited: false }) || (_embeddedHost?.IsOpen ?? false)
        || _sessions.Values.Any(static session => session.IsAuthenticated);

    /// <summary>Gets the operating-system identifier of the owned browser process.</summary>
    public nuint BrowserProcessId => _browserProcess is { HasExited: false } process
        ? checked((nuint)process.Id)
        : 0;

    internal bool IsEmbeddedWindowOpen => _embeddedHost?.IsOpen == true;

    // The regions attached in this window's page (host-protocol.md, Regions): the native host routes their frames'
    // drops, permission requests and menu keys to their guests. Kept across the window's hosts.
    internal RegionFrames RegionFrames { get; } = new();

    internal bool SupportsRegionFrames => _embeddedHost?.SupportsRegionFrames == true;

    /// <summary>Gets the native embedded-window handle, or zero outside WebView mode.</summary>
    public nint NativeWindowHandle => _embeddedHost?.NativeHandle ?? 0;
    internal DesktopWindowCapabilities WindowCapabilities => _embeddedHost is { } host
        ? SharedProfile(host) is null ? host.Capabilities : host.Capabilities & ~DesktopWindowCapabilities.PerWindowColorScheme
        : DesktopWindowCapabilities.None;

    // The ProfilePath the current window opened with, when the application configured one: windows that share it share
    // WebView2's preferred color scheme. A generated profile belongs to one window.
    private string? _configuredProfilePath;

    private string? SharedProfile(IWebUiEmbeddedHost host) =>
        host is WindowsWebView2Host or DesktopWindowHostAdapter ? _configuredProfilePath : null;

    // What the current window's color scheme belongs to: null for the window alone, else a key that the windows sharing
    // it have in common (DesktopHost tracks the scheme of each).
    internal string? EmbeddedColorSchemeScope => _embeddedHost is not { } host
        || (WindowCapabilities & DesktopWindowCapabilities.PerWindowColorScheme) != 0
        ? null
        : SharedProfile(host) is { } profile ? "profile:" + profile.ToUpperInvariant() : "process";

    internal bool SupportsNativeDispatch => _embeddedHost?.SupportsNativeDispatch == true;
    internal bool CheckNativeAccess() => _embeddedHost?.CheckNativeAccess() == true;
    internal ValueTask InvokeNativeAsync(Action action, CancellationToken cancellationToken) =>
        (_embeddedHost ?? throw new InvalidOperationException("No embedded native owner is available."))
            .InvokeNativeAsync(action, cancellationToken);

    /// <summary>Gets the selected browser for this window, or <see cref="WebUiBrowser.NoBrowser"/>.</summary>
    public WebUiBrowser CurrentBrowser => _currentBrowser;

    /// <summary>Gets the recommended installed browser.</summary>
    public WebUiBrowser BestBrowser => _currentBrowser != WebUiBrowser.NoBrowser
        ? _currentBrowser
        : WebUiBrowserDiscovery.Find(WebUiBrowser.AnyBrowser, BrowserFolder)?.Browser
            ?? WebUiBrowser.AnyBrowser;

    /// <summary>Registers a synchronous JavaScript binding.</summary>
    public WebUiBinding Bind(string element, Action<WebUiEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Bind(element, webUiEvent =>
        {
            handler(webUiEvent);
            return WebUiResult.None;
        });
    }

    /// <summary>Registers a synchronous JavaScript binding.</summary>
    public WebUiBinding Bind(string element, Func<WebUiEvent, WebUiResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return BindAsync(element, (webUiEvent, _) => ValueTask.FromResult(handler(webUiEvent)));
    }

    /// <summary>Registers an asynchronous JavaScript binding.</summary>
    /// <remarks>
    /// A session serializes only a call's admission and the handler's synchronous prefix; the rest can
    /// overlap other calls and events, and replies are sent as calls complete. Events are awaited one at
    /// a time, and Disconnected follows every call the session accepted. See the Runic.Desktop README.
    /// </remarks>
    public WebUiBinding BindAsync(
        string element,
        Func<WebUiEvent, CancellationToken, ValueTask<WebUiResult>> handler)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(handler);

        var registrationId = Interlocked.Increment(ref _nextRegistrationId);
        BindingRegistration registration;
        var added = false;
        while (true)
        {
            if (_bindings.TryGetValue(element, out var existing))
            {
                registration = new BindingRegistration(registrationId, existing.Order, handler);
                if (_bindings.TryUpdate(element, registration, existing))
                {
                    break;
                }
            }
            else
            {
                registration = new BindingRegistration(registrationId, registrationId, handler);
                if (_bindings.TryAdd(element, registration))
                {
                    added = true;
                    break;
                }
            }
        }

        if (added)
        {
            BroadcastBinding(element);
        }

        return new WebUiBinding(this, registration.Id, element);
    }

    /// <summary>Starts the managed HTTP and WebSocket server without opening a browser.</summary>
    public Uri StartServer(string content) => StartServerAsync(content).GetAwaiter().GetResult();

    /// <summary>Starts the managed HTTP and WebSocket server without opening a browser.</summary>
    public Task<Uri> StartServerAsync(string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return StartServerCoreAsync(() => ConfigureContent(content), cancellationToken);
    }

    /// <summary>Starts the managed server for a Desktop surface's typed content.</summary>
    internal Task<Uri> StartServerAsync(DesktopContent content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return StartServerCoreAsync(() => ConfigureContent(content), cancellationToken);
    }

    private async Task<Uri> StartServerCoreAsync(Action configureContent, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_surface is not null)
            {
                return Url ?? throw new InvalidOperationException("The running managed window has no server URL.");
            }

            if (_shutdown.IsCancellationRequested)
            {
                _shutdown.Dispose();
                _shutdown = new CancellationTokenSource();
            }

            configureContent();
            var host = _host ?? new PresentationHostCore(new PresentationHostCoreOptions(
                _requestedPort,
                _isPublic ? PresentationNetworkExposure.AllInterfaces : PresentationNetworkExposure.Loopback));
            _host = host;
            _surface = await host.AttachSurfaceAsync(
                _surfacePathBase,
                HandleRequestAsync,
                CloseFromHostAsync,
                cancellationToken).ConfigureAwait(false);
            if (_requestedPort == 0)
            {
                _requestedPort = host.Port;
            }
            Url = _surface.Url;
            if (_runtimeOptions is null)
            {
                WebUiApplication.Started(this);
            }
            return Url;
        }
        catch
        {
            if (_surface is not null)
            {
                await _surface.DisposeAsync().ConfigureAwait(false);
                _surface = null;
            }
            if (_ownsHost && _host is not null)
            {
                await _host.DisposeAsync().ConfigureAwait(false);
                _host = null;
            }

            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Starts the managed server and opens it in the recommended installed browser.</summary>
    public Task<Uri> ShowAsync(string content, CancellationToken cancellationToken = default)
        => ShowInBrowserAsync(content, WebUiBrowser.AnyBrowser, cancellationToken);

    /// <summary>Starts the managed server and opens it in a selected browser.</summary>
    public Task<Uri> ShowInBrowserAsync(
        string content,
        WebUiBrowser browser,
        CancellationToken cancellationToken = default)
        => browser == WebUiBrowser.WebView
            ? ShowWebViewAsync(content, cancellationToken)
            : ShowInBrowserCoreAsync(content, browser, cancellationToken);

    internal Task<Uri> OpenPresentationAsync(WebUiBrowser browser, CancellationToken cancellationToken)
    {
        if (browser != WebUiBrowser.WebView)
        {
            return ShowInBrowserCoreAsync(content: null, browser, cancellationToken);
        }
        return OperatingSystem.IsMacOS() && MacOsWkWebViewHost.IsMainThread
            ? Task.FromResult(ShowWebViewOnMacMainThread(content: null, cancellationToken))
            : ShowWebViewCoreAsync(content: null, cancellationToken);
    }

    private async Task<Uri> ShowInBrowserCoreAsync(
        string? content,
        WebUiBrowser browser,
        CancellationToken cancellationToken)
    {
        var url = content is null
            ? Url ?? throw new InvalidOperationException("The surface must be started before opening a presentation.")
            : await StartServerAsync(content, cancellationToken).ConfigureAwait(false);
        if (browser == WebUiBrowser.NoBrowser)
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (content is not null)
                {
                    ConfigureContent(content);
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
            return url;
        }

        Task connection;
        Uri browserUrl;
        Process? launched = null;
        var launchedBrowser = WebUiBrowser.NoBrowser;
        var navigateExisting = false;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (content is not null)
            {
                ConfigureContent(content);
            }
            browserUrl = GetBrowserUrl(url);
            if (_embeddedHost is not null)
            {
                throw new InvalidOperationException("This window is already hosted by an embedded WebView.");
            }
            if (_browserProcess is { HasExited: true })
            {
                await StopBrowserAsync().ConfigureAwait(false);
            }
            if (_browserProcess is { HasExited: false })
            {
                if (browser is not WebUiBrowser.AnyBrowser and not WebUiBrowser.ChromiumBased && browser != _currentBrowser)
                {
                    throw new InvalidOperationException($"This window is already hosted by {_currentBrowser}.");
                }

                navigateExisting = true;
                connection = Task.CompletedTask;
            }
            else
            {
                var requestedBrowser = browser == WebUiBrowser.AnyBrowser && _currentBrowser != WebUiBrowser.NoBrowser
                    ? _currentBrowser
                    : browser;
                launched = LaunchBrowser(requestedBrowser, browserUrl, out connection);
                launchedBrowser = _currentBrowser;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (navigateExisting)
        {
            await NavigateAsync(browserUrl.AbsoluteUri, cancellationToken).ConfigureAwait(false);
        }
        else if (WaitForConnection && _externalUrl is null)
        {
            await WaitForConnectionAsync(
                    connection,
                    launched,
                    surfaceHosted: content is null,
                    cancellationToken,
                    (stalled, stalledConnection) => RelaunchStalledBrowserAsync(
                        stalled, stalledConnection, launchedBrowser, browserUrl, cancellationToken))
                .ConfigureAwait(false);
        }

        return url;
    }

    // Called with the lifecycle gate held. Starts the browser with the current
    // profile, or a new generated one, and returns the connection to wait for.
    private Process LaunchBrowser(WebUiBrowser requestedBrowser, Uri browserUrl, out Task connection)
    {
        var installation = WebUiBrowserDiscovery.Find(requestedBrowser, BrowserFolder)
            ?? throw new InvalidOperationException($"No supported installation was found for {requestedBrowser}.");
        var profilePath = GetOrCreateProfilePath(installation.Browser);
        var options = new WebUiBrowserLaunchOptions(
            _profileName,
            profilePath,
            _proxyServer,
            _browserArgumentList,
            _kiosk,
            _hidden,
            _width,
            _height,
            _x,
            _y,
            _allowedPermissions);
        connection = BeginConnection();
        // The command line is readable by other users of a Linux or macOS machine: give the browser a one-time
        // code that binds it to the surface instead of the surface's address.
        var launchUrl = BindsBrowserLaunch
            ? new Uri(Url!, $"{BrowserLaunchBinding.PathSegment.TrimStart('/')}/{_launchBinding.Begin(browserUrl)}")
            : browserUrl;
        Process process;
        Task outputClosed;
        try
        {
            process = WebUiBrowserHost.Start(installation, launchUrl, options, out outputClosed);
        }
        catch
        {
            if (_generatedProfilePath is { } failedProfile)
            {
                _generatedProfilePath = null;
                _ = TryDeleteGeneratedProfile(failedProfile);
            }
            throw;
        }
        _browserProcess = process;
        _browserOutputClosed = outputClosed;
        _currentBrowser = installation.Browser;
        _ = MonitorBrowserAsync(process);
        return process;
    }

    private enum RelaunchOutcome
    {
        // The stalled browser was stopped and a new one launched.
        Relaunched,
        // A request or authentication arrived meanwhile; keep waiting on the same launch.
        KeepWaiting,
        // The stalled browser exited by itself before it could be relaunched.
        Exited,
        // The presentation was closed, disposed or replaced meanwhile.
        Closed,
    }

    private readonly record struct RelaunchResult(RelaunchOutcome Outcome, Task? Connection = null, Process? Process = null);

    // A browser that is running but has not requested anything after a whole
    // connection timeout is stuck in its own startup (#35); its page was never
    // asked for, so starting it again is safe. Under the lifecycle gate, confirm
    // the stall still holds, stop the browser, which deletes a generated profile,
    // and launch it once more with a fresh one.
    private async Task<RelaunchResult> RelaunchStalledBrowserAsync(
        Process stalled,
        Task stalledConnection,
        WebUiBrowser browser,
        Uri browserUrl,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || Url is null)
            {
                return new(RelaunchOutcome.Closed);
            }
            if (!ReferenceEquals(_browserProcess, stalled))
            {
                // Without a current browser, it exited and its monitor already stopped it.
                return new(_browserProcess is null ? RelaunchOutcome.Exited : RelaunchOutcome.Closed);
            }
            if (HasExited(stalled))
            {
                return new(RelaunchOutcome.Exited);
            }
            if (stalledConnection.IsCompleted
                || (ConnectionProgress)Volatile.Read(ref _connectionProgress) != ConnectionProgress.NoRequest)
            {
                return new(RelaunchOutcome.KeepWaiting);
            }
            try
            {
                await StopBrowserAsync().ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    "The browser stalled before its first request and did not stop when it was closed for a relaunch.",
                    exception);
            }
            var process = LaunchBrowser(browser, browserUrl, out var connection);
            return new(RelaunchOutcome.Relaunched, connection, process);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    /// <summary>Starts the managed server and opens it in the platform embedded WebView.</summary>
    public Task<Uri> ShowWebViewAsync(string content, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsMacOS() && MacOsWkWebViewHost.IsMainThread)
        {
            return Task.FromResult(ShowWebViewOnMacMainThread(content, cancellationToken));
        }
        return ShowWebViewCoreAsync(content, cancellationToken);
    }

    /// <summary>Starts the managed server and opens it in the platform embedded WebView.</summary>
    public Uri ShowWebView(string content) => ShowWebViewAsync(content).GetAwaiter().GetResult();

    /// <summary>Attempts to show content in the platform embedded WebView.</summary>
    public bool TryShowWebView(string content)
    {
        try
        {
            ShowWebView(content);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or TimeoutException
                or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private async Task<Uri> ShowWebViewCoreAsync(string? content, CancellationToken cancellationToken)
    {
        var url = content is null
            ? Url ?? throw new InvalidOperationException("The surface must be started before opening a presentation.")
            : await StartServerAsync(content, cancellationToken).ConfigureAwait(false);
        Task connection;
        IWebUiEmbeddedHost host;
        var navigateExisting = false;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (content is not null)
            {
                ConfigureContent(content);
            }
            if (_browserProcess is { HasExited: false })
            {
                throw new InvalidOperationException("This window is already hosted by a browser process.");
            }
            if (_embeddedHost is { IsOpen: true } existingHost)
            {
                host = existingHost;
                navigateExisting = true;
                connection = Task.CompletedTask;
            }
            else
            {
                await ReleaseClosedEmbeddedHostAsync().ConfigureAwait(false);
                host = CreateEmbeddedHost();
                host.Closed += EmbeddedHostClosed;
                Volatile.Write(ref _embeddedWindowState, null);
                host.StateChanged += EmbeddedHostStateChanged;
                host.ActivityChanged += EmbeddedHostActivityChanged;
                host.MenuBarPlacementChanged += EmbeddedHostMenuBarPlacementChanged;
                host.FilesDropped += EmbeddedHostFilesDropped;
                _embeddedHost = host;
                AttachMenuCommands(host);
                _currentBrowser = WebUiBrowser.WebView;
                connection = BeginConnection();
                try
                {
                    ConfigureCloseConfirmation(host);
                    await host.ShowAsync(GetBrowserUrl(url), CreateEmbeddedHostOptions(), cancellationToken).ConfigureAwait(false);
                    RecordMenuBarPlacement(host);
                    PushMenuBar(host);
                    PushSpellCheckingLanguages(host);
                    PushColorScheme(host);
                }
                catch
                {
                    StopCloseConfirmation();
                    host.Closed -= EmbeddedHostClosed;
                    host.StateChanged -= EmbeddedHostStateChanged;
                    host.ActivityChanged -= EmbeddedHostActivityChanged;
                    host.MenuBarPlacementChanged -= EmbeddedHostMenuBarPlacementChanged;
                    host.FilesDropped -= EmbeddedHostFilesDropped;
                    _embeddedHost = null;
                    _currentBrowser = WebUiBrowser.NoBrowser;
                    await host.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (navigateExisting)
        {
            await host.NavigateAsync(GetBrowserUrl(url), cancellationToken).ConfigureAwait(false);
        }
        else if (WaitForConnection && _externalUrl is null)
        {
            await WaitForConnectionAsync(connection, browserProcess: null, surfaceHosted: content is null, cancellationToken)
                .ConfigureAwait(false);
        }
        return url;
    }

    private Uri ShowWebViewOnMacMainThread(string? content, CancellationToken cancellationToken)
    {
        var url = content is null
            ? Url ?? throw new InvalidOperationException("The surface must be started before opening a presentation.")
            : StartServerAsync(content, cancellationToken).GetAwaiter().GetResult();
        _lifecycleGate.Wait(cancellationToken);
        IWebUiEmbeddedHost host;
        Task connection;
        var navigateExisting = false;
        try
        {
            if (content is not null)
            {
                ConfigureContent(content);
            }
            if (_browserProcess is { HasExited: false })
            {
                throw new InvalidOperationException("This window is already hosted by a browser process.");
            }
            if (_embeddedHost is { IsOpen: true } existingHost)
            {
                host = existingHost;
                navigateExisting = true;
                connection = Task.CompletedTask;
            }
            else
            {
                ReleaseClosedEmbeddedHostAsync().GetAwaiter().GetResult();
                host = CreateEmbeddedHost();
                host.Closed += EmbeddedHostClosed;
                Volatile.Write(ref _embeddedWindowState, null);
                host.StateChanged += EmbeddedHostStateChanged;
                host.ActivityChanged += EmbeddedHostActivityChanged;
                host.MenuBarPlacementChanged += EmbeddedHostMenuBarPlacementChanged;
                host.FilesDropped += EmbeddedHostFilesDropped;
                _embeddedHost = host;
                AttachMenuCommands(host);
                _currentBrowser = WebUiBrowser.WebView;
                connection = BeginConnection();
                try
                {
                    ConfigureCloseConfirmation(host);
                    host.ShowAsync(GetBrowserUrl(url), CreateEmbeddedHostOptions(), cancellationToken)
                        .AsTask().GetAwaiter().GetResult();
                    RecordMenuBarPlacement(host);
                    PushMenuBar(host);
                    PushSpellCheckingLanguages(host);
                    PushColorScheme(host);
                }
                catch
                {
                    StopCloseConfirmation();
                    host.Closed -= EmbeddedHostClosed;
                    host.StateChanged -= EmbeddedHostStateChanged;
                    host.ActivityChanged -= EmbeddedHostActivityChanged;
                    host.MenuBarPlacementChanged -= EmbeddedHostMenuBarPlacementChanged;
                    host.FilesDropped -= EmbeddedHostFilesDropped;
                    _embeddedHost = null;
                    _currentBrowser = WebUiBrowser.NoBrowser;
                    host.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    throw;
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (navigateExisting)
        {
            host.NavigateAsync(GetBrowserUrl(url), cancellationToken).AsTask().GetAwaiter().GetResult();
        }
        else if (WaitForConnection && _externalUrl is null)
        {
            var deadline = Volatile.Read(ref _connectionDeadline)!;
            var started = deadline.Now;
            try
            {
                while (!connection.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ProcessEmbeddedHostEvents();
                    if (RemainingConnectionTime(deadline, started) is null)
                    {
                        throw new TimeoutException(DescribeConnectionTimeout(browserProcess: null));
                    }
                    Thread.Sleep(10);
                }
                connection.GetAwaiter().GetResult();
            }
            catch
            {
                ObserveAbandoned(connection);
                CloseFailedPresentationAsync(surfaceHosted: content is null).GetAwaiter().GetResult();
                throw;
            }
        }
        return url;
    }

    /// <summary>Starts the managed server and opens it in the recommended installed browser.</summary>
    public Uri Show(string content) => ShowAsync(content).GetAwaiter().GetResult();

    /// <summary>Starts the managed server and opens it in a selected browser.</summary>
    public Uri ShowInBrowser(string content, WebUiBrowser browser)
        => ShowInBrowserAsync(content, browser).GetAwaiter().GetResult();

    /// <summary>Attempts to show content in the recommended installed browser.</summary>
    public bool TryShow(string content)
    {
        try
        {
            Show(content);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Attempts to show content in a selected browser.</summary>
    public bool TryShowInBrowser(string content, WebUiBrowser browser)
    {
        try
        {
            ShowInBrowser(content, browser);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Sets the local root used for files and folder-mode index discovery.</summary>
    public void SetRootFolder(string path)
    {
        if (!TrySetRootFolder(path))
        {
            throw new DirectoryNotFoundException($"The Runic Desktop root folder does not exist: {path}");
        }
    }

    /// <summary>Attempts to set the local root used for files and folder-mode index discovery.</summary>
    public bool TrySetRootFolder(string path)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (!Directory.Exists(fullPath))
        {
            return false;
        }

        Volatile.Write(ref _rootFolder, fullPath);
        return true;
    }

    /// <summary>Sets the port used the next time this window starts. Zero selects an ephemeral port.</summary>
    public void SetPort(nuint port)
    {
        if (!TrySetPort(port))
        {
            throw new InvalidOperationException("The port is outside the TCP range or the managed window is already running.");
        }
    }

    /// <summary>Attempts to set the port used the next time this window starts.</summary>
    public bool TrySetPort(nuint port)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (port > ushort.MaxValue || _surface is not null)
        {
            return false;
        }

        _requestedPort = (int)port;
        return true;
    }

    /// <summary>Sets whether the next server start binds to all IPv4 interfaces or only loopback.</summary>
    public void SetPublic(bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_surface is not null)
        {
            throw new InvalidOperationException("Close the managed window before changing its public binding.");
        }

        _isPublic = enabled;
    }

    /// <summary>Sets a virtual content handler that runs before local-root fallback.</summary>
    public void SetFileHandler(WebUiFileHandler? handler)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _fileHandler, handler);
    }

    internal void SetRequestHandler(
        Func<PresentationExchange, string, CancellationToken, ValueTask<WebUiContent?>>? handler) =>
        Volatile.Write(ref _requestHandler, handler);

    internal void SetResourceHandler(
        Func<PresentationExchange, string, CancellationToken, ValueTask<WebUiContent?>>? handler) =>
        Volatile.Write(ref _resourceHandler, handler);

    // blobs/1: the handler of the window's blob and upload requests, or null to answer them 404.
    internal void SetBlobHandler(Func<PresentationExchange, string?, Task>? handler) =>
        Volatile.Write(ref _blobHandler, handler);

    // region-component/1: the origin of the host page that may load this surface's component build with CORS, or null to
    // refuse every component request. The component attach (TC3) sets it to the attach's embedder.
    internal void SetComponentEmbedder(Uri? embedder) =>
        Volatile.Write(ref _componentEmbedder, embedder is null ? null : PresentationSecurityPolicy.CanonicalOrigin(embedder));

    // Removes handler when it is still the window's blob handler.
    internal void ClearBlobHandler(Func<PresentationExchange, string?, Task> handler) =>
        Interlocked.CompareExchange(ref _blobHandler, null, handler);

    /// <summary>Sets the browser profile name and storage path used for this window.</summary>
    public void SetProfile(string name, string path)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(path);
        _profileConfigured = true;
        _profileName = name.Length == 0 ? null : name;
        _profilePath = path.Length == 0 ? null : Path.GetFullPath(path);
        if (_profilePath is not null)
        {
            Directory.CreateDirectory(_profilePath);
        }
    }

    /// <summary>Sets the proxy server passed to Chromium-based browser hosts.</summary>
    public void SetProxy(string proxyServer)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(proxyServer);
        _proxyServer = proxyServer.Length == 0 ? null : proxyServer;
    }

    /// <summary>Sets additional command-line parameters passed to the browser.</summary>
    public void SetBrowserArguments(string parameters)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(parameters);
        _browserArguments = parameters.Length == 0 ? null : parameters;
        _browserArgumentList = WebUiCommandLine.Split(parameters);
    }

    internal void SetAllowedPermissions(DesktopPermissionGrant permissions) => _allowedPermissions = permissions;

    internal void SetWebContent(DesktopWebContentPolicy policy) => _webContent = policy;

    /// <summary>Sets whether the browser is launched in kiosk mode.</summary>
    public void SetKiosk(bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _kiosk = enabled;
    }

    /// <summary>Sets whether the browser is launched without a visible window.</summary>
    public void SetHidden(bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _hidden = enabled;
        if (_embeddedHost is { IsOpen: true } host)
        {
            host.SetVisibleAsync(!enabled).AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Sets the initial outer browser dimensions in pixels.</summary>
    public void SetSize(uint width, uint height)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        _width = width;
        _height = height;
        if (_embeddedHost is { IsOpen: true } host)
        {
            host.SetSizeAsync(width, height).AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Sets the initial browser position in pixels.</summary>
    public void SetPosition(uint x, uint y) => SetPosition(checked((int)x), checked((int)y));

    internal void SetPosition(int x, int y)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _x = x;
        _y = y;
        _centered = false;
        if (_embeddedHost is { IsOpen: true } host)
        {
            host.SetPositionAsync(x, y).AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Sets whether an embedded window can be resized by the user.</summary>
    public void SetResizable(bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _resizable = enabled;
    }

    /// <summary>Sets whether an embedded window uses a borderless frame.</summary>
    public void SetFrameless(bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _frameless = enabled;
    }

    /// <summary>Sets whether an embedded window and page background may be transparent.</summary>
    public void SetTransparent(bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _transparent = enabled;
    }

    /// <summary>Overrides high-contrast reporting for this window.</summary>
    public void SetHighContrast(bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _highContrast = enabled;
    }

    /// <summary>Sets the minimum embedded-window dimensions in pixels.</summary>
    public void SetMinimumSize(uint width, uint height)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        _minimumWidth = width;
        _minimumHeight = height;
    }

    /// <summary>Centers the embedded window when it is first shown.</summary>
    public void Center()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _centered = true;
        _x = null;
        _y = null;
    }

    /// <summary>Sets the platform window icon file used by embedded hosts that support it.</summary>
    public void SetIconFile(string path)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The embedded-window icon file does not exist.", fullPath);
        }
        _iconFile = fullPath;
    }

    /// <summary>Activates the embedded platform window.</summary>
    public void Focus() => FocusAsync().GetAwaiter().GetResult();

    /// <summary>Activates the embedded platform window.</summary>
    public Task FocusAsync(CancellationToken cancellationToken = default)
        => GetEmbeddedHost().FocusAsync(cancellationToken).AsTask();

    // The window's accessibility API announces the message, except on WebView2 while the keyboard focus is in the page:
    // the focus is then in WebView2's browser process, and NVDA speaks a UI Automation notification only from the
    // focused object's process (I-184). The page's Bridge then announces it in a live region, which NVDA speaks from the
    // page's process. Without a connected Bridge the window announces it.
    internal async Task AnnounceAsync(string message, DesktopAnnouncementPriority priority, CancellationToken cancellationToken)
    {
        var host = GetEmbeddedHost();
        if (host is WindowsWebView2Host { IsWebContentFocused: true }
            && await AnnounceInPageAsync(message, priority, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        await host.AnnounceAsync(message, priority, cancellationToken).ConfigureAwait(false);
    }

    // Whether the page may announce DesktopWindow.AnnounceAsync, or the announcements of the WPF window around a
    // RunicWebView (I-203); WebUiSession prepares its live regions.
    internal bool AnnouncesInPage => _embeddedHost is WindowsWebView2Host or DesktopWindowHostAdapter { AnnouncesInPage: true };

    // Announces the message in the live region of every authenticated page (WebUiBridge's announce); false when no
    // page is connected.
    internal async Task<bool> AnnounceInPageAsync(string message, DesktopAnnouncementPriority priority, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var sessions = _sessions.Values.Where(static session => session.IsAuthenticated).ToArray();
        if (sessions.Length == 0) return false;
        var assertive = priority == DesktopAnnouncementPriority.Assertive;
        await Task.WhenAll(sessions.Select(session => session.AnnounceAsync(message, assertive, cancellationToken))).ConfigureAwait(false);
        return true;
    }

    // Whether the current embedded window opened as a kiosk, which stays full-screen.
    internal bool IsKiosk => _kiosk;

    internal ValueTask SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken)
        => GetEmbeddedHost().SetFullScreenAsync(fullScreen, cancellationToken);

    internal ValueTask MoveToMonitorAsync(string monitorId, CancellationToken cancellationToken)
        => GetEmbeddedHost().MoveToMonitorAsync(monitorId, cancellationToken);

    internal ValueTask SetEmbeddedVisibleAsync(bool visible, CancellationToken cancellationToken)
        => GetEmbeddedHost().SetVisibleAsync(visible, cancellationToken);

    /// <summary>Minimizes the embedded platform window.</summary>
    public void Minimize() => MinimizeAsync().GetAwaiter().GetResult();

    /// <summary>Minimizes the embedded platform window.</summary>
    public Task MinimizeAsync(CancellationToken cancellationToken = default)
        => GetEmbeddedHost().MinimizeAsync(cancellationToken).AsTask();

    /// <summary>Toggles maximized and restored embedded-window state.</summary>
    public void Maximize() => MaximizeAsync().GetAwaiter().GetResult();

    /// <summary>Toggles maximized and restored embedded-window state.</summary>
    public Task MaximizeAsync(CancellationToken cancellationToken = default)
        => GetEmbeddedHost().MaximizeAsync(cancellationToken).AsTask();

    /// <summary>Runs JavaScript in every authenticated browser without waiting for a response.</summary>
    public void RunJavaScript(string script)
        => RunJavaScriptAsync(script).GetAwaiter().GetResult();

    /// <summary>Runs JavaScript in every authenticated browser without waiting for a response.</summary>
    public Task RunJavaScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        return Task.WhenAll(_sessions.Values
            .Where(static session => session.IsAuthenticated)
            .Select(session => session.RunJavaScriptAsync(script, cancellationToken)));
    }

    // Pushes a Views message, as UTF-8 JSON, to every authenticated browser (WebUiSession.PushViewsMessageAsync).
    internal Task PushViewsMessageAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (message.IsEmpty) throw new ArgumentException("The message is empty.", nameof(message));
        return Task.WhenAll(_sessions.Values
            .Where(static session => session.IsAuthenticated)
            .Select(session => session.PushViewsMessageAsync(message.Span, cancellationToken)));
    }

    // Pushes a Views message to the authenticated browser of one connection (WebUiEvent.ConnectionId). False when that
    // connection is not open.
    internal Task<bool> PushViewsMessageAsync(nuint connectionId, ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (message.IsEmpty) throw new ArgumentException("The message is empty.", nameof(message));
        foreach (var session in _sessions.Values)
            if (session.ConnectionId == connectionId)
                return session.PushViewsMessageAsync(message.Span, cancellationToken);
        return Task.FromResult(false);
    }

    /// <summary>Runs JavaScript in the connected browser and returns its UTF-8 result.</summary>
    public string ExecuteJavaScript(string script, TimeSpan? timeout = null, int responseBufferSize = 4 * 1024)
        => ExecuteJavaScriptAsync(script, timeout, responseBufferSize).GetAwaiter().GetResult();

    /// <summary>Runs JavaScript in the connected browser and returns its UTF-8 result.</summary>
    public async Task<string> ExecuteJavaScriptAsync(
        string script,
        TimeSpan? timeout = null,
        int responseBufferSize = 4 * 1024,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        ArgumentOutOfRangeException.ThrowIfLessThan(responseBufferSize, 1);
        if (timeout is { } specifiedTimeout && specifiedTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var session = GetExecutionSession();
        var response = await session.ExecuteJavaScriptAsync(script, timeout, cancellationToken).ConfigureAwait(false);
        // A result is returned whole or not at all; cutting it would also split a UTF-8 sequence.
        if (response.Length > responseBufferSize)
        {
            throw new DesktopException(
                DesktopErrorCategory.LimitExceeded,
                ScriptResultTooLargeCode,
                $"The script result has {response.Length} UTF-8 bytes, more than the response buffer size of {responseBufferSize}.");
        }
        return Encoding.UTF8.GetString(response);
    }

    internal const string ScriptResultTooLargeCode = "script-result-too-large";

    /// <summary>Navigates every authenticated browser to a URL.</summary>
    public void Navigate(string url) => NavigateAsync(url).GetAwaiter().GetResult();

    // Navigates the window's own WebView, which needs no page connection: a page without the Bridge, such as a
    // build error page, cannot be navigated through its session. False without an open embedded window, for example
    // when an installed browser shows the surface.
    internal async Task<bool> NavigateEmbeddedWindowAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(url);
        if (Volatile.Read(ref _embeddedHost) is not { IsOpen: true } host) return false;
        await host.NavigateAsync(url, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Navigates every authenticated browser to a URL.</summary>
    public Task NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        // The window's page: a side panel's page keeps its own address.
        return Task.WhenAll(_sessions.Values
            .Where(static session => session.IsAuthenticated && session.Slot == DesktopConnectionSlot.Page)
            .Select(session => session.NavigateAsync(url, cancellationToken)));
    }

    /// <summary>Sends arbitrary bytes to every authenticated browser.</summary>
    public void SendRaw(string function, ReadOnlySpan<byte> data)
        => SendRawAsync(function, data.ToArray()).GetAwaiter().GetResult();

    /// <summary>Sends arbitrary bytes to every authenticated browser.</summary>
    public Task SendRawAsync(string function, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(function);
        return Task.WhenAll(_sessions.Values
            .Where(static session => session.IsAuthenticated)
            .Select(session => session.SendRawAsync(function, data, cancellationToken)));
    }

    /// <summary>Stops the server and closes its active bridge connections.</summary>
    public void Close() => CloseAsync().GetAwaiter().GetResult();

    /// <summary>Stops the server and closes its active bridge connections.</summary>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await CloseCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (ReferenceEquals(CallbackWindow.Value, this))
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _ = CompleteDisposeAsync();
            }
            return;
        }

        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ = CompleteDisposeAsync();
        }

        if (!ReferenceEquals(CallbackWindow.Value, this))
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
        }
    }

    private async Task CompleteDisposeAsync()
    {
        if (Interlocked.Exchange(ref _menuBarSubscribed, 0) != 0 && MenuHost is { } menuHost) menuHost.MenuBarChanged -= OnMenuBarChanged;
        try
        {
            await CloseCoreAsync(CancellationToken.None).ConfigureAwait(false);
            _shutdown.Dispose();
            _lifecycleGate.Dispose();
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
        }
    }

    private Task CloseCoreAsync(CancellationToken cancellationToken) =>
        CloseCoreAsync(disposeSurface: true, cancellationToken);

    private ValueTask CloseFromHostAsync() =>
        new(CloseCoreAsync(disposeSurface: false, CancellationToken.None));

    private async Task CloseCoreAsync(bool disposeSurface, CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var surface = _surface;
            if (surface is null)
            {
                await StopBrowserAsync().ConfigureAwait(false);
                return;
            }

            var sessions = _sessions.Values.ToArray();
            var bridgeClosures = sessions
                .Where(static session => session.IsAuthenticated)
                .Select(session => CloseBridgeIgnoringFailureAsync(session, cancellationToken))
                .ToArray();
            await Task.WhenAll(bridgeClosures).ConfigureAwait(false);

            _shutdown.Cancel();
            var sessionClosures = sessions.Select(DisposeSessionIgnoringFailureAsync).ToArray();
            await Task.WhenAll(sessionClosures).ConfigureAwait(false);

            _sessions.Clear();
            await StopBrowserAsync().ConfigureAwait(false);
            try
            {
                if (disposeSurface)
                {
                    await surface.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _surface = null;
                if (disposeSurface && _ownsHost && _host is not null)
                {
                    await _host.DisposeAsync().ConfigureAwait(false);
                    _host = null;
                }
                Url = null;
                lock (_connectionGate)
                {
                    _activeConnections = 0;
                }
                if (_runtimeOptions is null)
                {
                    WebUiApplication.Stopped(this);
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal uint Token => _token;

    internal string? GeneratedProfilePath => _generatedProfilePath;

    internal Exception? LastProfileCleanupFailure { get; private set; }

    internal bool HasEmbeddedHost => _embeddedHost is not null;

    internal bool RequiresMainThreadEventPump => _embeddedHost is IWebUiMainThreadHost;

    internal bool IsExecutingCallback => ReferenceEquals(CallbackWindow.Value, this);

    internal void NotifyAuthenticated() => _browserConnected.TrySetResult();

    internal BridgeHandshakeLog HandshakeLog => Volatile.Read(ref _handshakeLog);

    internal void ReportHandshakeExpired(nuint connectionId, TimeSpan silentFor, string reason)
    {
        if (_runtimeOptions?.Logger is { } logger)
        {
            DesktopLog.BridgeHandshakeExpired(logger, connectionId, silentFor.TotalSeconds, reason);
        }
        else
        {
            Trace.TraceWarning(
                $"{BridgeHandshakeExpiredCode}: Bridge WebSocket {connectionId} sent no token check in {silentFor.TotalSeconds:0.###} seconds and was closed ({reason}); the page reconnects.");
        }
    }

    internal void ProcessEmbeddedHostEvents()
    {
        if (_embeddedHost is IWebUiMainThreadHost host)
        {
            host.ProcessEvents();
        }
    }

    internal Task BeginEmbeddedWindowMoveAsync(CancellationToken cancellationToken)
        => _embeddedHost?.BeginMoveAsync(cancellationToken).AsTask() ?? Task.CompletedTask;

    internal Task ClosePresentationAsync() => StopBrowserAsync();

    // DesktopWindowOptions is a complete generation configuration. Compatibility
    // WebUiWindow callers retain their incremental settings across close/reopen.
    internal void ResetDesktopPresentationOptions()
    {
        _minimumWidth = null;
        _minimumHeight = null;
        _x = null;
        _y = null;
        _centered = false;
        _placement = null;
        _highContrast = null;
        _iconFile = null;
        _profileConfigured = false;
        _profileName = null;
        _profilePath = null;
        _proxyServer = null;
        _browserArgumentList = [];
        _browserArguments = null;
        _menuBarPresentation = DesktopMenuBarPresentation.Native;
        _monitor = null;
        _layoutDirection = Runic.Platform.DesktopLayoutDirection.Default;
        _language = null;
        _macOS = new();
        _ownerHandle = 0;
        _openedOwnerHandle = 0;
        _modality = DesktopWindowModality.None;
    }

    // The monitor, native direction and web language of the next embedded window (DesktopWindowOptions).
    internal void SetMonitorAndLanguage(string? monitor, Runic.Platform.DesktopLayoutDirection layoutDirection, string? language)
    {
        _monitor = monitor;
        _layoutDirection = layoutDirection;
        _language = language;
    }

    // DesktopWindow.SetLayoutDirectionAsync for the open embedded window.
    internal async ValueTask SetEmbeddedLayoutDirectionAsync(Runic.Platform.DesktopLayoutDirection layoutDirection, CancellationToken cancellationToken)
    {
        await GetEmbeddedHost().SetLayoutDirectionAsync(layoutDirection, cancellationToken).ConfigureAwait(false);
        _layoutDirection = layoutDirection;
    }

    // Whether the next embedded window's documents start with spellcheck="false" (DesktopWindowOptions.SpellChecking).
    internal void SetSpellChecking(bool spellChecking) => _spellChecking = spellChecking;

    private bool _spellChecking = true;

    // The spell-checking languages the current embedded window opened with, from DesktopHost.SpellCheckingLanguages.
    private IReadOnlyList<string> _openedSpellCheckingLanguages = [];

    // DesktopHost.SetSpellCheckingLanguagesAsync for the open embedded window.
    internal ValueTask SetEmbeddedSpellCheckingLanguagesAsync(IReadOnlyList<string> languages, CancellationToken cancellationToken) =>
        Volatile.Read(ref _embeddedHost) is { IsOpen: true } host ? host.SetSpellCheckingLanguagesAsync(languages, cancellationToken) : ValueTask.CompletedTask;

    // Languages that DesktopHost.SetSpellCheckingLanguagesAsync changed while the window was opening, which the host
    // then did not have open yet.
    private void PushSpellCheckingLanguages(IWebUiEmbeddedHost host)
    {
        if (MenuHost is not { } desktopHost || ReferenceEquals(desktopHost.SpellCheckingLanguages, _openedSpellCheckingLanguages)) return;
        _ = PushSpellCheckingLanguagesAsync(host, desktopHost.SpellCheckingLanguages);
    }

    private async Task PushSpellCheckingLanguagesAsync(IWebUiEmbeddedHost host, IReadOnlyList<string> languages)
    {
        try
        {
            await host.SetSpellCheckingLanguagesAsync(languages).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!host.IsOpen || !ReferenceEquals(Volatile.Read(ref _embeddedHost), host)) return;
            ReportEmbeddedDiagnostic(new DesktopDiagnostic(DesktopErrorCategory.OperationFailed, "spell-checking-languages-failed",
                $"The window could not change its spell-checking languages: {DesktopLog.ErrorType(error)}.", Retryable: true)
            {
                Severity = DesktopDiagnosticSeverity.Warning,
                Option = "DesktopHostOptions.SpellCheckingLanguages",
            });
        }
    }

    // DesktopWindowOptions.SpellChecking = false: every document of the window, in every frame, starts with
    // spellcheck="false" on its root element, which its fields inherit unless they set spellcheck="true" themselves. It
    // replaces a spellcheck attribute of the page's own <html>, as soon as the parser has created the element.
    internal const string SpellCheckingOffScript = """
        (() => {
          const off = () => document.documentElement.setAttribute("spellcheck", "false");
          if (document.documentElement) { off(); return; }
          const observer = new MutationObserver(() => {
            if (!document.documentElement) return;
            observer.disconnect();
            off();
          });
          observer.observe(document, { childList: true });
        })();
        """;

    // The macOS title bar of the next embedded window (DesktopWindowOptions.MacOS).
    internal void SetMacOS(MacOSWindowOptions macOS) => _macOS = macOS;

    // The owner handle the current embedded window received, or zero.
    private nint _openedOwnerHandle;

    internal nint OpenedOwnerHandle => _openedOwnerHandle;

    // The owner and modality of the next embedded window (DesktopWindowOptions.Owner and Modality).
    internal void SetOwner(nint ownerHandle, DesktopWindowModality modality)
    {
        _ownerHandle = ownerHandle;
        _modality = ownerHandle == 0 ? DesktopWindowModality.None : modality;
    }

    // Where the next embedded window shows the menu bar (DesktopWindowOptions.MenuBarPresentation).
    internal void SetMenuBarPresentation(DesktopMenuBarPresentation presentation) => _menuBarPresentation = presentation;

    // The light or dark appearance of the next embedded window (DesktopWindowOptions.ColorScheme).
    internal void SetColorScheme(Runic.Platform.DesktopColorScheme colorScheme, bool fromApplication = false)
    {
        _colorScheme = colorScheme;
        _colorSchemeFromApplication = fromApplication;
    }

    // Whether the next window's scheme is the application's (DesktopHost.SetColorSchemeAsync), which a host whose scheme
    // belongs to the process, as GTK's does, applies once it is open.
    private bool _colorSchemeFromApplication;

    private void PushColorScheme(IWebUiEmbeddedHost host)
    {
        if (!_colorSchemeFromApplication || (host.Capabilities & DesktopWindowCapabilities.ColorScheme) == 0) return;
        _ = PushColorSchemeAsync(host, _colorScheme);
    }

    private async Task PushColorSchemeAsync(IWebUiEmbeddedHost host, Runic.Platform.DesktopColorScheme colorScheme)
    {
        try
        {
            await host.SetColorSchemeAsync(colorScheme).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!host.IsOpen || !ReferenceEquals(Volatile.Read(ref _embeddedHost), host)) return;
            ReportEmbeddedDiagnostic(new DesktopDiagnostic(DesktopErrorCategory.OperationFailed, "color-scheme-failed",
                $"The window could not apply the application's color scheme: {DesktopLog.ErrorType(error)}.", Retryable: true)
            {
                Severity = DesktopDiagnosticSeverity.Warning,
                Option = "DesktopHost.ColorScheme",
            });
        }
    }

    // The saved placement the next embedded window restores; ApplyWindowOptions already set its size and position.
    internal void SetPlacement(DesktopWindowPlacement placement) => _placement = placement;

    // The embedded window's last reported state, kept after it closes, and each change, from the current host only.
    internal DesktopWindowState? EmbeddedWindowState => Volatile.Read(ref _embeddedHost)?.State ?? Volatile.Read(ref _embeddedWindowState);

    internal event Action<object, DesktopWindowState>? EmbeddedWindowStateChanged;

    // Identifies the current embedded window, so a DesktopWindow ignores the state of a later replacement.
    internal object? EmbeddedHostIdentity => Volatile.Read(ref _embeddedHost);

    internal Microsoft.Extensions.Logging.ILogger? Logger => _runtimeOptions?.Logger;

    // The current embedded window's menu command target; a replacement window starts without one.
    internal void SetEmbeddedMenuCommands(IDesktopMenuCommandTarget? target)
    {
        if (Volatile.Read(ref _embeddedHost) is { } host && Volatile.Read(ref _menuChain) is { } chain && ReferenceEquals(chain.Host, host))
            chain.Commands.Window = target;
    }

    private volatile bool _menuShortcutsSuspended;

    // Whether the embedded host's own menu path runs nothing, while an in-page bar matches the shortcuts itself; a
    // replacement window keeps the setting until the page changes it.
    internal void SetEmbeddedMenuShortcutsSuspended(bool suspended)
    {
        _menuShortcutsSuspended = suspended;
        if (Volatile.Read(ref _menuChain) is { } chain) chain.Commands.ShortcutsSuspended = suspended;
    }

    // The command path of the current embedded host's items, with the host it belongs to.
    private sealed record EmbeddedMenuChain(IWebUiEmbeddedHost Host, MenuCommandChain Commands);
    private EmbeddedMenuChain? _menuChain;

    private DesktopMenuBarPresentation _menuBarPresentation;
    private Runic.Platform.DesktopColorScheme _colorScheme;
    private int _menuBarSubscribed;

    // The host whose menu bar the windows show, and whose MenuCommands come after the window's.
    private DesktopHost? MenuHost => _runtimeOptions?.MenuHost;

    // Gives a new embedded host the command path of its items, and follows the host's menu bar from now on.
    private void AttachMenuCommands(IWebUiEmbeddedHost host)
    {
        var menuHost = MenuHost;
        var commands = new MenuCommandChain(() => menuHost?.MenuCommands, Logger) { ShortcutsSuspended = _menuShortcutsSuspended };
        Volatile.Write(ref _menuChain, new EmbeddedMenuChain(host, commands));
        host.MenuCommands = commands;
        if (menuHost is not null && Interlocked.Exchange(ref _menuBarSubscribed, 1) == 0) menuHost.MenuBarChanged += OnMenuBarChanged;
    }

    private void OnMenuBarChanged(object? sender, EventArgs eventArgs)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            if (sender is DesktopHost menuHost) menuHost.MenuBarChanged -= OnMenuBarChanged;
            return;
        }
        // A hosted window shows the guest's menus; the macOS application menu bar follows the host's on its own.
        if (HostedDataStore is not null) return;
        if (Volatile.Read(ref _embeddedHost) is { IsOpen: true } host) PushMenuBar(host);
    }

    // runic-host/1 menus/1: the guest's menus, which a hosted window shows instead of DesktopHost.MenuBar; none until the
    // guest sets them, so a hosted window never shows the host application's own menus.
    private IReadOnlyList<Runic.Platform.DesktopMenu> _hostedMenuBar = [];

    // Replaces a hosted window's menus (DesktopWindow.SetHostedMenuBarAsync); checked by the caller.
    internal Task SetHostedMenuBarAsync(IReadOnlyList<Runic.Platform.DesktopMenu> menus)
    {
        Volatile.Write(ref _hostedMenuBar, menus);
        return Volatile.Read(ref _embeddedHost) is { IsOpen: true } host ? PushMenuBarAsync(host, menus) : Task.CompletedTask;
    }

    // Hands the host's current menus to the window, or a hosted window's own; a failure is the window's diagnostic,
    // never the caller's.
    private void PushMenuBar(IWebUiEmbeddedHost host)
    {
        if (HostedDataStore is not null)
        {
            _ = PushMenuBarAsync(host, Volatile.Read(ref _hostedMenuBar));
            return;
        }
        if (MenuHost is not { } menuHost) return;
        _ = PushMenuBarAsync(host, menuHost.MenuBar);
    }

    private async Task PushMenuBarAsync(IWebUiEmbeddedHost host, IReadOnlyList<Runic.Platform.DesktopMenu> menus)
    {
        try
        {
            await host.SetMenuBarAsync(menus).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!host.IsOpen || !ReferenceEquals(Volatile.Read(ref _embeddedHost), host)) return;
            ReportEmbeddedDiagnostic(new DesktopDiagnostic(DesktopErrorCategory.OperationFailed, "menu-bar-failed",
                $"The window could not show the menu bar: {DesktopLog.ErrorType(error)}.", Retryable: true)
            {
                Severity = DesktopDiagnosticSeverity.Warning,
                Option = "DesktopHostOptions.MenuBar",
            });
        }
    }

    // Where the current embedded window shows its menu bar now.
    internal DesktopMenuBarPlacement EmbeddedMenuBarPlacement =>
        Volatile.Read(ref _embeddedHost) is { IsOpen: true } host ? host.MenuBarPlacement : DesktopMenuBarPlacement.None;

    // Each change of the current embedded window's placement, with the window that reported it.
    internal event Action<object>? EmbeddedMenuBarPlacementChanged;

    private void EmbeddedHostMenuBarPlacementChanged(object? sender, EventArgs eventArgs)
    {
        if (sender is not IWebUiEmbeddedHost host || !ReferenceEquals(Volatile.Read(ref _embeddedHost), host)) return;
        var current = host.MenuBarPlacement;
        var previous = (DesktopMenuBarPlacement)Interlocked.Exchange(ref _loggedPlacement, (int)current);
        if (previous != current && _runtimeOptions?.Logger is { } logger)
        {
            DesktopLog.MenuBarPlacementChanged(logger, previous, current, current == DesktopMenuBarPlacement.Global
                ? "the desktop's global menu registrar appeared and took the window"
                : previous == DesktopMenuBarPlacement.Global ? "the desktop's global menu went away" : "the window changed");
        }
        EmbeddedMenuBarPlacementChanged?.Invoke(host);
    }

    // The placement last logged (event 3015), from when the current embedded window opened.
    private int _loggedPlacement;

    private void RecordMenuBarPlacement(IWebUiEmbeddedHost host) => Volatile.Write(ref _loggedPlacement, (int)host.MenuBarPlacement);

    // macOS document conventions on Runic's own window; nothing for another presentation.
    internal ValueTask SetRepresentedFileAsync(string? path, CancellationToken cancellationToken) =>
        Volatile.Read(ref _embeddedHost) is MacOsWkWebViewHost { IsOpen: true } host ? host.SetRepresentedFileAsync(path, cancellationToken) : ValueTask.CompletedTask;

    internal ValueTask SetDocumentEditedAsync(bool edited, CancellationToken cancellationToken) =>
        Volatile.Read(ref _embeddedHost) is MacOsWkWebViewHost { IsOpen: true } host ? host.SetDocumentEditedAsync(edited, cancellationToken) : ValueTask.CompletedTask;

    internal Task<Runic.Platform.DesktopDragEffects> StartDragAsync(Runic.Platform.DesktopDragData data, CancellationToken cancellationToken)
        => GetEmbeddedHost().StartDragAsync(data, cancellationToken).AsTask();

    internal Task<Runic.Platform.DesktopMenuItem?> ShowContextMenuAsync(System.Collections.Immutable.ImmutableArray<Runic.Platform.DesktopMenuEntry> items,
        Runic.Platform.DesktopContextMenuOptions options, CancellationToken cancellationToken)
        => GetEmbeddedHost().ShowContextMenuAsync(items, options, cancellationToken).AsTask();

    // Runs an item through the command path: on the window's UI thread when it has one, else on the caller's.
    internal async Task<bool> TryExecuteMenuItemAsync(Runic.Platform.DesktopMenuItem item, IDesktopMenuCommandTarget? window,
        CancellationToken cancellationToken)
    {
        var application = MenuHost?.MenuCommands;
        if (Volatile.Read(ref _embeddedHost) is { IsOpen: true, SupportsNativeDispatch: true } host && !host.CheckNativeAccess())
        {
            var ran = false;
            await host.InvokeNativeAsync(() => ran = MenuCommandChain.TryExecute(item, window, application, Logger), cancellationToken)
                .ConfigureAwait(false);
            return ran;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return MenuCommandChain.TryExecute(item, window, application, Logger);
    }

    // Whether each item can run now, as the command path asks, and its check mark: on the window's UI thread when it has one.
    internal async Task<(bool Enabled, bool? Checked)[]> MenuItemStatesAsync(IReadOnlyList<Runic.Platform.DesktopMenuItem> items,
        IDesktopMenuCommandTarget? window, CancellationToken cancellationToken)
    {
        var application = MenuHost?.MenuCommands;
        var results = new (bool Enabled, bool? Checked)[items.Count];
        void Ask()
        {
            for (var index = 0; index < items.Count; index++)
                results[index] = (MenuCommandChain.CanExecute(items[index], window, application, Logger),
                    MenuCommandChain.IsChecked(items[index], window, application, Logger));
        }
        if (Volatile.Read(ref _embeddedHost) is { IsOpen: true, SupportsNativeDispatch: true } host && !host.CheckNativeAccess())
        {
            await host.InvokeNativeAsync(Ask, cancellationToken).ConfigureAwait(false);
            return results;
        }
        cancellationToken.ThrowIfCancellationRequested();
        Ask();
        return results;
    }

    // Each drop of files onto the current embedded window, with the window that reported it.
    internal event Action<object, DesktopFileDropEventArgs>? EmbeddedFilesDropped;

    private void EmbeddedHostFilesDropped(object? sender, DesktopFileDropEventArgs eventArgs)
    {
        if (sender is IWebUiEmbeddedHost host && ReferenceEquals(Volatile.Read(ref _embeddedHost), host))
        {
            EmbeddedFilesDropped?.Invoke(host, eventArgs);
        }
    }

    private DesktopWindowState? _embeddedWindowState;

    // The current embedded window's activity, and each change, from the current host only.
    internal DesktopWindowActivity? EmbeddedWindowActivity => Volatile.Read(ref _embeddedHost)?.Activity;

    internal event Action<object, DesktopWindowActivity>? EmbeddedWindowActivityChanged;

    private void EmbeddedHostActivityChanged(object? sender, EventArgs eventArgs)
    {
        if (sender is IWebUiEmbeddedHost host && ReferenceEquals(Volatile.Read(ref _embeddedHost), host) && host.Activity is { } activity)
        {
            EmbeddedWindowActivityChanged?.Invoke(host, activity);
        }
    }

    // DesktopWindow.SetColorSchemeAsync and DesktopHost.SetColorSchemeAsync for the open embedded window; the next
    // window opens with the scheme too.
    internal ValueTask SetEmbeddedColorSchemeAsync(Runic.Platform.DesktopColorScheme colorScheme, CancellationToken cancellationToken)
    {
        _colorScheme = colorScheme;
        return GetEmbeddedHost().SetColorSchemeAsync(colorScheme, cancellationToken);
    }

    private void EmbeddedHostStateChanged(object? sender, EventArgs eventArgs)
    {
        if (sender is IWebUiEmbeddedHost host && ReferenceEquals(Volatile.Read(ref _embeddedHost), host) && host.State is { } state)
        {
            Volatile.Write(ref _embeddedWindowState, state);
            EmbeddedWindowStateChanged?.Invoke(host, state);
        }
    }

    internal ValueTask ResizePresentationAsync(uint width, uint height, CancellationToken cancellationToken)
    {
        _width = width;
        _height = height;
        return GetEmbeddedHost().SetSizeAsync(width, height, cancellationToken);
    }

    internal ValueTask MovePresentationAsync(int x, int y, CancellationToken cancellationToken)
    {
        _x = x;
        _y = y;
        return GetEmbeddedHost().SetPositionAsync(x, y, cancellationToken);
    }

    internal Task CloseFromApplicationAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref _disposed) != 0
            ? _disposeCompletion.Task.WaitAsync(cancellationToken)
            : CloseCoreAsync(cancellationToken);

    internal string[] GetBindingNames() =>
        ["__webui_core_api__", .. _bindings.OrderBy(static pair => pair.Value.Order).Select(static pair => pair.Key)];

    internal async ValueTask<WebUiResult> InvokeCallbackAsync(
        WebUiSession session,
        string element,
        ReadOnlyMemory<byte>[] arguments,
        CancellationToken cancellationToken)
    {
        if (element == "__webui_core_api__")
        {
            if (arguments.Length > 1 && arguments[0].Span.SequenceEqual("region"u8))
                return ReportFrame(arguments[1]);
            return arguments.Length > 0 && arguments[0].Span.SequenceEqual("high_contrast"u8)
                ? WebUiResult.FromBoolean(_highContrast ?? WebUiSystemTheme.IsHighContrast)
                : WebUiResult.None;
        }

        if (!_bindings.TryGetValue(element, out var registration))
        {
            // A page can still call a capability its owner just released: a page removed from a navigation region
            // unmounts after the snapshot that removed it reached the browser. Such a call is late, not unadmitted.
            // Nothing is invoked, and the reply says the capability is gone, in the envelope a CS-WebUI host sends for
            // an unbound route, so a client reports it as released rather than as an invalid reply.
            if (WasRecentlyReleased(element)) return WebUiResult.FromString(ReleasedCapabilityReply);

            _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
                DesktopErrorCategory.CapabilityDenied,
                "capability-not-admitted",
                $"The presentation capability '{DescribeElement(element)}' is not admitted for this session.",
                Retryable: false,
                CorrelationId: Guid.NewGuid().ToString("N")));
            return WebUiResult.None;
        }

        return await InvokeRegistrationAsync(
            session,
            registration,
            WebUiEventType.Callback,
            element,
            arguments,
            (nuint)Interlocked.Increment(ref _nextEventNumber),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task DispatchEventAsync(
        WebUiSession session,
        WebUiEventType eventType,
        string element,
        ReadOnlyMemory<byte>[] arguments,
        CancellationToken cancellationToken)
    {
        var eventNumber = (nuint)Interlocked.Increment(ref _nextEventNumber);
        // Counted when the event is dispatched, so its handlers see the count with this connection. A session whose
        // Connected event was never dispatched, because it lost its admission first, is not counted (#317).
        if (eventType == WebUiEventType.Connected) _connectedSessions.TryAdd(session.Id, 0);
        else if (eventType == WebUiEventType.Disconnected) _connectedSessions.TryRemove(session.Id, out _);
        _bindings.TryGetValue(string.Empty, out var allEventsRegistration);
        if (allEventsRegistration is not null)
        {
            await InvokeRegistrationAsync(
                session,
                allEventsRegistration,
                eventType,
                element,
                arguments,
                eventNumber,
                cancellationToken).ConfigureAwait(false);
        }

        if (element.Length > 0 &&
            _bindings.TryGetValue(element, out var registration) &&
            registration != allEventsRegistration)
        {
            await InvokeRegistrationAsync(
                session,
                registration,
                eventType,
                element,
                arguments,
                eventNumber,
                cancellationToken).ConfigureAwait(false);
        }

        if (eventType is WebUiEventType.Connected or WebUiEventType.Disconnected)
        {
            NotifyConnectionObservers(session, eventType, eventNumber);
        }
    }

    // Observes Connected and Disconnected events without an all-events binding. That
    // binding is advertised to the page, whose bridge then cancels every navigation,
    // including location.reload(), and reports it as a Navigation event instead (#196).
    // Every observer receives every event, in subscription order.
    internal IDisposable ObserveConnections(Action<WebUiEvent> observer)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(observer);
        lock (_connectionObserverGate) _connectionObservers = [.. _connectionObservers, observer];
        return new ConnectionObservation(this, observer);
    }

    private void NotifyConnectionObservers(WebUiSession session, WebUiEventType eventType, nuint eventNumber)
    {
        var observers = Volatile.Read(ref _connectionObservers);
        if (observers.Length == 0)
        {
            return;
        }

        var webUiEvent = new WebUiEvent(
            this,
            session.Id,
            eventType,
            string.Empty,
            [],
            eventNumber,
            0,
            session.ClientId,
            session.ConnectionId,
            session.Cookies)
        {
            Slot = session.Slot,
        };
        var previousCallbackWindow = CallbackWindow.Value;
        CallbackWindow.Value = this;
        List<Exception>? failures = null;
        try
        {
            // One failing observer does not hide the event from the others.
            foreach (var observer in observers)
            {
                try
                {
                    observer(webUiEvent);
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }
        }
        finally
        {
            CallbackWindow.Value = previousCallbackWindow;
            webUiEvent.Invalidate();
        }

        if (failures is [var failure])
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
        else if (failures is not null)
        {
            throw new AggregateException(failures);
        }
    }

    private void RemoveConnectionObserver(Action<WebUiEvent> observer)
    {
        lock (_connectionObserverGate)
        {
            var index = Array.IndexOf(_connectionObservers, observer);
            if (index >= 0)
            {
                _connectionObservers = [.. _connectionObservers[..index], .. _connectionObservers[(index + 1)..]];
            }
        }
    }

    private sealed class ConnectionObservation(WebUiWindow window, Action<WebUiEvent> observer) : IDisposable
    {
        private WebUiWindow? _window = window;

        public void Dispose() => Interlocked.Exchange(ref _window, null)?.RemoveConnectionObserver(observer);
    }

    internal Task RunJavaScriptForSessionAsync(Guid sessionId, string script, CancellationToken cancellationToken)
        => GetSession(sessionId).RunJavaScriptAsync(script, cancellationToken);

    internal Task NavigateSessionAsync(Guid sessionId, string url, CancellationToken cancellationToken)
        => GetSession(sessionId).NavigateAsync(url, cancellationToken);

    internal Task SendRawToSessionAsync(
        Guid sessionId,
        string function,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
        => GetSession(sessionId).SendRawAsync(function, data, cancellationToken);

    internal Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken)
        => GetSession(sessionId).CloseBridgeAsync(cancellationToken);

    private async ValueTask<WebUiResult> InvokeRegistrationAsync(
        WebUiSession session,
        BindingRegistration registration,
        WebUiEventType eventType,
        string element,
        ReadOnlyMemory<byte>[] arguments,
        nuint eventNumber,
        CancellationToken cancellationToken)
    {
        var webUiEvent = new WebUiEvent(
            this,
            session.Id,
            eventType,
            element,
            arguments,
            eventNumber,
            (nuint)registration.Order,
            session.ClientId,
            session.ConnectionId,
            session.Cookies)
        {
            Slot = session.Slot,
        };
        var previousCallbackWindow = CallbackWindow.Value;
        CallbackWindow.Value = this;
        try
        {
            return await registration.Handler(webUiEvent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CallbackWindow.Value = previousCallbackWindow;
            webUiEvent.Invalidate();
        }
    }

    // The window's page, never a side panel's page.
    private WebUiSession GetExecutionSession()
        => _sessions.Values.FirstOrDefault(static session => session.IsAuthenticated && session.Slot == DesktopConnectionSlot.Page)
            ?? throw new InvalidOperationException("No authenticated WebUI browser is connected.");

    private IWebUiEmbeddedHost GetEmbeddedHost()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _embeddedHost is { IsOpen: true } host
            ? host
            : throw new InvalidOperationException("This window is not using an embedded WebView host.");
    }

    internal bool HasCloseConfirmation => _closeController is not null;

    internal bool SupportsCloseConfirmation => _embeddedHost?.SupportsCloseConfirmation == true;

    internal void SetCloseConfirmation(Func<CancellationToken, ValueTask<bool>>? confirm) => _confirmClose = confirm;

    internal async Task<bool> RequestCloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_closeController is { } controller)
        {
            return await controller.RequestAsync(cancellationToken).ConfigureAwait(false);
        }
        await ClosePresentationAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void ConfigureCloseConfirmation(IWebUiEmbeddedHost host)
    {
        if (_confirmClose is null) return;
        if (!host.SupportsCloseConfirmation)
        {
            throw new NotSupportedException("The embedded host does not support close confirmation.");
        }
        _closeController = new WindowCloseController(_confirmClose, () => host.CloseAsync(), _runtimeOptions?.Logger);
    }

    private void StopCloseConfirmation() => Interlocked.Exchange(ref _closeController, null)?.Dispose();

    // runic-host/1 hosted windows (host-protocol.md, Page admission): the window loads the guest's launch address
    // (DesktopContent.ExternalUrl), whose origin is the only application origin of the window. It injects no credential,
    // admits no origin of this surface and uses the host session's ephemeral data store.
    internal string? HostedDataStore
    {
        get => Volatile.Read(ref _hostedDataStore);
        set => Volatile.Write(ref _hostedDataStore, value);
    }

    private WebUiEmbeddedHostOptions CreateEmbeddedHostOptions()
    {
        var hosted = HostedDataStore;
        if (hosted is not null) Internal.EphemeralDataStores.RequireActive(hosted);
        // Only a host that can own windows receives the owner; the others open the window on its own.
        var ownerHandle = _embeddedHost is { } ownedHost && ownedHost.Capabilities.HasFlag(DesktopWindowCapabilities.Ownership)
            ? _ownerHandle
            : 0;
        string? profilePath = null;
        _configuredProfilePath = _profileConfigured ? _profilePath : null;
        if (hosted is not null)
        {
            // WebView2's data store is its user data folder: the host session's own, which its windows share like a
            // configured ProfilePath (SharedProfile).
            profilePath = _configuredProfilePath = OperatingSystem.IsWindows() ? Internal.EphemeralDataStores.Folder(hosted) : null;
        }
        else if (_profileConfigured)
        {
            profilePath = _profilePath;
        }
        else if (OperatingSystem.IsWindows())
        {
            profilePath = GetOrCreateProfilePath(WebUiBrowser.WebView);
        }

        return new WebUiEmbeddedHostOptions
        {
            CloseRequested = _closeController is { } close ? close.RequestFromPlatform : null,
            Width = _width ?? 800,
            Height = _height ?? 600,
            MinimumWidth = _minimumWidth,
            MinimumHeight = _minimumHeight,
            X = _x,
            Y = _y,
            Centered = _centered,
            Placement = _placement,
            Resizable = _resizable,
            Frameless = _frameless,
            Transparent = _transparent,
            Hidden = _hidden,
            Kiosk = _kiosk,
            HighContrast = _highContrast ?? WebUiSystemTheme.IsHighContrast,
            HighContrastFollowsSystem = _highContrast is null,
            IconFile = _iconFile,
            ProfilePath = profilePath,
            BrowserArguments = _browserArguments,
            AllowedPermissions = _allowedPermissions,
            // Only the host application's own pages show regions; a hosted window shows its guest's page.
            RegionFrames = hosted is null ? RegionFrames : null,
            WebContent = hosted is null
                ? WebContentRules.Resolve(_webContent, Environment.GetEnvironmentVariable)
                : HostedWebContent(WebContentRules.Resolve(_webContent, Environment.GetEnvironmentVariable)),
            // The surface's own origin too: a window presenting external content can return to it. A hosted window
            // (host-protocol.md, Page admission) has the guest's origin alone, the presented launch address's.
            AdditionalOrigins = hosted is not null
                ? []
                : (_securityPolicy?.AdditionalOrigins ?? Enumerable.Empty<string>())
                    .Select(static origin => new Uri(origin, UriKind.Absolute))
                    .Prepend(Url!)
                    .ToArray(),
            // A hosted window injects no credential into the guest's documents.
            DocumentStartScript = _embeddedHost?.SupportsDocumentStartScript != true
                ? null
                : hosted is not null
                    ? _spellChecking ? null : SpellCheckingOffScript
                    : _spellChecking ? CreateDocumentStartScript() : SpellCheckingOffScript + "\n" + CreateDocumentStartScript(),
            EphemeralDataStore = hosted,
            ReportDiagnostic = ReportEmbeddedDiagnostic,
            // The log of the open in progress: BeginConnection replaced it before the window is shown.
            StartupProgress = description => HandshakeLog.Record(description),
            // The deadline of the same open, which the WebView's progress extends (I-176).
            StartupSignal = Volatile.Read(ref _connectionDeadline) is { } deadline ? deadline.Report : null,
            MenuBarPresentation = _menuBarPresentation,
            ColorScheme = _colorScheme,
            GlobalMenu = _runtimeOptions?.GlobalMenu ?? true,
            MonitorId = _monitor,
            LayoutDirection = _layoutDirection,
            Language = _language,
            MacOS = _macOS,
            OwnerHandle = _openedOwnerHandle = ownerHandle,
            Modality = ownerHandle == 0 ? DesktopWindowModality.None : _modality,
            SpellChecking = _spellChecking,
            SpellCheckingLanguages = _openedSpellCheckingLanguages = MenuHost?.SpellCheckingLanguages ?? [],
        };
    }

    // A hosted window's top-level navigation stays on the guest's origin; links elsewhere open in the default browser.
    private static DesktopWebContentPolicy HostedWebContent(DesktopWebContentPolicy policy) =>
        policy.ExternalNavigation == DesktopExternalNavigation.AllowInWindow
            ? policy with { ExternalNavigation = DesktopExternalNavigation.OpenInBrowser }
            : policy;

    // An embedded window's own event, such as a crashed web process (#313): to the sink, whose failure is logged, and
    // to the log.
    private void ReportEmbeddedDiagnostic(DesktopDiagnostic diagnostic)
    {
        try
        {
            _runtimeOptions?.DiagnosticSink?.Invoke(diagnostic);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_runtimeOptions?.Logger is { } sinkLogger)
                DesktopLog.DiagnosticSinkFailed(sinkLogger, exception, diagnostic.Code, DesktopLog.ErrorType(exception));
            else Trace.TraceWarning($"The diagnostic sink failed for {diagnostic.Code}: {exception.GetType().FullName}");
        }
        if (_runtimeOptions?.Logger is { } globalMenuLogger && diagnostic.Code is LinuxGlobalMenu.FailedCode or LinuxGlobalMenu.PermissionCode)
        {
            // The Linux global menu's own events (3016, 3015); the bar stays in the window.
            if (diagnostic.Code == LinuxGlobalMenu.FailedCode)
                DesktopLog.GlobalMenuFailed(globalMenuLogger, diagnostic.Message);
            else
                DesktopLog.MenuBarPlacementChanged(globalMenuLogger, DesktopMenuBarPlacement.Window, DesktopMenuBarPlacement.Window,
                    diagnostic.Message + " " + diagnostic.Remediation);
        }
        else if (_runtimeOptions?.Logger is { } logger)
        {
            DesktopLog.EmbeddedWindowDiagnostic(logger,
                diagnostic.Severity == DesktopDiagnosticSeverity.Error ? LogLevel.Error : LogLevel.Warning,
                diagnostic.Code, diagnostic.Message, diagnostic.Remediation ?? string.Empty);
        }
        else if (diagnostic.Severity == DesktopDiagnosticSeverity.Error)
        {
            Trace.TraceError($"{diagnostic.Code}: {diagnostic.Message}");
        }
        else
        {
            Trace.TraceWarning($"{diagnostic.Code}: {diagnostic.Message}");
        }
    }

    internal event EventHandler? PresentationClosed;

    private void EmbeddedHostClosed(object? sender, EventArgs eventArgs)
    {
        if (sender is IWebUiEmbeddedHost host && ReferenceEquals(Volatile.Read(ref _embeddedHost), host))
        {
            PresentationClosed?.Invoke(this, EventArgs.Empty);
            _ = HandleEmbeddedHostClosedAsync(host);
        }
    }

    // A host can report closed before its Closed callback acquires the lifecycle gate.
    // Replacing it must still release its native owner, such as a dispatcher lease;
    // the late callback then finds a different host and does nothing.
    private async Task ReleaseClosedEmbeddedHostAsync()
    {
        if (_embeddedHost is null)
        {
            return;
        }

        await _presentationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _embeddedHost, null) is { } closed)
            {
                StopCloseConfirmation();
                closed.Closed -= EmbeddedHostClosed;
                closed.StateChanged -= EmbeddedHostStateChanged;
                closed.ActivityChanged -= EmbeddedHostActivityChanged;
                closed.MenuBarPlacementChanged -= EmbeddedHostMenuBarPlacementChanged;
                closed.FilesDropped -= EmbeddedHostFilesDropped;
                _currentBrowser = WebUiBrowser.NoBrowser;
                await closed.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _presentationGate.Release();
        }
    }

    private async Task HandleEmbeddedHostClosedAsync(IWebUiEmbeddedHost host)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _presentationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(Interlocked.CompareExchange(ref _embeddedHost, null, host), host))
                {
                    return;
                }
                StopCloseConfirmation();
                host.Closed -= EmbeddedHostClosed;
                host.StateChanged -= EmbeddedHostStateChanged;
                host.ActivityChanged -= EmbeddedHostActivityChanged;
                host.MenuBarPlacementChanged -= EmbeddedHostMenuBarPlacementChanged;
                host.FilesDropped -= EmbeddedHostFilesDropped;
                _currentBrowser = WebUiBrowser.NoBrowser;
                await host.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _presentationGate.Release();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
        if (_runtimeOptions is null)
        {
            await CloseCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    // Test seam: completes when an ended session starts draining its accepted
    // callbacks, or at once when the session has already been removed.
    internal Task CallbackDrainStarted(Guid sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session.CallbackDrainStarted : Task.CompletedTask;

    private WebUiSession GetSession(Guid sessionId)
        => _sessions.TryGetValue(sessionId, out var session) && session.IsAuthenticated
            ? session
            : throw new InvalidOperationException("The WebUI browser connection is no longer active.");

    internal void RemoveBinding(string element, long registrationId)
    {
        if (_bindings.TryGetValue(element, out var registration) && registration.Id == registrationId)
        {
            // Remembered first, so a call racing the removal finds the name in one of the two tables.
            RememberReleased(element);
            ((ICollection<KeyValuePair<string, BindingRegistration>>)_bindings)
                .Remove(new KeyValuePair<string, BindingRegistration>(element, registration));
        }
    }

    private void RememberReleased(string element)
    {
        lock (_releasedGate)
        {
            // Every registration takes a new id, so an unchanged id means no binding was registered since the open
            // group began: this release belongs to the same run, such as the cascade that releases one content tree.
            var lastRegistration = Volatile.Read(ref _nextRegistrationId);
            var group = _openReleasedGroup;
            if (group is null || group.LastRegistration != lastRegistration)
            {
                group = new ReleasedGroup(lastRegistration);
                _releasedGroups.Enqueue(group);
                _openReleasedGroup = group;
            }
            ref var owner = ref CollectionsMarshal.GetValueRefOrAddDefault(_releasedBindings, element, out _);
            if (ReferenceEquals(owner, group)) return;
            // A name released again moves to the newest group; its entry in an older group no longer owns it.
            owner = group;
            group.Names.Add(element);
            _releasedNameCount++;
            // Whole groups are forgotten, oldest first; the newest is kept whatever its size, so one large release
            // never forgets its own names. Its size is bounded by the bindings registered before the run began.
            while (_releasedGroups.Count > ReleasedGroupMemory
                || (_releasedNameCount > ReleasedNameMemory && _releasedGroups.Count > 1))
            {
                var forgotten = _releasedGroups.Dequeue();
                _releasedNameCount -= forgotten.Names.Count;
                foreach (var name in forgotten.Names)
                {
                    if (_releasedBindings.TryGetValue(name, out var current) && ReferenceEquals(current, forgotten))
                        _releasedBindings.Remove(name);
                }
            }
        }
    }

    // Test seam: the remembered groups and names.
    internal (int Groups, int Names) ReleasedMemory
    {
        get { lock (_releasedGate) return (_releasedGroups.Count, _releasedBindings.Count); }
    }

    private sealed class ReleasedGroup(long lastRegistration)
    {
        public long LastRegistration { get; } = lastRegistration;
        public List<string> Names { get; } = [];
    }

    // Only consulted for a name with no binding, so a rebound name is served by its new registration.
    internal bool WasRecentlyReleased(string element)
    {
        lock (_releasedGate) return _releasedBindings.ContainsKey(element);
    }

    // The page chooses the name: keep it short and printable in a diagnostic.
    private static string DescribeElement(string element)
    {
        const int limit = 64;
        var text = element.Length > limit ? element[..limit] + "..." : element;
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var index = 0; index < span.Length; index++)
                span[index] = char.IsControl(source[index]) ? '?' : source[index];
        });
    }

    private string? GetOrCreateProfilePath(WebUiBrowser browser)
    {
        if (_profileConfigured)
        {
            return _profilePath;
        }

        if (_generatedProfilePath is not null)
        {
            return _generatedProfilePath;
        }

        // A fresh owner-only directory: a shared temporary parent could be pre-created
        // by another local user, who could then substitute or read the profile.
        _generatedProfilePath = Directory.CreateTempSubdirectory(
            $"runic-desktop-{browser.ToString().ToLowerInvariant()}-").FullName;
        if (_runtimeOptions is null)
        {
            WebUiApplication.RegisterGeneratedProfile(_generatedProfilePath);
        }
        return _generatedProfilePath;
    }

    private async Task MonitorBrowserAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (ReferenceEquals(Volatile.Read(ref _browserProcess), process))
        {
            if (_runtimeOptions is null)
            {
                await CloseCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await StopBrowserAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task StopBrowserAsync()
    {
        // A native Closed callback can already be releasing the presentation.
        // All close callers must await that release even after its handle is cleared.
        await _presentationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopBrowserCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _presentationGate.Release();
        }
    }

    private async Task StopBrowserCoreAsync()
    {
        StopCloseConfirmation();
        var embeddedHost = Interlocked.Exchange(ref _embeddedHost, null);
        if (embeddedHost is not null)
        {
            embeddedHost.Closed -= EmbeddedHostClosed;
            embeddedHost.StateChanged -= EmbeddedHostStateChanged;
            embeddedHost.ActivityChanged -= EmbeddedHostActivityChanged;
            embeddedHost.MenuBarPlacementChanged -= EmbeddedHostMenuBarPlacementChanged;
            embeddedHost.FilesDropped -= EmbeddedHostFilesDropped;
            try
            {
                await embeddedHost.CloseAsync().ConfigureAwait(false);
            }
            finally
            {
                await embeddedHost.DisposeAsync().ConfigureAwait(false);
            }
        }

        var process = _browserProcess;
        var outputClosed = _browserOutputClosed;
        _browserOutputClosed = Task.CompletedTask;
        if ((process is not null || embeddedHost is not null)
            && WaitForConnection && _externalUrl is null)
        {
            _browserConnected.TrySetException(new IOException("The window closed before bridge authentication completed."));
        }
        _browserProcess = null;
        _currentBrowser = WebUiBrowser.NoBrowser;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    try
                    {
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // On macOS, the runtime's recursive stop/kill traversal can
                        // hang (dotnet/runtime#131944) or affect the shared process
                        // group. Chromium's children observe their parent exiting.
                        process.Kill(entireProcessTree: !OperatingSystem.IsMacOS());
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                }
                // POSIX allows deletion while a helper still has files open, and
                // that helper can recreate the directory. Join inherited output
                // pipes before disposing their readers or removing the profile.
                await outputClosed.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        if (_generatedProfilePath is { } profilePath)
        {
            _generatedProfilePath = null;
            // Waiting for the browser's main process does not wait for all of its
            // descendants. Give their profile handles a bounded interval to close.
            long cleanupStarted = Stopwatch.GetTimestamp();
            var deleted = false;
            while (!(deleted = TryDeleteGeneratedProfile(profilePath))
                && Stopwatch.GetElapsedTime(cleanupStarted) < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
            if (!deleted)
            {
                _ = DeleteGeneratedProfileLaterAsync(profilePath, _runtimeOptions?.Logger);
            }
        }
    }

    // On Windows a browser descendant can hold a profile file for longer than the
    // bounded wait, for example after a stalled launch was killed. Keep retrying in
    // the background so the profile is not leaked. WebUiApplication also keeps it
    // registered for DeleteAllProfiles until a deletion succeeds.
    private static async Task DeleteGeneratedProfileLaterAsync(string path, Microsoft.Extensions.Logging.ILogger? logger)
    {
        for (var retry = 0; retry < 60; retry++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            if (WebUiApplication.TryDeleteGeneratedProfile(path))
            {
                return;
            }
        }
        if (logger is not null) DesktopLog.BrowserProfileNotDeleted(logger);
        else Trace.TraceWarning("A generated browser profile could not be deleted; it remains in the temporary folder.");
    }

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private enum ConnectionProgress
    {
        NoRequest,
        DocumentRequested,
        BridgeRequested,
        WebSocketRejected,
        WebSocketOpened,
    }

    private Task BeginConnection()
    {
        Volatile.Write(ref _connectionProgress, (int)ConnectionProgress.NoRequest);
        Volatile.Write(ref _otherRequests, 0);
        Volatile.Write(ref _firstOtherRequest, null);
        Volatile.Write(ref _connectionDeadline, new ConnectionDeadline(ConnectionTimeout, TimeProvider));
        Volatile.Write(ref _handshakeLog, new BridgeHandshakeLog());
        _browserConnected = NewCompletionSource();
        return _browserConnected.Task;
    }

    // A request that reached the presentation counts toward its connection only when it proves it came from the page
    // (IsFromPage, or proven by the caller); any other request is answered as usual but moves neither the progress nor
    // the deadline, and the first one is logged.
    private void ReportConnectionProgress(PresentationExchange context, ConnectionProgress progress, bool proven = false)
    {
        if (proven || IsFromPage(context))
        {
            ReportConnectionProgress(progress);
        }
        else
        {
            ReportOtherRequest(context);
        }
    }

    private void ReportOtherRequest(PresentationExchange context)
    {
        if (Interlocked.Increment(ref _otherRequests) == 1)
        {
            // A spent launch code does not belong in a log either.
            var request = $"{context.Method} {FrameLaunchBinding.Redact(context.Path)}";
            Volatile.Write(ref _firstOtherRequest, request);
            HandshakeLog.Record($"{request} requested without the window's cookie or launch code");
        }
    }

    // Whether a request proves that it came from the page this window presented. A development tool or scanner that
    // probes each new loopback listener (t3code's preview discovery sends GET / to every one) would otherwise make a
    // stalled browser look connected, skipping its relaunch, and move the deadline. A page's first document request
    // carries nothing to tell it from such a probe. Its response gives the page the window's client cookie, which
    // every later request carries; a browser this window launched is bound to one client by its launch code
    // (ServeBrowserLaunchAsync, which counts that request), and then only that client counts. A WebSocket can also
    // prove itself with the session credential (AcceptWebSocketAsync).
    private bool IsFromPage(PresentationExchange context)
    {
        var client = FindClient(context);
        return client != 0 && (!ChecksLaunchBinding || _launchBinding.Admits(client));
    }

    private void ReportConnectionProgress(ConnectionProgress progress)
    {
        // Every reported stage is a request from the page that reached the server.
        Volatile.Read(ref _connectionDeadline)?.ReportRequest();
        var current = Volatile.Read(ref _connectionProgress);
        while (current < (int)progress)
        {
            var observed = Interlocked.CompareExchange(ref _connectionProgress, (int)progress, current);
            if (observed == current)
            {
                return;
            }
            current = observed;
        }
    }

    private async Task WaitForConnectionAsync(
        Task connection,
        Process? browserProcess,
        bool surfaceHosted,
        CancellationToken cancellationToken,
        Func<Process, Task, Task<RelaunchResult>>? relaunchStalled = null)
    {
        // Each attempt gets the whole connection timeout. Only a launched browser
        // that never requested anything is relaunched; see RelaunchStalledBrowserAsync.
        var attempts = browserProcess is null || relaunchStalled is null ? 1 : BrowserLaunchAttempts;
        var attempt = 1;
        try
        {
            while (true)
            {
                string stalled;
                try
                {
                    await WaitWithinConnectionDeadlineAsync(connection, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (TimeoutException exception)
                {
                    // Describe the browser process before closing the presentation stops it.
                    stalled = DescribeConnectionTimeout(browserProcess, attempt, attempts);
                    if (attempt >= attempts || !IsStalledBeforeFirstRequest(browserProcess))
                    {
                        if (await ProbeStalledConnectionAsync().ConfigureAwait(false))
                        {
                            stalled = DescribeConnectionTimeout(browserProcess, attempt, attempts);
                        }
                        var timeout = new TimeoutException(stalled, exception);
                        await CloseFailedPresentationAsync(surfaceHosted).ConfigureAwait(false);
                        throw timeout;
                    }
                }
                catch
                {
                    await CloseFailedPresentationAsync(surfaceHosted).ConfigureAwait(false);
                    throw;
                }

                RelaunchResult relaunch;
                try
                {
                    relaunch = await relaunchStalled!(browserProcess!, connection).ConfigureAwait(false);
                }
                catch
                {
                    await CloseFailedPresentationAsync(surfaceHosted).ConfigureAwait(false);
                    throw;
                }
                switch (relaunch.Outcome)
                {
                    case RelaunchOutcome.KeepWaiting:
                        // The page progressed after the stall check; this attempt continues.
                        continue;
                    case RelaunchOutcome.Exited:
                    {
                        var exited = new TimeoutException(
                            DescribeConnectionTimeout(browserProcess, attempt, attempts, exitedBeforeRelaunch: true));
                        await CloseFailedPresentationAsync(surfaceHosted).ConfigureAwait(false);
                        throw exited;
                    }
                    case RelaunchOutcome.Closed:
                        throw new IOException("The window closed before bridge authentication completed.");
                }
                attempt++;
                ReportStalledLaunch(stalled, attempt, attempts);
                // Stopping the stalled browser faulted its connection, which nothing waits for.
                ObserveAbandoned(connection);
                connection = relaunch.Connection!;
                browserProcess = relaunch.Process!;
            }
        }
        finally
        {
            // A wait that gives up on its connection closes the presentation, which faults the connection after
            // nothing waits for it any more.
            if (!connection.IsCompletedSuccessfully)
            {
                ObserveAbandoned(connection);
            }
        }
    }

    // A connection whose wait ended without it: the close that ends its presentation (StopBrowserCoreAsync) faults it
    // after the wait gave up, by a timeout, a relaunch, a cancellation or a failure. Its fault is expected, so it is
    // observed here instead of surfacing as an unobserved task exception.
    private static void ObserveAbandoned(Task connection) =>
        _ = connection.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    // I-176: when nothing reached the server, records whether the server still accepted a loopback connection at the
    // timeout, and how long a small write-through to the profile's disk (or the temporary folder's) took. A browser
    // that stalled while both were quick waited inside itself. Each probe is bounded; true when it recorded them.
    private async Task<bool> ProbeStalledConnectionAsync()
    {
        if ((ConnectionProgress)Volatile.Read(ref _connectionProgress) != ConnectionProgress.NoRequest || Url is not { } url)
        {
            return false;
        }
        var connect = await MeasureProbeAsync(async cancellationToken =>
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(url.Host, url.Port, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        var profile = _profileConfigured ? null : _generatedProfilePath;
        var folder = profile ?? Path.GetTempPath();
        var write = await MeasureProbeAsync(cancellationToken => Task.Run(() =>
        {
            using var stream = new FileStream(
                Path.Combine(folder, $".runic-probe-{Guid.NewGuid():N}"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough | FileOptions.DeleteOnClose);
            stream.Write(new byte[4096]);
            stream.Flush(flushToDisk: true);
        }, cancellationToken)).ConfigureAwait(false);
        HandshakeLog.Record($"at the timeout: loopback connect {connect}; {(profile is null ? "temporary" : "profile")} folder write {write}");
        return true;
    }

    private static async Task<string> MeasureProbeAsync(Func<CancellationToken, Task> probe)
    {
        var limit = TimeSpan.FromSeconds(5);
        using var cancellation = new CancellationTokenSource(limit);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await probe(cancellation.Token).WaitAsync(limit).ConfigureAwait(false);
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"not done within {limit.TotalSeconds:0} s");
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or IOException or UnauthorizedAccessException)
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"failed after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms ({exception.GetType().Name})");
        }
    }

    // The connection timeout counts from the launch while nothing has reached the server, and from the first
    // request once something has. A browser that starts slowly and reaches the server just before the deadline
    // (27 seconds into 30 on a Windows runner, I-180) still gets the whole timeout to finish the Bridge handshake,
    // instead of being closed while it connects. Before any request, an embedded WebView that is still loading the
    // page extends the timeout, up to ConnectionDeadline.CapFactor timeouts (I-176); see ConnectionDeadline.
    private async Task WaitWithinConnectionDeadlineAsync(Task connection, CancellationToken cancellationToken)
    {
        var deadline = Volatile.Read(ref _connectionDeadline)!;
        var started = deadline.Now;
        while (!connection.IsCompleted)
        {
            if (RemainingConnectionTime(deadline, started) is not { } remaining)
            {
                throw new TimeoutException();
            }
            try
            {
                // A startup signal can move the deadline, so it wakes the wait. A timer waits at most about 49 days.
                await Task.WhenAny(connection, deadline.Changed)
                    .WaitAsync(remaining < MaximumConnectionWait ? remaining : MaximumConnectionWait, deadline.Time, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
        await connection.ConfigureAwait(false);
    }

    private static readonly TimeSpan MaximumConnectionWait = TimeSpan.FromDays(1);

    // The time left of a wait that started at started, or null once it ran out. Logs the wait's first extension.
    private TimeSpan? RemainingConnectionTime(ConnectionDeadline deadline, TimeSpan started)
    {
        var remaining = deadline.Remaining(started, out var extended);
        if (extended is not null)
        {
            ReportConnectionDeadlineExtended(deadline, extended);
        }
        return remaining;
    }

    // A slow start that connects is otherwise invisible (I-176).
    private void ReportConnectionDeadlineExtended(ConnectionDeadline deadline, ConnectionDeadline.Extension extended)
    {
        HandshakeLog.Record($"timeout extended: {extended.Progress}");
        if (_runtimeOptions?.Logger is { } logger)
        {
            DesktopLog.ConnectionDeadlineExtended(
                logger, deadline.Timeout.TotalSeconds, extended.Progress, deadline.Cap.TotalSeconds);
        }
        else
        {
            var message = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"the embedded WebView had requested nothing after {deadline.Timeout.TotalSeconds:0.###} seconds but was still making progress ({extended.Progress}); waiting up to {deadline.Cap.TotalSeconds:0.###} seconds.");
            Trace.TraceWarning($"{ConnectionDeadlineExtendedCode}: {message}");
        }
    }

    // Checked again under the lifecycle gate before the relaunch commits.
    private bool IsStalledBeforeFirstRequest(Process? browserProcess) =>
        browserProcess is not null
        && (ConnectionProgress)Volatile.Read(ref _connectionProgress) == ConnectionProgress.NoRequest
        && !HasExited(browserProcess);

    // Reported after the relaunch, outside the lifecycle gate, so a slow or failing
    // sink cannot hold the stalled browser or its profile.
    private void ReportStalledLaunch(string description, int attempt, int attempts)
    {
        var profile = _profileConfigured ? "its configured profile" : "a fresh profile";
        var message = $"{description} Relaunched it with {profile} (attempt {attempt} of {attempts}).";
        try
        {
            _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
            DesktopErrorCategory.TimedOut,
            BrowserLaunchStalledCode,
            message,
            Retryable: true,
            Remediation: "No action is needed if the relaunch connects. Set DesktopHostOptions.BrowserLaunchAttempts to 1 to disable relaunching.")
        {
                Severity = DesktopDiagnosticSeverity.Warning,
                Option = "DesktopHostOptions.BrowserLaunchAttempts",
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_runtimeOptions?.Logger is { } sinkLogger)
                DesktopLog.DiagnosticSinkFailed(sinkLogger, exception, BrowserLaunchStalledCode, DesktopLog.ErrorType(exception));
            else Trace.TraceWarning($"The diagnostic sink failed for {BrowserLaunchStalledCode}: {exception.GetType().FullName}");
        }
        if (_runtimeOptions?.Logger is { } logger)
        {
            DesktopLog.BrowserLaunchStalled(logger, attempt - 1, attempts, ConnectionTimeout.TotalSeconds);
        }
        else
        {
            Trace.TraceWarning($"{BrowserLaunchStalledCode}: {message}");
        }
    }

    // A surface-hosted presentation leaves the started surface to its owner,
    // which may open another presentation on it.
    private Task CloseFailedPresentationAsync(bool surfaceHosted) =>
        surfaceHosted ? ClosePresentationAsync() : CloseCoreAsync(CancellationToken.None);

    private string DescribeConnectionTimeout(
        Process? browserProcess, int attempt = 1, int attempts = 1, bool exitedBeforeRelaunch = false)
    {
        var otherRequests = Volatile.Read(ref _otherRequests);
        var progress = (ConnectionProgress)Volatile.Read(ref _connectionProgress) switch
        {
            // The server cannot tell a page that stopped after its first request from another program's request.
            ConnectionProgress.NoRequest when otherRequests > 0 => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"no request with the window's cookie or launch code reached the server, {otherRequests} without them did, the first {Volatile.Read(ref _firstOtherRequest)} (the page's first request if it loaded nothing after it, such as {BridgePath}, or another program's)"),
            ConnectionProgress.NoRequest => "no request reached the server",
            ConnectionProgress.DocumentRequested => $"the page was requested, but not {BridgePath}",
            ConnectionProgress.BridgeRequested => "the Bridge script was requested, but no WebSocket opened",
            ConnectionProgress.WebSocketRejected => "the Bridge WebSocket was rejected",
            _ => "the Bridge WebSocket opened, but authentication did not complete",
        };
        var presentation = browserProcess is null ? "The embedded WebView" : "The browser";
        var process = string.Empty;
        if (browserProcess is not null)
        {
            try
            {
                process = browserProcess.HasExited
                    ? $"; the browser process exited with code {browserProcess.ExitCode}"
                    : "; the browser process is still running";
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
        if (exitedBeforeRelaunch && process.Length == 0)
        {
            process = "; the browser process exited before it could be relaunched";
        }
        var attemptText = attempts > 1 ? $" on launch attempt {attempt} of {attempts}" : string.Empty;
        var deadline = Volatile.Read(ref _connectionDeadline);
        var timeout = deadline?.Timeout ?? ConnectionTimeout;
        if (deadline?.Expired is { Progress: { } extendedBy } expired)
        {
            // Before any request, the WebView's progress extended the timeout (I-176).
            var limit = expired.AtCap
                ? $"its limit of {ConnectionDeadline.CapFactor} times the timeout"
                : $"{expired.Elapsed.TotalSeconds:0.#} seconds";
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{presentation} did not connect within {expired.Elapsed.TotalSeconds:0.#} seconds{attemptText}: {progress}{process}, although the WebView was still making progress ({extendedBy}); the {timeout.TotalSeconds:0.###}-second connection timeout was extended to {limit}. {HandshakeLog.Describe()}");
        }
        // With a request, the timeout counted from the first one (WaitWithinConnectionDeadlineAsync).
        var since = deadline?.HasRequest == true ? " of its first request" : string.Empty;
        return $"{presentation} did not connect within {timeout.TotalSeconds:0.###} seconds{since}{attemptText}: {progress}{process}. {HandshakeLog.Describe()}";
    }

    private Task HandleRequestAsync(PresentationExchange context) => PrepareResponseAsync(context, DispatchRequestAsync);

    private Task DispatchRequestAsync(PresentationExchange context)
    {
        var path = context.Path;
        if (PresentationExchange.StartsWithSegments(path, BlobsPath, out var blobToken))
            return ServeBlobRequestAsync(context, blobToken.TrimStart('/'));
        if (IsPath(path, UploadPath))
            return ServeBlobRequestAsync(context, null);
        if (IsPath(path, BridgePath) && (context.IsGet || context.IsHead))
        {
            HandshakeLog.Record($"{BridgePath} requested");
            ReportConnectionProgress(context, ConnectionProgress.BridgeRequested);
            return ServeBridgeAsync(context);
        }
        if (IsPath(path, DesktopBootstrapPath) && (context.IsGet || context.IsHead))
        {
            HandshakeLog.Record($"{DesktopBootstrapPath} requested");
            ReportConnectionProgress(context, ConnectionProgress.BridgeRequested);
            return ServeDesktopBootstrapAsync(context);
        }
        if (IsPath(path, WebSocketPath) && context.IsGet)
        {
            return AcceptWebSocketAsync(context);
        }
        if (context.IsGet || context.IsHead)
        {
            // A frame's requests carry nothing that tells its page from another requester; its WebSocket does.
            if (_frameBinding.IsActive)
            {
            }
            else if (!IsFromPage(context))
            {
                ReportOtherRequest(context);
            }
            else
            {
                if (path == "/" || Volatile.Read(ref _connectionProgress) < (int)ConnectionProgress.DocumentRequested)
                {
                    HandshakeLog.Record($"{context.Method} {FrameLaunchBinding.Redact(context.Path)} requested");
                }
                ReportConnectionProgress(ConnectionProgress.DocumentRequested);
            }
            return ServeContentAsync(context);
        }

        context.StatusCode = PresentationStatus.MethodNotAllowed;
        return Task.CompletedTask;
    }

    private async Task ServeContentAsync(PresentationExchange context)
    {
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            _shutdown.Token);
        var cancellationToken = requestCancellation.Token;
        var path = GetDecodedPath(context.Path);
        var requestHandler = Volatile.Read(ref _requestHandler);
        if (requestHandler is not null)
        {
            var response = await requestHandler(context, path, cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                await SendVirtualContentAsync(context, path, response, ContentSecurityPolicy, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        var handler = Volatile.Read(ref _fileHandler);
        if (handler is not null)
        {
            var virtualContent = await handler(path, cancellationToken).ConfigureAwait(false);
            if (virtualContent is not null)
            {
                await SendVirtualContentAsync(context, path, virtualContent, ContentSecurityPolicy, cancellationToken).ConfigureAwait(false);
                return;
            }

            var virtualIndex = await FindVirtualIndexAsync(handler, path, cancellationToken).ConfigureAwait(false);
            if (virtualIndex is not null)
            {
                Redirect(context, virtualIndex);
                return;
            }
        }

        if (path == "/")
        {
            if (_embeddedHtml is not null)
            {
                AddContentSecurityPolicy(context, ContentSecurityPolicy);
                await SendTextAsync(context, _embeddedHtml, "text/html; charset=utf-8").ConfigureAwait(false);
                return;
            }

            if (_externalUrl is not null)
            {
                var escapedUrl = WebUtility.HtmlEncode(_externalUrl.AbsoluteUri);
                await SendTextAsync(
                    context,
                    $"<html><head><meta http-equiv=\"refresh\" content=\"0;url={escapedUrl}\"></head></html>",
                    "text/html; charset=utf-8").ConfigureAwait(false);
                return;
            }
        }

        var localPath = ResolveLocalPath(path);
        if (localPath is not null && File.Exists(localPath))
        {
            await SendFileAsync(context, localPath, path, ContentSecurityPolicy).ConfigureAwait(false);
            return;
        }

        if (localPath is not null && Directory.Exists(localPath) && path is not "/favicon.ico" and not "/favicon.svg")
        {
            var index = FindPhysicalIndex(path, localPath);
            if (index is not null)
            {
                Redirect(context, index);
                return;
            }
        }

        // The application's resources serve what the content does not, such as icons from a cache, in a window and
        // in a browser alike.
        var resourceHandler = Volatile.Read(ref _resourceHandler);
        if (resourceHandler is not null)
        {
            var resource = await resourceHandler(context, path, cancellationToken).ConfigureAwait(false);
            if (resource is not null)
            {
                await SendVirtualContentAsync(context, path, resource, ContentSecurityPolicy, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (path == "/favicon.ico")
        {
            Redirect(context, "/favicon.svg");
            return;
        }

        if (path == "/favicon.svg")
        {
            await SendTextAsync(context, DefaultIcon, "image/svg+xml").ConfigureAwait(false);
            return;
        }

        await SendNotFoundAsync(context).ConfigureAwait(false);
    }

    // blobs/1 (specs/application/README.md, "Blobs and uploads"). A blob URL is a capability that works only for this
    // window's page: with client cookies, the request must carry the cookie of a client this window served (and, for a
    // launched browser, the client bound to its launch code), and fetch metadata must not name another site. An upload
    // must also come from an allowed origin with a POST; the Bridge transport then checks its connection's token.
    private async Task ServeBlobRequestAsync(PresentationExchange context, string? token)
    {
        var handler = Volatile.Read(ref _blobHandler);
        var upload = token is null;
        if (handler is null || !upload && token!.Contains('/'))
        {
            context.SetResponseHeader("Cache-Control", NoCache);
            context.StatusCode = PresentationStatus.NotFound;
            return;
        }
        var origin = context.GetRequestHeader("Origin");
        // region-component/1: the component's page is the host page, another origin, so its requests are cross-origin.
        var component = _componentBinding.Embedder;
        if (upload && component is not null && string.Equals(context.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            ServeUploadPreflight(context, component, origin);
            return;
        }
        if (upload ? !string.Equals(context.Method, "POST", StringComparison.OrdinalIgnoreCase) : !(context.IsGet || context.IsHead))
        {
            context.StatusCode = PresentationStatus.MethodNotAllowed;
            return;
        }
        // A blob may be embedded by the window's own pages (same-origin) and fetched by them; an upload is a POST that
        // a browser always sends with Origin, which must be the surface's or an additional origin of its policy.
        var crossSite = context.GetRequestHeader("Sec-Fetch-Site").Equals("cross-site", StringComparison.OrdinalIgnoreCase);
        var deniedOrigin = component is not null
            // A component's blob may be embedded by the host page, whose no-cors request (an <img>) carries no Origin and
            // may be cross-site: its URL is the capability. A request that names an origin must name the embedder. An
            // upload always names it.
            ? upload ? !string.Equals(origin, component, StringComparison.Ordinal)
                : origin.Length != 0 && !string.Equals(origin, component, StringComparison.Ordinal)
            : upload
                ? _securityPolicy is not null && (origin.Length == 0 || !AllowsPageOrigin(origin))
                : crossSite;
        if (component is not null && !deniedOrigin)
        {
            context.SetResponseHeader("Vary", "Origin");
            context.SetResponseHeader("Cross-Origin-Resource-Policy", "cross-origin");
            if (origin.Length != 0)
            {
                context.SetResponseHeader("Access-Control-Allow-Origin", origin);
                // The client sends uploads with credentials; the surface sets no cookie in region mode.
                if (upload) context.SetResponseHeader("Access-Control-Allow-Credentials", "true");
            }
        }
        var deniedClient = UsesClientCookies && !IsFromPage(context);
        if (deniedOrigin || deniedClient)
        {
            _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
                deniedOrigin ? DesktopErrorCategory.OriginDenied : DesktopErrorCategory.AuthenticationDenied,
                upload ? "upload-not-admitted" : "blob-not-admitted",
                upload ? "An upload came from another origin or client than this window's page."
                    : "A blob was requested by another site or client than this window's page.",
                Retryable: false));
            context.StatusCode = PresentationStatus.Forbidden;
            return;
        }
        await handler(context, token).ConfigureAwait(false);
    }

    // region-component/1: the CORS preflight of a component's upload (its custom headers need one), granted to the
    // embedder alone. The upload itself still needs its connection's upload token.
    private void ServeUploadPreflight(PresentationExchange context, string embedder, string origin)
    {
        context.SetResponseHeader("Vary", "Origin");
        if (!string.Equals(origin, embedder, StringComparison.Ordinal) ||
            !string.Equals(context.GetRequestHeader("Access-Control-Request-Method"), "POST", StringComparison.Ordinal))
        {
            _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
                DesktopErrorCategory.OriginDenied,
                "upload-not-admitted",
                "An upload's preflight came from another origin than the host page that embeds this window's component.",
                Retryable: false));
            context.StatusCode = PresentationStatus.Forbidden;
            return;
        }
        context.SetResponseHeader("Access-Control-Allow-Origin", origin);
        context.SetResponseHeader("Access-Control-Allow-Credentials", "true");
        context.SetResponseHeader("Access-Control-Allow-Methods", "POST");
        context.SetResponseHeader("Access-Control-Allow-Headers", "Content-Type, X-Runic-Upload-Token, X-Runic-Upload-Name");
        context.SetResponseHeader("Access-Control-Max-Age", "600");
        context.StatusCode = PresentationStatus.NoContent;
    }

    // region-component/1: the component build (ComponentPath) is served to the embedder's document alone, with CORS for
    // exactly that origin. A module script, a dynamic import, a font and a CORS fetch always send Origin; the embedder's
    // document has no cookie of this surface, so the client cookie and the single-client rule do not apply. The files
    // carry no credential: the component gets its token from the host page (TC3). Pages are not served here, and a
    // request without the embedder's Origin is refused, so another site gets no CORS grant and no bootstrap script.
    private Task ServeComponentAsync(PresentationExchange context)
    {
        if (!(context.IsGet || context.IsHead))
        {
            context.StatusCode = PresentationStatus.MethodNotAllowed;
            return Task.CompletedTask;
        }
        var embedder = Volatile.Read(ref _componentEmbedder);
        var origin = context.GetRequestHeader("Origin");
        context.SetResponseHeader("Vary", "Origin");
        if (embedder is null || !Uri.TryCreate(origin, UriKind.Absolute, out var candidate) ||
            candidate.Scheme is not ("http" or "https") || candidate.PathAndQuery != "/" || candidate.UserInfo.Length != 0 ||
            !string.Equals(PresentationSecurityPolicy.CanonicalOrigin(candidate), embedder, StringComparison.Ordinal))
        {
            _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
                DesktopErrorCategory.OriginDenied,
                "component-origin-not-allowed",
                embedder is null
                    ? "The component build was requested while no host page embeds this surface's component."
                    : "The component build was requested by another origin than the host page that embeds it.",
                Retryable: false));
            context.StatusCode = PresentationStatus.Forbidden;
            return Task.CompletedTask;
        }
        var path = GetDecodedPath(context.Path);
        if (path.EndsWith('/') || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
        {
            context.StatusCode = PresentationStatus.NotFound;
            return Task.CompletedTask;
        }
        // The request's own serialization of the origin, which the browser compares byte for byte.
        context.SetResponseHeader("Access-Control-Allow-Origin", origin);
        context.SetResponseHeader("Cross-Origin-Resource-Policy", "cross-origin");
        return ServeContentAsync(context);
    }

    // Any page can embed a loopback script, and a classic script needs no CORS
    // grant to run. Fetch metadata identifies a cross-site embedding. Clients that
    // send none are not browser subresource loads and still need the WebSocket
    // origin and credential checks to open a session.
    private bool AdmitsBootstrapRequest(PresentationExchange context)
    {
        var destination = context.GetRequestHeader("Sec-Fetch-Dest");
        var site = context.GetRequestHeader("Sec-Fetch-Site");
        var admitted = destination.Length == 0 || destination.Equals("script", StringComparison.OrdinalIgnoreCase);
        admitted &= site.Length == 0 ||
            site.Equals("same-origin", StringComparison.OrdinalIgnoreCase) ||
            site.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            _securityPolicy?.AllowsAdditionalOrigin(context.GetRequestHeader("Referer")) == true;
        if (!admitted)
        {
            _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
                DesktopErrorCategory.OriginDenied,
                "bootstrap-origin-not-allowed",
                "The bootstrap script was requested by a document whose origin is not allowed.",
                Retryable: false));
            context.StatusCode = PresentationStatus.Forbidden;
            return false;
        }
        // In an installed browser the bootstrap carries the session credential: only the launched browser gets it.
        if (ChecksLaunchBinding && !_launchBinding.Admits(RequestClient(context)))
        {
            RejectUnboundClient(context, "bootstrap");
            return false;
        }
        return true;
    }

    // Each page load requests its bootstrap scripts, so each is built once for the inputs that can change between
    // presentations and served from its UTF-8 bytes. The response keeps PrepareResponseAsync's no-store: in an installed
    // browser the script carries the token and the session credential, which must not outlive the session in a disk
    // cache, and in an embedded window it names the port of this run, which a WebView profile would keep.
    private Task ServeBridgeAsync(PresentationExchange context)
    {
        if (!AdmitsBootstrapRequest(context))
        {
            return Task.CompletedTask;
        }

        return SendScriptAsync(context, GetBootstrapScript(ref _bridgeScript, BuildBridgeScript));
    }

    private string BuildBridgeScript(BootstrapInputs inputs)
    {
        var (token, sessionCredential) = GetBootstrapCredentialExpressions(inputs);
        return WebUiBridge.Script
            .Replace("__TOKEN__", token, StringComparison.Ordinal)
            .Replace("__PORT__", AsciiNumbers.Format(inputs.Port), StringComparison.Ordinal)
            .Replace("__BASE_PATH__", inputs.PathBase, StringComparison.Ordinal)
            .Replace("__SESSION_CREDENTIAL__", sessionCredential, StringComparison.Ordinal)
            .Replace("__CUSTOM_WINDOW_DRAG__", inputs.CustomWindowDrag ? "true" : "false", StringComparison.Ordinal)
            .Replace("__FRAME__", inputs.Frame ? "true" : "false", StringComparison.Ordinal)
            .Replace("__MEASURE_HEIGHT__", inputs.MeasuresHeight ? "true" : "false", StringComparison.Ordinal);
    }

    private Task ServeDesktopBootstrapAsync(PresentationExchange context)
    {
        if (!AdmitsBootstrapRequest(context))
        {
            return Task.CompletedTask;
        }

        return SendScriptAsync(context, GetBootstrapScript(ref _desktopBootstrapScript, BuildDesktopBootstrapScript));
    }

    private string BuildDesktopBootstrapScript(BootstrapInputs inputs)
    {
        var webSocketPath = $"{inputs.PathBase}{WebSocketPath}";
        var (token, sessionCredential) = GetBootstrapCredentialExpressions(inputs);
        return $$"""
            (() => {
              "use strict";
              const endpoint = new URL({{EncodeJavaScriptString(webSocketPath)}}, globalThis.location.href);
              endpoint.protocol = endpoint.protocol === "https:" ? "wss:" : "ws:";
              endpoint.port = {{AsciiNumbers.Format(inputs.Port)}};
              Object.defineProperty(globalThis, "runicDesktop", {
                configurable: false,
                enumerable: true,
                value: Object.freeze({
                  product: "Runic Desktop",
                  profile: "webui-compat/52f9e75",
                  endpoint: endpoint.href,
                  token: {{token}},
                  sessionCredential: {{sessionCredential}}
                })
              });
            })();
            """;
    }

    private static Task SendScriptAsync(PresentationExchange context, byte[] script)
    {
        context.ContentType = "text/javascript; charset=utf-8";
        context.ContentLength = script.Length;
        return context.IsHead
            ? Task.CompletedTask
            : context.ResponseBody.WriteAsync(script, context.RequestAborted).AsTask();
    }

    // The inputs of a bootstrap script that can change between presentations of one window: the server's port and path,
    // whether the embedded host hands the credentials to the document, the frameless window's own drag, and whether a
    // host page frames the surface (region mode), whose page offers its frame token instead of the session credential.
    private readonly record struct BootstrapInputs(int Port, string PathBase, bool DocumentStartScript, bool CustomWindowDrag,
        bool Frame, bool MeasuresHeight);

    private sealed record BootstrapScript(BootstrapInputs Inputs, byte[] Utf8);

    private byte[] GetBootstrapScript(ref BootstrapScript? cache, Func<BootstrapInputs, string> build)
    {
        var embeddedHost = _embeddedHost;
        // A frame's page connects to the listener that serves it.
        var frameListener = _frameBinding.Listener;
        var inputs = new BootstrapInputs(
            frameListener?.Port ?? Url?.Port ?? 0,
            _surface?.PathBase ?? string.Empty,
            embeddedHost?.SupportsDocumentStartScript == true,
            embeddedHost is not null && _frameless,
            frameListener is not null,
            frameListener is not null && _frameBinding.MeasuresHeight);
        var cached = Volatile.Read(ref cache);
        if (cached is not null && cached.Inputs == inputs) return cached.Utf8;
        var built = new BootstrapScript(inputs, Encoding.UTF8.GetBytes(build(inputs)));
        Volatile.Write(ref cache, built);
        return built.Utf8;
    }

    private static string EncodeJavaScriptString(string value) =>
        $"\"{JavaScriptEncoder.Default.Encode(value)}\"";

    // A host that runs a document-start script hands the credentials only to
    // documents from admitted origins. Fetchable scripts then read them from the
    // document instead of carrying them to every requester. A frame's bootstrap
    // carries no credential at all: its page offers the frame token it received.
    private (string Token, string SessionCredential) GetBootstrapCredentialExpressions(BootstrapInputs inputs) =>
        inputs.Frame ? (AsciiNumbers.Format(_token), FrameTokenExpression(inputs.PathBase))
        : inputs.DocumentStartScript
            ? ($"({DocumentBootstrapProperty}?.token ?? 0)", $"({DocumentBootstrapProperty}?.sessionCredential ?? \"\")")
            : (AsciiNumbers.Format(_token), EncodeJavaScriptString(_sessionCredential));

    // Region mode (FrameLaunchBinding): the frame token, read from the #runic-frame= fragment of the frame launch's
    // redirect before any page script runs and removed from the address at once, or from sessionStorage after a reload
    // of the frame. Both bootstrap scripts read it through one page-global, so the second finds what the first removed
    // even where the storage is blocked. Empty without a token: the page is then not admitted.
    private static string FrameTokenExpression(string pathBase) => $$"""
        (() => {
          const shared = Symbol.for("runic-desktop.frame");
          if (typeof globalThis[shared] === "string") return globalThis[shared];
          const key = {{EncodeJavaScriptString("runic-frame:" + pathBase)}};
          const match = /^#{{FrameLaunchBinding.FragmentName}}=([A-Za-z0-9_-]{43})$/.exec(globalThis.location.hash);
          let token = "";
          if (match) {
            token = match[1];
            try { globalThis.history.replaceState(globalThis.history.state, "", globalThis.location.pathname + globalThis.location.search); } catch {}
            try { globalThis.sessionStorage.setItem(key, token); } catch {}
          } else {
            try { token = globalThis.sessionStorage.getItem(key) ?? ""; } catch {}
          }
          Object.defineProperty(globalThis, shared, { value: token });
          return token;
        })()
        """;

    private string FrameAncestorsPolicy => _frameAncestorsPolicy ??= CreateFrameAncestorsPolicy(_securityPolicy?.AdditionalOrigins);

    // The origin of the page: in region mode the frame's (the listener that serves it) alone, without additional
    // origins; otherwise the surface's and its policy's additional origins.
    // A component's page is the host page that loads it: its origin is the embedder's, exactly as the attach named it.
    private bool AllowsPageOrigin(string origin) =>
        _frameBinding.IsActive
            ? _componentBinding.Embedder is { } embedder
                ? string.Equals(embedder, origin, StringComparison.Ordinal)
                // As browsers serialize it: http://[::1]:40117 (CanonicalOrigin's IdnHost drops an IPv6 literal's brackets).
                : _frameBinding.Listener is { } frame &&
                  string.Equals(frame.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase)
            : _securityPolicy?.AllowsOrigin(Url!, origin) != false;

    // The additional origins are canonical (scheme://host:port), which is a valid CSP host source.
    internal static string CreateFrameAncestorsPolicy(IEnumerable<string>? additionalOrigins) =>
        string.Join(' ', (additionalOrigins ?? []).Order(StringComparer.Ordinal).Prepend("frame-ancestors 'self'"));

    private string CreateDocumentStartScript() => CreateDocumentStartScript(_sessionCredential);

    private string CreateDocumentStartScript(string sessionCredential)
    {
        var url = Url ?? throw new InvalidOperationException("The surface must be started before opening a presentation.");
        var origins = (_securityPolicy?.AdditionalOrigins ?? Enumerable.Empty<string>())
            .Select(static origin => new Uri(origin, UriKind.Absolute))
            .Prepend(url)
            .Select(static origin => origin.IsDefaultPort
                ? $"{origin.Scheme}://{origin.IdnHost}"
                : $"{origin.Scheme}://{origin.IdnHost}:{AsciiNumbers.Format(origin.Port)}")
            .Distinct(StringComparer.Ordinal)
            .Select(EncodeJavaScriptString);
        // SEC-05: a frame receives the credentials only when the document and every document that contains it have an
        // admitted origin, so an external page cannot frame the surface to connect. location.ancestorOrigins is in
        // WebKit and Chromium; where it is missing, a top document whose origin cannot be read is foreign.
        return $$"""
            (() => {
              "use strict";
              const admitted = [{{string.Join(", ", origins)}}];
              if (!admitted.includes(globalThis.location.origin)) return;
              if (globalThis.top !== globalThis) {
                const ancestors = globalThis.location.ancestorOrigins;
                if (ancestors) {
                  for (let index = 0; index < ancestors.length; index++) {
                    if (!admitted.includes(ancestors[index])) return;
                  }
                } else {
                  let top;
                  try { top = globalThis.top.location.origin; } catch { return; }
                  if (!admitted.includes(top)) return;
                }
              }
              Object.defineProperty(globalThis, Symbol.for("runic-desktop.bootstrap"), {
                value: Object.freeze({
                  token: {{AsciiNumbers.Format(_token)}},
                  sessionCredential: {{EncodeJavaScriptString(sessionCredential)}}
                })
              });
            })();
            """;
    }

    private async Task AcceptWebSocketAsync(PresentationExchange context)
    {
        if (!context.IsWebSocketRequest)
        {
            ReportConnectionProgress(context, ConnectionProgress.WebSocketRejected);
            HandshakeLog.RecordRejection("not a WebSocket upgrade");
            context.StatusCode = PresentationStatus.UpgradeRequired;
            return;
        }

        // Region mode: the attachment whose token the upgrade offers, checked again once its session is listed.
        var frameGeneration = _frameBinding.Generation;
        var componentGeneration = _componentBinding.Generation;
        var acceptedSubprotocol = ValidateWebSocketAdmission(context);
        if (acceptedSubprotocol is null)
        {
            ReportConnectionProgress(context, ConnectionProgress.WebSocketRejected);
            HandshakeLog.RecordRejection(context.StatusCode == PresentationStatus.Unauthorized
                ? "the session credential was missing"
                : "the origin is not allowed");
            return;
        }

        if (ChecksLaunchBinding && !_launchBinding.Admits(FindClient(context)))
        {
            ReportOtherRequest(context);
            HandshakeLog.RecordRejection("the client is not the browser this window launched");
            RejectUnboundClient(context, "WebSocket");
            return;
        }

        // The slot follows from the credential the WebSocket offered (PanelConnectionSlot).
        var slot = SlotOf(acceptedSubprotocol, out var panelGeneration);
        if (slot is null)
        {
            HandshakeLog.RecordRejection("the side panel closed");
            context.StatusCode = PresentationStatus.Unauthorized;
            return;
        }

        bool admitted;
        lock (_connectionGate)
        {
            // One connection per slot: the page's slot as before, and the side panel's for its own credential.
            if (slot == DesktopConnectionSlot.SidePanel)
            {
                admitted = _activePanelConnections == 0
                    || YieldSilentConnections(_activePanelConnections, SessionsOf(DesktopConnectionSlot.SidePanel));
                if (admitted) _activePanelConnections++;
            }
            else
            {
                admitted = AllowMultipleClients || _activeConnections == 0
                    || YieldSilentConnections(_activeConnections, SessionsOf(DesktopConnectionSlot.Page));
                if (admitted) _activeConnections++;
            }
        }

        // A WebSocket that offered the session credential came from a page that ran this window's bootstrap.
        var credentialed = acceptedSubprotocol.Length != 0;
        if (!admitted)
        {
            ReportConnectionProgress(context, ConnectionProgress.WebSocketRejected, credentialed);
            HandshakeLog.RecordRejection(slot == DesktopConnectionSlot.SidePanel
                ? "another WebSocket holds the side panel's connection"
                : "another WebSocket holds the only connection");
            context.StatusCode = PresentationStatus.Forbidden;
            return;
        }

        try
        {
            var fromPage = credentialed || IsFromPage(context);
            var clientId = GetOrCreateClient(context);
            using var socket = await context.AcceptWebSocketAsync(
                acceptedSubprotocol.Length == 0 ? null : acceptedSubprotocol).ConfigureAwait(false);
            ReportConnectionProgress(context, ConnectionProgress.WebSocketOpened, fromPage);
            var connectionId = checked((nuint)Interlocked.Increment(ref _nextConnectionId));
            HandshakeLog.Record($"WebSocket {connectionId} opened");
            var session = new WebUiSession(
                this,
                socket,
                clientId == 0 ? connectionId : clientId,
                connectionId,
                context.GetRequestHeader("Cookie"),
                WebUiApplication.BridgeHandshakeTimeout,
                TimeProvider)
            {
                Slot = slot.Value,
                Origin = _securityPolicy is null ? string.Empty : context.GetRequestHeader("Origin"),
                // A component cannot read this window's packet token, which only its served bootstrap carries: the
                // component token on the upgrade admitted it instead (ComponentBinding).
                AdmittedByComponentToken = _componentBinding.Embedder is not null &&
                    string.Equals(acceptedSubprotocol, _componentBinding.ExpectedProtocol, StringComparison.Ordinal),
            };
            if (!_sessions.TryAdd(session.Id, session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return;
            }
            // A detach, a new attach or a launch conflict that came between the token check and now disconnected the
            // pages it found; this one was not listed yet.
            if (_frameBinding.IsActive &&
                (_frameBinding.Generation != frameGeneration || _componentBinding.Generation != componentGeneration))
            {
                _sessions.TryRemove(session.Id, out _);
                await session.DisposeAsync().ConfigureAwait(false);
                return;
            }
            // The same for the side panel: a close or a new open since the credential check revoked this page's admission.
            if (slot == DesktopConnectionSlot.SidePanel && !_panelSlot.Holds(panelGeneration))
            {
                _sessions.TryRemove(session.Id, out _);
                await session.DisposeAsync().ConfigureAwait(false);
                return;
            }

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _shutdown.Token);
            try
            {
                await session.RunAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (WebSocketException)
            {
            }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
            {
            }
            finally
            {
                _sessions.TryRemove(session.Id, out _);
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_connectionGate)
            {
                if (slot == DesktopConnectionSlot.SidePanel) _activePanelConnections--;
                else _activeConnections--;
            }
        }
    }

    // Called under the connection gate when a new WebSocket finds the only
    // connection taken. A socket that never checked its token, such as one from a
    // replaced page, gives the connection up, and so does one whose token check
    // failed or that ended before its check. An authenticated socket keeps it, and so
    // does one still being accepted or removed, which the table no longer lists. A
    // silent socket is closed only when that frees the connection.
    // Returns whether the connection is now free.
    internal static bool YieldSilentConnections(int activeConnections, IEnumerable<WebUiSession> sessions)
    {
        var holding = activeConnections;
        List<WebUiSession> silent = [];
        foreach (var session in sessions)
        {
            if (session.HasGivenUpConnection)
            {
                holding--;
            }
            else if (session.CanYieldSilentHandshake)
            {
                silent.Add(session);
            }
        }
        if (holding - silent.Count > 0)
        {
            return false;
        }
        foreach (var session in silent)
        {
            // A token check that arrived meanwhile keeps its socket.
            if (session.TryYieldSilentHandshake() || session.HasGivenUpConnection)
            {
                holding--;
            }
        }
        return holding <= 0;
    }

    private async Task PrepareResponseAsync(PresentationExchange context, Func<PresentationExchange, Task> next)
    {
        context.SetResponseHeader("Cache-Control", NoCache);
        context.SetResponseHeader("X-Content-Type-Options", "nosniff");
        var region = _frameBinding.IsActive;
        if (region)
        {
            // Region mode: only the host page's origin, which the host named over its authenticated channel, may frame
            // the page, and none before the first attach. No X-Frame-Options, which would refuse that framer where
            // frame-ancestors is not applied (CSP 3 makes browsers ignore it next to frame-ancestors). No Referer
            // leaves the frame's page.
            context.SetResponseHeader(ContentSecurityPolicyHeader, "frame-ancestors " + (_frameBinding.Embedder ?? "'none'"));
            context.SetResponseHeader("Referrer-Policy", "no-referrer");
        }
        else
        {
            // SEC-05: only the surface's own documents and its additional origins may frame its pages. Browsers that
            // apply frame-ancestors ignore X-Frame-Options; it covers those that do not.
            context.SetResponseHeader(ContentSecurityPolicyHeader, FrameAncestorsPolicy);
            context.SetResponseHeader("X-Frame-Options", "SAMEORIGIN");
        }
        if ((_allowedPermissions & DesktopPermissionGrant.MediaCapture) == 0)
        {
            context.SetResponseHeader("Permissions-Policy", "camera=(), microphone=()");
        }
        // A frame launch code of a surface in region mode is answered on every listener, a dead one with 403
        // (host-protocol.md, Limits).
        if (region && !context.IsWebSocketRequest &&
            PresentationExchange.StartsWithSegments(context.Path, FrameLaunchBinding.PathSegment, out var frameCode))
        {
            await ServeFrameLaunchAsync(context, frameCode.TrimStart('/')).ConfigureAwait(false);
            return;
        }
        // The IPv6 loopback listener serves only the frame of a surface in region mode.
        if (!ServesListener(context.Listener))
        {
            context.StatusCode = PresentationStatus.NotFound;
            return;
        }
        if (!context.IsWebSocketRequest && PresentationExchange.StartsWithSegments(context.Path, ComponentPath, out _))
        {
            // Before the client cookie: the host page's document loads the component, with no cookie of this surface.
            await ServeComponentAsync(context).ConfigureAwait(false);
            return;
        }
        if (region)
        {
            // No client cookie: a frame's page proves itself with its frame token on the WebSocket alone.
            await next(context).ConfigureAwait(false);
            return;
        }
        if (!context.IsWebSocketRequest)
        {
            var clientId = GetOrCreateClient(context);
            context.ClientId = clientId;
            // Before the single-client check: the launched browser must reach its conflict check even when another
            // client already holds the connection.
            if (PresentationExchange.StartsWithSegments(context.Path, BrowserLaunchBinding.PathSegment, out var code))
            {
                await ServeBrowserLaunchAsync(context, clientId, code.TrimStart('/')).ConfigureAwait(false);
                return;
            }
            if (UseClientCookies &&
                !AllowMultipleClients &&
                _sessions.Values.Any(session => session.ClientId != clientId))
            {
                context.StatusCode = PresentationStatus.Forbidden;
                await SendTextAsync(context, AccessDenied, "text/html; charset=utf-8").ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }

    // The installed browser was launched with a one-time code: bind its client, or detect that another client
    // used the code first (SEC-01).
    private async Task ServeBrowserLaunchAsync(PresentationExchange context, nuint clientId, string code)
    {
        HandshakeLog.Record($"{BrowserLaunchBinding.PathSegment} requested");
        if (!context.IsGet)
        {
            context.StatusCode = PresentationStatus.MethodNotAllowed;
            return;
        }
        if (await RefuseForeignUserAsync(context, "browser-launch-foreign-user",
                "Another user's process opened the browser launch address and was refused.").ConfigureAwait(false))
        {
            return;
        }

        switch (_launchBinding.Consume(code, clientId, out var target))
        {
            case BrowserLaunchBinding.Outcome.Bound or BrowserLaunchBinding.Outcome.AlreadyBound:
                // The launch code proves the request came from the browser this window launched.
                ReportConnectionProgress(ConnectionProgress.DocumentRequested);
                context.StatusCode = PresentationStatus.SeeOther;
                context.SetResponseHeader("Location", target!.AbsoluteUri);
                return;
            case BrowserLaunchBinding.Outcome.Conflict:
                HandshakeLog.RecordRejection("another client used the launch code first");
                RevokeLaunchedSessions();
                break;
            default:
                HandshakeLog.RecordRejection("the launch code is not valid");
                break;
        }
        context.StatusCode = PresentationStatus.Forbidden;
        await SendTextAsync(context, LaunchRefused, "text/html; charset=utf-8").ConfigureAwait(false);
    }

    // On Linux, a launch code that another user's process presents is refused without spending it: other users can read
    // a browser's command line, and a loopback listener is reachable by every user of the machine.
    private async Task<bool> RefuseForeignUserAsync(PresentationExchange context, string code, string message)
    {
        if (!OperatingSystem.IsLinux() ||
            context.RemoteIpAddress is not { } remoteAddress ||
            context.LocalIpAddress is not { } localAddress ||
            LoopbackPeerOwner.Find(remoteAddress, context.RemotePort, localAddress, context.LocalPort) is not { } owner ||
            owner == LoopbackPeerOwner.CurrentUser)
        {
            return false;
        }
        HandshakeLog.RecordRejection("another user opened the launch code");
        _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
            DesktopErrorCategory.AuthenticationDenied, code, message, Retryable: false));
        context.StatusCode = PresentationStatus.Forbidden;
        await SendTextAsync(context, LaunchRefused, "text/html; charset=utf-8").ConfigureAwait(false);
        return true;
    }

    // Region mode: the host page's frame opened its one-time frame launch address. The first request is redirected to
    // the page with the frame token in the fragment; the consumed code presented again means that someone else used it
    // (FrameLaunchBinding): the token is revoked and the page disconnected, so nobody keeps the page.
    private async Task ServeFrameLaunchAsync(PresentationExchange context, string code)
    {
        HandshakeLog.Record($"{FrameLaunchBinding.PathSegment} requested");
        if (!context.IsGet)
        {
            context.StatusCode = PresentationStatus.MethodNotAllowed;
            return;
        }
        if (await RefuseForeignUserAsync(context, "frame-launch-foreign-user",
                "Another user's process opened the frame launch address and was refused.").ConfigureAwait(false))
        {
            return;
        }
        // The code belongs to the listener the host was given; it is not spent elsewhere, and after a detach it is dead.
        string? token = null;
        Uri? target = null;
        var outcome = SameAuthority(context.Listener ?? Url, _frameBinding.Listener)
            ? _frameBinding.Consume(code, TimeProvider, out token, out target)
            : FrameLaunchBinding.Outcome.Refused;
        switch (outcome)
        {
            case FrameLaunchBinding.Outcome.Bound:
                ReportConnectionProgress(ConnectionProgress.DocumentRequested);
                context.StatusCode = PresentationStatus.SeeOther;
                context.SetResponseHeader("Location",
                    $"{target!.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)}#{FrameLaunchBinding.FragmentName}={token}");
                return;
            case FrameLaunchBinding.Outcome.Conflict:
                HandshakeLog.RecordRejection("another client used the frame launch code first");
                _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
                    DesktopErrorCategory.AuthenticationDenied,
                    "frame-launch-conflict",
                    "Another client opened this window's frame launch address. The frame's page was disconnected; attach the region again.",
                    Retryable: true));
                CloseSessions();
                FrameRevoked?.Invoke();
                break;
            default:
                HandshakeLog.RecordRejection("the frame launch code is not valid");
                break;
        }
        context.StatusCode = PresentationStatus.Forbidden;
        await SendTextAsync(context, LaunchRefused, "text/html; charset=utf-8").ConfigureAwait(false);
    }

    private void CloseSessions()
    {
        foreach (var session in _sessions.Values)
        {
            _ = CloseBridgeIgnoringFailureAsync(session, CancellationToken.None);
        }
    }

    // Whether this surface answers a request that arrived on listener: its own listener, and in region mode the frame's.
    private bool ServesListener(Uri? listener) =>
        listener is null || SameAuthority(listener, Url) || SameAuthority(listener, _frameBinding.Listener) ||
        SameAuthority(listener, _componentBinding.Listener);

    private static bool SameAuthority(Uri? left, Uri? right) =>
        left is not null && right is not null && string.Equals(left.Authority, right.Authority, StringComparison.OrdinalIgnoreCase);

    // Someone else used the launch code before the launched browser, or after it: nobody keeps the surface.
    private void RevokeLaunchedSessions()
    {
        const string message =
            "Another client opened this window's browser launch address. The window's sessions were closed; show the window again.";
        _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
            DesktopErrorCategory.AuthenticationDenied,
            "browser-launch-conflict",
            message,
            Retryable: true));
        // Only a show that waits for its connection observes the failure (StopBrowserCoreAsync).
        if (WaitForConnection && _externalUrl is null)
        {
            _browserConnected.TrySetException(new DesktopException(
                DesktopErrorCategory.AuthenticationDenied,
                "browser-launch-conflict",
                message,
                retryable: true));
        }
        foreach (var session in _sessions.Values)
        {
            _ = CloseBridgeIgnoringFailureAsync(session, CancellationToken.None);
        }
    }

    private void RejectUnboundClient(PresentationExchange context, string request)
    {
        _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
            DesktopErrorCategory.AuthenticationDenied,
            "client-not-bound",
            $"A {request} request came from a client other than the browser this window launched.",
            Retryable: false));
        context.StatusCode = PresentationStatus.Forbidden;
    }

    // runic-host/1 hosted mode: a host application's window loads this surface through a one-time launch code, as a
    // browser Runic launched does (BrowserLaunchBinding), so its page is admitted through the client cookie and the
    // surface's credential never leaves this process. A new launch replaces an earlier code.
    internal Uri BeginHostedLaunch()
    {
        var url = Url ?? throw new InvalidOperationException("The surface must be started before a hosted launch.");
        if (_embeddedHost is not null)
            throw new InvalidOperationException("This surface is already hosted by an embedded WebView.");
        if (!BindsBrowserLaunch)
            throw new InvalidOperationException(
                "A hosted launch needs the session credential, client cookies and a single client (DesktopSecurityPolicy).");
        return new Uri(url, $"{BrowserLaunchBinding.PathSegment.TrimStart('/')}/{_launchBinding.Begin(GetBrowserUrl(url))}");
    }

    // runic-host/1 region/1: the window shows in a region of a host page instead of a window of its own. Its window-mode
    // launch address, which its host never opens, admits nobody from now on, and until a region attaches no origin may
    // frame its page.
    internal void EnterRegionMode()
    {
        _frameBinding.Enter();
        _launchBinding.Revoke();
        // A region has no side panel (D062.8): its credential is revoked with every other page.
        _panelSlot.Close();
        CloseSessions();
    }

    // runic-host/1 region/1, frame admission: a new one-time frame launch address for a frame of the page whose origin
    // is embedder (FrameEmbedderHost). The frame is served from the loopback family that the embedder does not use, so
    // it is another site and receives none of the embedder's cookies: from the IPv6 loopback listener for 127.0.0.1 and
    // localhost, else from the surface's own IPv4 listener. Where [::1] cannot be bound, a localhost embedder's frame
    // comes from 127.0.0.1, also another site; a 127.0.0.1 embedder's falls back to the surface's listener, of the
    // embedder's site, only when allowSameSite (SameSite reports it), else DesktopFrameCrossSiteUnavailableException. A
    // new attachment replaces the earlier one, whose code and token are revoked and whose page is disconnected.
    internal async Task<DesktopFrameLaunch> BeginFrameLaunchAsync(string embedder, bool allowSameSite, bool measuresHeight,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        var url = Url ?? throw new InvalidOperationException("The surface must be started before a frame launch.");
        if (_embeddedHost is not null)
            throw new InvalidOperationException("This surface is already hosted by an embedded WebView.");
        if (_securityPolicy is not { RequireSessionCredential: true } || AllowMultipleClients || _host is null || _surface is null)
            throw new InvalidOperationException(
                "A frame launch needs the session credential and a single client on a hosted surface (DesktopSecurityPolicy).");
        if (_externalUrl is not null)
            throw new InvalidOperationException("A frame shows only content that the surface serves itself, not an external URL.");
        var host = FrameEmbedderHost(embedder)
            ?? throw new ArgumentException("The embedder must be an http origin of 127.0.0.1, [::1] or localhost with an explicit port.", nameof(embedder));
        // The earlier attachment ends first: its page must not survive into the new one.
        EndFrameLaunch();
        EndComponent();
        var listener = url;
        var sameSite = false;
        if (host != "[::1]")
        {
            if (await _host.GetIpv6LoopbackUrlAsync(_surface, cancellationToken).ConfigureAwait(false) is { } ipv6)
            {
                listener = ipv6;
            }
            else if (host == "127.0.0.1")
            {
                if (!allowSameSite)
                    throw new DesktopFrameCrossSiteUnavailableException(
                        "The IPv6 loopback address cannot be bound, so the frame cannot be served cross-site to its 127.0.0.1 host page.");
                sameSite = true;
                _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
                    DesktopErrorCategory.InvalidState,
                    "frame-same-site",
                    "The IPv6 loopback address cannot be bound, so the frame is served from its host page's site, and the host's web view sends it the cookies of the host page's origin.",
                    Retryable: false));
            }
        }
        var code = _frameBinding.Begin(GetBrowserUrl(listener), embedder, listener, measuresHeight, TimeProvider);
        return new DesktopFrameLaunch(new Uri(listener, $"{FrameLaunchBinding.PathSegment.TrimStart('/')}/{code}"), sameSite);
    }

    // Detaches: the code and token are revoked and the page is disconnected; the surface stays in region mode.
    internal void EndFrameLaunch()
    {
        if (_frameBinding.End()) CloseSessions();
    }

    // runic-host/1 region-component/1, component admission: a new component token for the host page whose origin is
    // embedder, which loads the surface's component build (ComponentPath) into its own document. The component is
    // served from the surface's own listener, whose origin a host page's Content-Security-Policy can name
    // (http://127.0.0.1:*; CSP host sources cannot name an IPv6 literal), unless that listener is of the embedder's own
    // site and the host did not allow the same site: then from the IPv6 loopback listener, else
    // DesktopFrameCrossSiteUnavailableException. A browser host's page (browser) runs in the user's browser, whose
    // 127.0.0.1 cookies other local applications set, so its component comes from the IPv6 loopback listener whatever its
    // host, and from 127.0.0.1 only for a [::1] page, as its frames do (D059.18). A new attachment replaces the earlier
    // one, frame or component, whose token is revoked and whose page is disconnected.
    internal async Task<DesktopComponentLaunch> BeginComponentAsync(string embedder, bool allowSameSite, bool browser,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        var url = Url ?? throw new InvalidOperationException("The surface must be started before a component attach.");
        if (_embeddedHost is not null)
            throw new InvalidOperationException("This surface is already hosted by an embedded WebView.");
        if (_securityPolicy is not { RequireSessionCredential: true } || AllowMultipleClients || _host is null || _surface is null)
            throw new InvalidOperationException(
                "A component attach needs the session credential and a single client on a hosted surface (DesktopSecurityPolicy).");
        if (_externalUrl is not null)
            throw new InvalidOperationException("A component is served only from content that the surface serves itself, not an external URL.");
        var host = FrameEmbedderHost(embedder)
            ?? throw new ArgumentException("The embedder must be an http origin of 127.0.0.1, [::1] or localhost with an explicit port.", nameof(embedder));
        // The earlier attachment ends first: its page must not survive into the new one.
        EndFrameLaunch();
        EndComponent();
        // Region mode first, with its window-mode launch revoked, as the guest's region window always is.
        if (!_frameBinding.IsActive) EnterRegionMode();
        var listener = url;
        // Hosts are compared as written: 127.0.0.1, [::1] and localhost are three sites.
        var sameSite = string.Equals(url.Host, host, StringComparison.OrdinalIgnoreCase);
        if (browser && host != "[::1]")
        {
            listener = await _host.GetIpv6LoopbackUrlAsync(_surface, cancellationToken).ConfigureAwait(false)
                ?? throw new DesktopFrameCrossSiteUnavailableException(
                    "The IPv6 loopback address cannot be bound, so the component cannot be served apart from the 127.0.0.1 cookies of the user's browser.");
            sameSite = false;
        }
        else if (sameSite && !allowSameSite)
        {
            listener = (url.Host == "127.0.0.1" ? await _host.GetIpv6LoopbackUrlAsync(_surface, cancellationToken).ConfigureAwait(false) : null)
                ?? throw new DesktopFrameCrossSiteUnavailableException(
                    "The IPv6 loopback address cannot be bound, so the component cannot be served cross-site to its host page, which did not allow its own site.");
            sameSite = false;
        }
        var token = _componentBinding.Begin(embedder, listener);
        SetComponentEmbedder(new Uri(embedder, UriKind.Absolute));
        return new DesktopComponentLaunch(listener, token, sameSite);
    }

    // Detaches a component: its token is revoked, its build is refused to every origin and its page is disconnected.
    internal void EndComponent()
    {
        if (!_componentBinding.End()) return;
        SetComponentEmbedder(null);
        CloseSessions();
    }

    // The base URL of the listener that the page of this surface uses: a region's attachment's, else the surface's.
    // Blob and upload addresses are made from it (blobs/1), so they have the origin the page requests them from.
    internal Uri? PageUrl => _componentBinding.Listener ?? _frameBinding.Listener ?? Url;

    // The host of a frame embedder in the form browsers serialize such an origin: http://, the host 127.0.0.1, [::1] or
    // localhost, an explicit port from 1 to 65535 without leading zeros, nothing after it, at most 256 characters
    // (runic-host/1 region-attach). Null for anything else.
    internal static string? FrameEmbedderHost(string embedder)
    {
        if (embedder.Length > 256 || !embedder.StartsWith("http://", StringComparison.Ordinal)) return null;
        var authority = embedder.AsSpan("http://".Length);
        var colon = authority.LastIndexOf(':');
        if (colon <= 0) return null;
        var host = authority[..colon].ToString();
        var port = authority[(colon + 1)..];
        if (host is not ("127.0.0.1" or "[::1]" or "localhost") || port.Length is 0 or > 5 || port[0] == '0') return null;
        foreach (var digit in port)
        {
            if (!char.IsAsciiDigit(digit)) return null;
        }
        return int.Parse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture) <= 65_535 ? host : null;
    }

    // Region mode: moves the keyboard focus into the frame's page, to its first field ("forward"), its last
    // ("backward") or the one it had last ("programmatic"); false when no page is connected.
    internal async Task<bool> FocusFrameAsync(string direction, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_frameBinding.Embedder is null || direction is not ("forward" or "backward" or "programmatic")) return false;
        var sessions = _sessions.Values.Where(static session => session.IsAuthenticated).ToArray();
        if (sessions.Length == 0) return false;
        await Task.WhenAll(sessions.Select(session => session.SendRegionAsync(direction, cancellationToken))).ConfigureAwait(false);
        return true;
    }

    // Region mode: what the frame's page reports of itself, its title and its content height (WebUiBridge's region
    // reports), from an admitted page only. Raised on the session's thread; a handler queues its work.
    internal event Action<DesktopFrameReport>? FrameReported;

    // Region mode: a launch conflict revoked the frame token (ServeFrameLaunchAsync). Raised on a request's thread.
    internal event Action? FrameRevoked;

    private WebUiResult ReportFrame(ReadOnlyMemory<byte> report)
    {
        if (_frameBinding.Embedder is null || FrameReported is not { } reported || report.Length > 8_192) return WebUiResult.None;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(report);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return WebUiResult.None;
            string? title = root.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == System.Text.Json.JsonValueKind.String
                ? titleValue.GetString()
                : null;
            int? height = root.TryGetProperty("height", out var heightValue) && heightValue.TryGetDouble(out var measured) &&
                double.IsFinite(measured) && measured >= 0
                ? (int)Math.Min(Math.Ceiling(measured), 32_767)
                : null;
            if (title is not null || height is not null) reported(new DesktopFrameReport(title, height));
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return WebUiResult.None;
    }

    // Launch binding needs the client cookie, a credential to protect and a single client.
    private bool BindsBrowserLaunch =>
        _securityPolicy is { RequireSessionCredential: true } && !AllowMultipleClients && UseClientCookies;

    // Only while the surface is not in an embedded host, which has no access to the browser's cookie and
    // receives the credential from its document-start script instead.
    // In region mode the frame binding admits the page instead.
    private bool ChecksLaunchBinding => _embeddedHost is null && _launchBinding.IsActive && !_frameBinding.IsActive;

    // Client cookies identify the page, except in region mode: a frame uses none (FrameLaunchBinding).
    private bool UsesClientCookies => UseClientCookies && !_frameBinding.IsActive;

    // The client of a request that PrepareResponseAsync identified.
    private static nuint RequestClient(PresentationExchange context) => context.ClientId;

    // The client a WebSocket upgrade's cookie names, without creating one.
    private nuint FindClient(PresentationExchange context) =>
        UsesClientCookies && context.GetRequestCookie(AuthCookieName) is { } cookie && _clients.TryGetValue(cookie, out var clientId)
            ? clientId
            : 0;

    private nuint GetOrCreateClient(PresentationExchange context)
    {
        if (!UsesClientCookies)
        {
            return 0;
        }

        if (context.GetRequestCookie(AuthCookieName) is { } existing &&
            _clients.TryGetValue(existing, out var clientId))
        {
            return clientId;
        }

        var cookie = Convert.ToHexStringLower(OsRandom.GetBytes(16));
        clientId = checked((nuint)Interlocked.Increment(ref _nextClientId));
        _clients[cookie] = clientId;
        context.AppendResponseHeader("Set-Cookie", PresentationCookies.StrictHttpOnly(AuthCookieName, cookie, $"{_surface?.PathBase}/"));
        return clientId;
    }

    // The admitted Origin of the page connected as connectionId (DesktopConnectionEvent.SessionId), or null when no such
    // page is connected or its upgrade carried none.
    internal string? ConnectionOrigin(ulong connectionId)
    {
        foreach (var session in _sessions.Values)
        {
            if ((ulong)session.ConnectionId == connectionId) return session.Origin.Length == 0 ? null : session.Origin;
        }
        return null;
    }

    private string? ValidateWebSocketAdmission(PresentationExchange context)
    {
        if (_securityPolicy is null)
        {
            return string.Empty;
        }

        if (!AllowsPageOrigin(context.GetRequestHeader("Origin")))
        {
            _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
                DesktopErrorCategory.OriginDenied,
                "origin-not-allowed",
                "The browser origin is not allowed.",
                Retryable: false));
            context.StatusCode = PresentationStatus.Forbidden;
            return null;
        }

        if (!_securityPolicy.RequireSessionCredential)
        {
            return string.Empty;
        }

        // The side panel's page offers the panel credential instead (PanelConnectionSlot); region mode admits only its
        // frame.
        if (!_frameBinding.IsActive && OffersPanelCredential(context, out var panel))
        {
            return panel ?? RefuseCredential(context);
        }

        // A frame's page offers its frame token instead (FrameLaunchBinding); none is admitted before the frame used its
        // launch code or after the token was revoked. A component offers its component token (ComponentBinding), on the
        // listener that serves it.
        var expected = !_frameBinding.IsActive ? $"runic-desktop.{_sessionCredential}"
            : _componentBinding.Embedder is not null
                ? SameAuthority(context.Listener ?? Url, _componentBinding.Listener) ? _componentBinding.ExpectedProtocol : null
                : _frameBinding.ExpectedProtocol;
        var offered = context.WebSocketRequestedProtocols;
        if (expected is null || !OffersCredential(offered, expected))
        {
            _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
                DesktopErrorCategory.AuthenticationDenied,
                "credential-required",
                "A valid session credential is required.",
                Retryable: false));
            context.StatusCode = PresentationStatus.Unauthorized;
            return null;
        }

        return expected;
    }

    // Compares in constant time (SEC-24), so the time a refusal takes says nothing about how much of an offered
    // credential matched.
    internal static bool OffersCredential(IEnumerable<string> offered, string expected)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var found = false;
        foreach (var protocol in offered)
        {
            found |= CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(protocol), expectedBytes);
        }
        return found;
    }

    // Whether a WebSocket offers the open side panel's credential; panel is its subprotocol, or null when the WebSocket
    // offers the session credential too, which admits it to neither slot.
    private bool OffersPanelCredential(PresentationExchange context, out string? panel)
    {
        var offered = context.WebSocketRequestedProtocols;
        panel = _panelSlot.Admit(offered, out _);
        if (panel is null) return false;
        if (OffersCredential(offered, $"runic-desktop.{_sessionCredential}")) panel = null;
        return true;
    }

    private string? RefuseCredential(PresentationExchange context)
    {
        _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
            DesktopErrorCategory.AuthenticationDenied,
            "credential-required",
            "A valid session credential is required.",
            Retryable: false));
        context.StatusCode = PresentationStatus.Unauthorized;
        return null;
    }

    // The slot of a WebSocket that ValidateWebSocketAdmission admitted with accepted, and for the side panel the
    // generation it holds the slot at. Null when it offered the side panel's credential and the panel closed or opened
    // again since: the WebSocket is then admitted to no slot. The panel credential is matched first, so a panel page
    // that raced the surface into region mode never takes the frame's slot; its generation is then stale.
    private DesktopConnectionSlot? SlotOf(string accepted, out long panelGeneration)
    {
        panelGeneration = 0;
        if (accepted.Length != 0 && _panelSlot.Admit([accepted], out panelGeneration) is not null)
        {
            return DesktopConnectionSlot.SidePanel;
        }
        return accepted.Length == 0 || _frameBinding.IsActive ||
            string.Equals(accepted, $"runic-desktop.{_sessionCredential}", StringComparison.Ordinal)
            ? DesktopConnectionSlot.Page
            : null;
    }

    // The sessions of one slot, which YieldSilentConnections looks through for that slot's one connection.
    private IEnumerable<WebUiSession> SessionsOf(DesktopConnectionSlot slot) =>
        _sessions.Values.Where(session => session.Slot == slot);

    // sdk#864, P1a: opens the side panel's connection slot and returns its credential, which only the panel's view
    // receives, in its document-start script (CreatePanelDocumentStartScript). Opening again revokes the earlier
    // credential and closes its page's connection. The surface must admit one page with a session credential, outside
    // region mode and hosted or installed-browser launches (D062.8). The caller hands the credential only to a view that
    // runs document-start scripts: a served bootstrap script never carries it.
    internal string OpenPanelSlot()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_securityPolicy is not { RequireSessionCredential: true } || AllowMultipleClients)
            throw new InvalidOperationException(
                "A side panel needs the session credential and a single client (DesktopSecurityPolicy).");
        if (_frameBinding.IsActive || ChecksLaunchBinding)
            throw new InvalidOperationException(
                "A side panel needs a window of its own: the surface shows in a region, a host's window or an installed browser.");
        var hadPanel = _panelSlot.IsOpen;
        var credential = _panelSlot.Open();
        if (hadPanel) ClosePanelSessions();
        return credential;
    }

    // Revokes the side panel's credential and closes its page's connection. False when no panel slot was open.
    internal bool ClosePanelSlot()
    {
        if (!_panelSlot.Close()) return false;
        ClosePanelSessions();
        return true;
    }

    internal bool HasPanelSlot => _panelSlot.IsOpen;

    // The side panel's view's document-start script: CreateDocumentStartScript with the panel credential in place of the
    // session credential, so the panel's page offers it on its Bridge WebSocket. Null while no panel slot is open.
    internal string? CreatePanelDocumentStartScript() =>
        _panelSlot.Credential is { } credential ? CreateDocumentStartScript(credential) : null;

    private void ClosePanelSessions()
    {
        foreach (var session in SessionsOf(DesktopConnectionSlot.SidePanel))
        {
            _ = CloseBridgeIgnoringFailureAsync(session, CancellationToken.None);
        }
    }

    private bool AllowMultipleClients => _securityPolicy?.AllowMultipleClients ?? WebUiApplication.MultiClient;

    private bool UseClientCookies => _securityPolicy?.UseClientCookies ?? WebUiApplication.UseCookies;

    private bool WaitForConnection => _runtimeOptions?.WaitForConnection ?? WebUiApplication.ShowWaitConnection;

    private TimeSpan ConnectionTimeout => _runtimeOptions?.ConnectionTimeout ?? WebUiApplication.ConnectionTimeout;

    private TimeProvider TimeProvider => _runtimeOptions?.TimeProvider ?? TimeProvider.System;

    private int BrowserLaunchAttempts => _runtimeOptions?.BrowserLaunchAttempts ?? WebUiApplication.BrowserLaunchAttempts;

    internal const string BrowserLaunchStalledCode = "browser-launch-stalled";
    internal const string BridgeHandshakeExpiredCode = "bridge-handshake-expired";
    internal const string ConnectionDeadlineExtendedCode = "connection-deadline-extended";

    private string? BrowserFolder => _runtimeOptions?.BrowserFolder ?? WebUiApplication.GetBrowserFolder();

    private IWebUiEmbeddedHost CreateEmbeddedHost()
    {
        if (_runtimeOptions is null)
        {
            var applicationHost = WebUiApplication.CreateEmbeddedHost();
            (applicationHost as DesktopWindowHostAdapter)?.AttachPageAnnouncer(AnnounceInPageAsync);
            return applicationHost;
        }

        var factory = _runtimeOptions.EmbeddedHostFactory;
        if (!factory.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "No embedded WebView host is available. Install the platform runtime or configure a custom host factory.");
        }
        var host = factory.Create();
        (host as DesktopWindowHostAdapter)?.AttachPageAnnouncer(AnnounceInPageAsync);
        return host;
    }

    private bool TryDeleteGeneratedProfile(string path)
    {
        if (_runtimeOptions is null)
        {
            var deleted = WebUiApplication.TryDeleteGeneratedProfile(path, out var failure);
            LastProfileCleanupFailure = failure;
            return deleted;
        }

        LastProfileCleanupFailure = null;
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LastProfileCleanupFailure = exception;
            return false;
        }
    }

    // The surface's root folder is fixed by DesktopHost: the directory for directory content, otherwise none.
    private void ConfigureContent(DesktopContent content)
    {
        _embeddedHtml = null;
        _entryFile = null;
        _externalUrl = null;
        _allowIndexFallback = false;
        switch (content)
        {
            case DesktopContent.Directory { Entry: { } entry }:
                _entryFile = NormalizeEntryFile(entry);
                break;
            case DesktopContent.Directory:
                _allowIndexFallback = true;
                break;
            case DesktopContent.Html html:
                _embeddedHtml = html.Document;
                break;
            case DesktopContent.ExternalUrl external:
                _externalUrl = external.Url;
                break;
            case DesktopContent.Handler:
                // DesktopHost installs the request handler before the server starts.
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(content), content, "Unknown Desktop content.");
        }
    }

    // CS-WebUI compatible content strings for the engine's string API, which only the engine tests use.
    private void ConfigureContent(string content)
    {
        _embeddedHtml = null;
        _entryFile = null;
        _externalUrl = null;
        _allowIndexFallback = false;

        if (content.Length == 0)
        {
            _allowIndexFallback = true;
            return;
        }

        if (Uri.TryCreate(content, UriKind.Absolute, out var url) &&
            (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            _externalUrl = url;
            return;
        }

        if (IsEmbeddedHtml(content))
        {
            _embeddedHtml = content;
            return;
        }

        // Preserve M0's useful text-fragment behavior while keeping path-shaped values file-based.
        if (Path.GetExtension(content).Length == 0 &&
            !content.Contains('/') &&
            !content.Contains('\\'))
        {
            _embeddedHtml = content;
            return;
        }

        var isRooted = Path.IsPathRooted(content);
        var rootedCandidate = isRooted
            ? Path.GetFullPath(content)
            : Path.GetFullPath(content, Volatile.Read(ref _rootFolder) ?? Environment.CurrentDirectory);
        if (Directory.Exists(rootedCandidate))
        {
            Volatile.Write(ref _rootFolder, rootedCandidate);
            _allowIndexFallback = true;
            return;
        }

        if (File.Exists(rootedCandidate))
        {
            if (isRooted)
            {
                Volatile.Write(ref _rootFolder, Path.GetDirectoryName(rootedCandidate)!);
                _entryFile = Path.GetFileName(rootedCandidate);
            }
            else
            {
                _entryFile = NormalizeEntryFile(content);
            }
            return;
        }

        _entryFile = NormalizeEntryFile(content);
    }

    private Uri GetBrowserUrl(Uri serverUrl)
    {
        if (_externalUrl is not null)
        {
            return _externalUrl;
        }

        if (_entryFile is null)
        {
            return serverUrl;
        }

        return new Uri(serverUrl, string.Join('/', _entryFile.Split('/').Select(Uri.EscapeDataString)));
    }

    private async ValueTask<string?> FindVirtualIndexAsync(
        WebUiFileHandler handler,
        string path,
        CancellationToken cancellationToken)
    {
        foreach (var indexUrl in GetIndexUrls(path))
        {
            if (await handler(indexUrl, cancellationToken).ConfigureAwait(false) is not null)
            {
                return indexUrl;
            }
        }

        return null;
    }

    private string? FindPhysicalIndex(string path, string directory)
    {
        foreach (var indexUrl in GetIndexUrls(path))
        {
            var candidate = path == "/"
                ? ResolveLocalPath(indexUrl)
                : Path.Combine(directory, indexUrl[(indexUrl.LastIndexOf('/') + 1)..]);
            if (candidate is not null && File.Exists(candidate))
            {
                return indexUrl;
            }
        }

        return null;
    }

    private IEnumerable<string> GetIndexUrls(string path)
    {
        var basePath = path == "/" ? "/" : path.TrimEnd('/') + "/";
        if (_entryFile is not null)
        {
            var entry = path == "/" ? _entryFile : Path.GetFileName(_entryFile);
            yield return basePath + entry;
        }

        if (_allowIndexFallback)
        {
            yield return basePath + "index.html";
            yield return basePath + "index.htm";
            yield return basePath + "index.ts";
            yield return basePath + "index.js";
        }
    }

    private string? ResolveLocalPath(string path)
    {
        if (Volatile.Read(ref _rootFolder) is not { } configuredRoot || path.Contains('\0'))
        {
            return null;
        }

        var root = Path.GetFullPath(configuredRoot);
        var relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.Equals(root, comparison) && !candidate.StartsWith(rootPrefix, comparison))
        {
            return null;
        }

        var resolvedRoot = ResolveLink(root, directory: true);
        var resolvedRootPrefix = resolvedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedRoot
            : resolvedRoot + Path.DirectorySeparatorChar;
        var resolvedCandidate = resolvedRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolvedCandidate = Path.Combine(resolvedCandidate, segment);
            if (Directory.Exists(resolvedCandidate))
            {
                resolvedCandidate = ResolveLink(resolvedCandidate, directory: true);
            }
            else if (File.Exists(resolvedCandidate))
            {
                resolvedCandidate = ResolveLink(resolvedCandidate, directory: false);
            }

            resolvedCandidate = Path.GetFullPath(resolvedCandidate);
            if (!resolvedCandidate.Equals(resolvedRoot, comparison) &&
                !resolvedCandidate.StartsWith(resolvedRootPrefix, comparison))
            {
                return null;
            }
        }

        return candidate;
    }

    private static bool IsPath(string path, string expected) => string.Equals(path, expected, StringComparison.OrdinalIgnoreCase);

    private static string GetDecodedPath(string path)
    {
        var value = path.Length == 0 ? "/" : path;
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static bool IsEmbeddedHtml(string content) =>
        (content.Contains('<') && content.Contains('>')) ||
        content.Contains("<html", StringComparison.Ordinal) ||
        content.Contains("<!DOCTYPE", StringComparison.Ordinal) ||
        content.Contains("<!doctype", StringComparison.Ordinal) ||
        content.Contains("<!Doctype", StringComparison.Ordinal);

    private static string NormalizeEntryFile(string content) =>
        content.Replace('\\', '/').TrimStart('/');

    private static string ResolveLink(string path, bool directory)
    {
        if (directory ? !Directory.Exists(path) : !File.Exists(path))
        {
            return path;
        }

        FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
        return info.ResolveLinkTarget(true)?.FullName ?? path;
    }

    private static async Task CloseBridgeIgnoringFailureAsync(
        WebUiSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            await session.CloseBridgeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private static async Task DisposeSessionIgnoringFailureAsync(WebUiSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    // The redirect keeps the request's query, such as a route a served page reads from location.search (#313). The
    // fragment never reaches the server; a browser keeps it across a redirect whose location has none.
    private void Redirect(PresentationExchange context, string location)
    {
        context.StatusCode = PresentationStatus.Found;
        var pathBase = _surface?.PathBase;
        var target = pathBase is { Length: > 0 } && location.StartsWith('/')
            ? pathBase + location
            : location;
        context.SetResponseHeader("Location", target + context.QueryString);
    }

    private static async Task SendVirtualContentAsync(
        PresentationExchange context,
        string path,
        WebUiContent content,
        string? contentSecurityPolicy,
        CancellationToken cancellationToken)
    {
        context.StatusCode = content.StatusCode;
        context.ContentType = content.ContentType ?? GetContentType(path);
        if (content.Headers is not null)
        {
            foreach (var header in content.Headers)
            {
                // A handler's policy is sent beside the surface's framing policy instead of replacing it.
                if (string.Equals(header.Key, ContentSecurityPolicyHeader, StringComparison.OrdinalIgnoreCase))
                {
                    AddContentSecurityPolicy(context, header.Value);
                    continue;
                }
                context.SetResponseHeader(header.Key, header.Value);
            }
        }
        if (IsHtml(context.ContentType))
        {
            AddContentSecurityPolicy(context, contentSecurityPolicy);
        }
        context.ContentLength = content.ContentLength;
        if (!context.IsHead)
        {
            await content.WriteBodyAsync(context.ResponseBody, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendFileAsync(PresentationExchange context, string filePath, string requestPath, string? contentSecurityPolicy)
    {
        var file = new FileInfo(filePath);
        context.ContentType = GetContentType(requestPath);
        if (IsHtml(context.ContentType))
        {
            AddContentSecurityPolicy(context, contentSecurityPolicy);
        }
        context.ContentLength = file.Length;
        if (!context.IsHead)
        {
            await context.SendFileAsync(file.FullName, context.RequestAborted).ConfigureAwait(false);
        }
    }

    // SEC-06: DesktopSurfaceOptions.ContentSecurityPolicy and a handler's own policy are each a header of their
    // own next to the framing policy that PrepareResponseAsync sets, so every policy is enforced and none
    // drops frame-ancestors. A policy already sent is not repeated.
    private string? ContentSecurityPolicy => _runtimeOptions?.ContentSecurityPolicy;

    internal static void AddContentSecurityPolicy(PresentationExchange context, string? policy)
    {
        if (string.IsNullOrEmpty(policy))
        {
            return;
        }
        foreach (var value in context.GetResponseHeader(ContentSecurityPolicyHeader))
        {
            if (string.Equals(value, policy, StringComparison.Ordinal))
            {
                return;
            }
        }
        context.AppendResponseHeader(ContentSecurityPolicyHeader, policy);
    }

    private static bool IsHtml(string? contentType) =>
        contentType is not null &&
        contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) &&
        (contentType.Length == "text/html".Length || contentType["text/html".Length] is ';' or ' ');

    private static Task SendTextAsync(PresentationExchange context, string content, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        context.ContentType = contentType;
        context.ContentLength = bytes.Length;
        return context.IsHead
            ? Task.CompletedTask
            : context.ResponseBody.WriteAsync(bytes, context.RequestAborted).AsTask();
    }

    private Task SendNotFoundAsync(PresentationExchange context)
    {
        context.StatusCode = PresentationStatus.NotFound;
        var texts = Runic.Platform.DesktopPlatformTexts.Current;
        return SendTextAsync(context, ErrorPage(texts.ResourceUnavailableTitle, texts.ResourceUnavailableMessage, bridgeScript: true),
            "text/html; charset=utf-8");
    }

    private string? _pageLanguage;

    // The BCP 47 language of the pages the window serves itself (Access Denied, Resource Not Available): the window
    // locale, which Runic.Application.Views.Desktop sets; null leaves their lang and dir out. Their text is
    // DesktopPlatformTexts.Current when they are served, and dir follows the language's script.
    internal string? PageLanguage
    {
        get => Volatile.Read(ref _pageLanguage);
        set
        {
            if (value is not null && !IsLanguageTag(value))
                throw new ArgumentException($"'{value}' is not a language tag, such as \"de\" or \"pt-BR\".", nameof(value));
            Volatile.Write(ref _pageLanguage, value);
        }
    }

    private static bool IsLanguageTag(string value) =>
        value.Length is > 0 and <= 64 && char.IsAsciiLetter(value[0]) &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-') && !value.EndsWith('-') && !value.Contains("--");

    // A page of the window's own: the application's texts (DesktopPlatformTexts), in its language and direction.
    private string ErrorPage(string title, string message, bool bridgeScript)
    {
        var language = PageLanguage;
        var head = language is null
            ? "<html>"
            : $"<html lang=\"{language}\" dir=\"{(Internal.WindowLocaleDirection.Of(language) == Runic.Platform.DesktopLayoutDirection.RightToLeft ? "rtl" : "ltr")}\">";
        var encodedTitle = System.Net.WebUtility.HtmlEncode(title);
        return head + "<head><meta charset=\"utf-8\"><title>" + encodedTitle + "</title>"
            + (bridgeScript ? "<script src=\"/webui.js\"></script>" : "")
            + "</head><body><h2>&#9888; " + encodedTitle + "</h2><p>" + System.Net.WebUtility.HtmlEncode(message) + "</p></body></html>";
    }

    private string AccessDenied
    {
        get
        {
            var texts = Runic.Platform.DesktopPlatformTexts.Current;
            return ErrorPage(texts.AccessDeniedTitle, texts.ContentInUseMessage, bridgeScript: true);
        }
    }

    private string LaunchRefused
    {
        get
        {
            var texts = Runic.Platform.DesktopPlatformTexts.Current;
            return ErrorPage(texts.AccessDeniedTitle, texts.LaunchLinkInvalidMessage, bridgeScript: false);
        }
    }

    private static string GetContentType(string path) =>
        PresentationContentTypes.TryGetContentType(path, out var contentType)
            ? contentType
            : "application/octet-stream";

    private void BroadcastBinding(string element)
    {
        foreach (var session in _sessions.Values)
        {
            _ = SendBindingIgnoringClosedSessionAsync(session, element);
        }
    }

    private async Task SendBindingIgnoringClosedSessionAsync(WebUiSession session, string element)
    {
        try
        {
            await session.SendBindingAsync(element, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static uint CreateToken()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        uint token;
        do
        {
            OsRandom.Fill(bytes);
            token = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
        while (token == 0);

        return token;
    }

    private sealed record BindingRegistration(
        long Id,
        long Order,
        Func<WebUiEvent, CancellationToken, ValueTask<WebUiResult>> Handler);
}
