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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
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
    private const string WebSocketPath = "/_webui_ws_connect";
    private const string AuthCookieName = "webui_auth";
    private const string DocumentBootstrapProperty = "globalThis[Symbol.for(\"runic-desktop.bootstrap\")]";
    private const string NoCache = "no-cache, no-store, must-revalidate, private, max-age=0";
    private const string AccessDenied = "<html><head><title>Access Denied</title><script src=\"/webui.js\"></script></head><body><h2>&#9888; Access Denied</h2><p>This content is already in use and multi-client mode is disabled.</p></body></html>";
    private const string LaunchRefused = "<html><head><title>Access Denied</title></head><body><h2>&#9888; Access Denied</h2><p>This window's launch link is not valid or was already used by another client. Close this window and open it again from the application.</p></body></html>";
    private const string ResourceUnavailable = "<html><head><title>Resource Not Available</title><script src=\"/webui.js\"></script></head><body><h2>&#9888; Resource Not Available</h2><p>The requested resource is not available.</p></body></html>";
    private const string DefaultIcon = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"500\" viewBox=\"0 0 375 375\" height=\"500\"><path fill=\"#2a6699\" d=\"M22 22h330v330H22z\"/></svg>";
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
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
    private WebUiFileHandler? _fileHandler;
    private Func<HttpContext, string, CancellationToken, ValueTask<WebUiContent?>>? _requestHandler;
    // DesktopSurfaceOptions.Resources: asked for what the content does not serve, before 404 (#313).
    private Func<HttpContext, string, CancellationToken, ValueTask<WebUiContent?>>? _resourceHandler;
    private bool _allowIndexFallback;
    private bool _isPublic;
    private bool _profileConfigured;
    private bool _kiosk;
    private string? _monitor;
    private Runic.Platform.DesktopLayoutDirection _layoutDirection;
    private string? _language;
    private bool _hidden;
    private bool _resizable = true;
    private bool _frameless;
    private bool _transparent;
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
            ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
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

    /// <summary>Gets the native embedded-window handle, or zero outside WebView mode.</summary>
    public nint NativeWindowHandle => _embeddedHost?.NativeHandle ?? 0;
    internal DesktopWindowCapabilities WindowCapabilities => _embeddedHost?.Capabilities ?? DesktopWindowCapabilities.None;

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
                    PushMenuBar(host);
                }
                catch
                {
                    StopCloseConfirmation();
                    host.Closed -= EmbeddedHostClosed;
                    host.StateChanged -= EmbeddedHostStateChanged;
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
                    PushMenuBar(host);
                }
                catch
                {
                    StopCloseConfirmation();
                    host.Closed -= EmbeddedHostClosed;
                    host.StateChanged -= EmbeddedHostStateChanged;
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
            var deadline = DateTime.UtcNow + ConnectionTimeout;
            try
            {
                while (!connection.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ProcessEmbeddedHostEvents();
                    if (DateTime.UtcNow >= deadline)
                    {
                        throw new TimeoutException(DescribeConnectionTimeout(browserProcess: null));
                    }
                    Thread.Sleep(10);
                }
                connection.GetAwaiter().GetResult();
            }
            catch
            {
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
        Func<HttpContext, string, CancellationToken, ValueTask<WebUiContent?>>? handler) =>
        Volatile.Write(ref _requestHandler, handler);

    internal void SetResourceHandler(
        Func<HttpContext, string, CancellationToken, ValueTask<WebUiContent?>>? handler) =>
        Volatile.Write(ref _resourceHandler, handler);

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

    internal Task AnnounceAsync(string message, DesktopAnnouncementPriority priority, CancellationToken cancellationToken)
        => GetEmbeddedHost().AnnounceAsync(message, priority, cancellationToken).AsTask();

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

    // Pushes a View's state, as UTF-8 JSON, to every authenticated browser (WebUiSession.PushStateAsync).
    internal Task PushStateAsync(string route, ReadOnlyMemory<byte> stateUtf8, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrEmpty(route);
        if (stateUtf8.IsEmpty) throw new ArgumentException("The state is empty.", nameof(stateUtf8));
        return Task.WhenAll(_sessions.Values
            .Where(static session => session.IsAuthenticated)
            .Select(session => session.PushStateAsync(route, stateUtf8.Span, cancellationToken)));
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

    /// <summary>Navigates every authenticated browser to a URL.</summary>
    public Task NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return Task.WhenAll(_sessions.Values
            .Where(static session => session.IsAuthenticated)
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
    }

    // The monitor, native direction and web language of the next embedded window (DesktopWindowOptions).
    internal void SetMonitorAndLanguage(string? monitor, Runic.Platform.DesktopLayoutDirection layoutDirection, string? language)
    {
        _monitor = monitor;
        _layoutDirection = layoutDirection;
        _language = language;
    }

    // Where the next embedded window shows the menu bar (DesktopWindowOptions.MenuBarPresentation).
    internal void SetMenuBarPresentation(DesktopMenuBarPresentation presentation) => _menuBarPresentation = presentation;

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

    // The command path of the current embedded host's items, with the host it belongs to.
    private sealed record EmbeddedMenuChain(IWebUiEmbeddedHost Host, MenuCommandChain Commands);
    private EmbeddedMenuChain? _menuChain;

    private DesktopMenuBarPresentation _menuBarPresentation;
    private int _menuBarSubscribed;

    // The host whose menu bar the windows show, and whose MenuCommands come after the window's.
    private DesktopHost? MenuHost => _runtimeOptions?.MenuHost;

    // Gives a new embedded host the command path of its items, and follows the host's menu bar from now on.
    private void AttachMenuCommands(IWebUiEmbeddedHost host)
    {
        var menuHost = MenuHost;
        var commands = new MenuCommandChain(() => menuHost?.MenuCommands, Logger);
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
        if (Volatile.Read(ref _embeddedHost) is { IsOpen: true } host) PushMenuBar(host);
    }

    // Hands the host's current menus to the window; a failure is the window's diagnostic, never the caller's.
    private void PushMenuBar(IWebUiEmbeddedHost host)
    {
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
        if (sender is IWebUiEmbeddedHost host && ReferenceEquals(Volatile.Read(ref _embeddedHost), host))
            EmbeddedMenuBarPlacementChanged?.Invoke(host);
    }

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
        byte[][] arguments,
        CancellationToken cancellationToken)
    {
        if (element == "__webui_core_api__")
        {
            return arguments.Length > 0 && Encoding.UTF8.GetString(arguments[0]) == "high_contrast"
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
        byte[][] arguments,
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
            session.Cookies);
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
        byte[][] arguments,
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
            session.Cookies);
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

    private WebUiSession GetExecutionSession()
        => _sessions.Values.FirstOrDefault(static session => session.IsAuthenticated)
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

    private WebUiEmbeddedHostOptions CreateEmbeddedHostOptions()
    {
        string? profilePath = null;
        if (_profileConfigured)
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
            IconFile = _iconFile,
            ProfilePath = profilePath,
            BrowserArguments = _browserArguments,
            AllowedPermissions = _allowedPermissions,
            WebContent = WebContentRules.Resolve(_webContent, Environment.GetEnvironmentVariable),
            // The surface's own origin too: a window presenting external content can return to it.
            AdditionalOrigins = (_securityPolicy?.AdditionalOrigins ?? Enumerable.Empty<string>())
                .Select(static origin => new Uri(origin, UriKind.Absolute))
                .Prepend(Url!)
                .ToArray(),
            DocumentStartScript = _embeddedHost?.SupportsDocumentStartScript == true ? CreateDocumentStartScript() : null,
            ReportDiagnostic = ReportEmbeddedDiagnostic,
            MenuBarPresentation = _menuBarPresentation,
            Monitor = _monitor,
            LayoutDirection = _layoutDirection,
            Language = _language,
        };
    }

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
        if (_runtimeOptions?.Logger is { } logger)
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
        Volatile.Write(ref _handshakeLog, new BridgeHandshakeLog());
        _browserConnected = NewCompletionSource();
        return _browserConnected.Task;
    }

    private void ReportConnectionProgress(ConnectionProgress progress)
    {
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
        while (true)
        {
            string stalled;
            try
            {
                await connection.WaitAsync(ConnectionTimeout, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException exception)
            {
                // Describe the browser process before closing the presentation stops it.
                stalled = DescribeConnectionTimeout(browserProcess, attempt, attempts);
                if (attempt >= attempts || !IsStalledBeforeFirstRequest(browserProcess))
                {
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
            connection = relaunch.Connection!;
            browserProcess = relaunch.Process!;
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
        var progress = (ConnectionProgress)Volatile.Read(ref _connectionProgress) switch
        {
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
        return $"{presentation} did not connect within {ConnectionTimeout.TotalSeconds:0.###} seconds{attemptText}: {progress}{process}. {HandshakeLog.Describe()}";
    }

    private Task HandleRequestAsync(HttpContext context) => PrepareResponseAsync(context, DispatchRequestAsync);

    private Task DispatchRequestAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path == BridgePath && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            HandshakeLog.Record($"{BridgePath} requested");
            ReportConnectionProgress(ConnectionProgress.BridgeRequested);
            return ServeBridgeAsync(context);
        }
        if (path == DesktopBootstrapPath && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            HandshakeLog.Record($"{DesktopBootstrapPath} requested");
            ReportConnectionProgress(ConnectionProgress.BridgeRequested);
            return ServeDesktopBootstrapAsync(context);
        }
        if (path == WebSocketPath && HttpMethods.IsGet(context.Request.Method))
        {
            return AcceptWebSocketAsync(context);
        }
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            if (context.Request.Path == "/" || Volatile.Read(ref _connectionProgress) < (int)ConnectionProgress.DocumentRequested)
            {
                HandshakeLog.Record($"{context.Request.Method} {context.Request.Path} requested");
            }
            ReportConnectionProgress(ConnectionProgress.DocumentRequested);
            return ServeContentAsync(context);
        }

        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        return Task.CompletedTask;
    }

    private async Task ServeContentAsync(HttpContext context)
    {
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            _shutdown.Token);
        var cancellationToken = requestCancellation.Token;
        var path = GetDecodedPath(context.Request.Path);
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
                AddContentSecurityPolicy(context.Response.Headers, ContentSecurityPolicy);
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

    // Any page can embed a loopback script, and a classic script needs no CORS
    // grant to run. Fetch metadata identifies a cross-site embedding. Clients that
    // send none are not browser subresource loads and still need the WebSocket
    // origin and credential checks to open a session.
    private bool AdmitsBootstrapRequest(HttpContext context)
    {
        var headers = context.Request.Headers;
        var destination = headers["Sec-Fetch-Dest"].ToString();
        var site = headers["Sec-Fetch-Site"].ToString();
        var admitted = destination.Length == 0 || destination.Equals("script", StringComparison.OrdinalIgnoreCase);
        admitted &= site.Length == 0 ||
            site.Equals("same-origin", StringComparison.OrdinalIgnoreCase) ||
            site.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            _securityPolicy?.AllowsAdditionalOrigin(headers.Referer.ToString()) == true;
        if (!admitted)
        {
            _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
                DesktopErrorCategory.OriginDenied,
                "bootstrap-origin-not-allowed",
                "The bootstrap script was requested by a document whose origin is not allowed.",
                Retryable: false));
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
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

    private async Task ServeBridgeAsync(HttpContext context)
    {
        if (!AdmitsBootstrapRequest(context))
        {
            return;
        }

        context.Response.ContentType = "text/javascript; charset=utf-8";
        var (token, sessionCredential) = GetBootstrapCredentialExpressions();
        var script = WebUiBridge.Script
            .Replace("__TOKEN__", token, StringComparison.Ordinal)
            .Replace("__PORT__", Url?.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0", StringComparison.Ordinal)
            .Replace("__BASE_PATH__", _surface?.PathBase ?? string.Empty, StringComparison.Ordinal)
            .Replace("__SESSION_CREDENTIAL__", sessionCredential, StringComparison.Ordinal)
            .Replace("__CUSTOM_WINDOW_DRAG__", _embeddedHost is not null && _frameless ? "true" : "false", StringComparison.Ordinal);
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(script);
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.WriteAsync(script, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private async Task ServeDesktopBootstrapAsync(HttpContext context)
    {
        if (!AdmitsBootstrapRequest(context))
        {
            return;
        }

        context.Response.ContentType = "text/javascript; charset=utf-8";
        var webSocketPath = $"{_surface?.PathBase ?? string.Empty}{WebSocketPath}";
        var (token, sessionCredential) = GetBootstrapCredentialExpressions();
        var script = $$"""
            (() => {
              "use strict";
              const endpoint = new URL({{EncodeJavaScriptString(webSocketPath)}}, globalThis.location.href);
              endpoint.protocol = endpoint.protocol === "https:" ? "wss:" : "ws:";
              endpoint.port = {{Url?.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0"}};
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
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(script);
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.WriteAsync(script, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static string EncodeJavaScriptString(string value) =>
        $"\"{JavaScriptEncoder.Default.Encode(value)}\"";

    // A host that runs a document-start script hands the credentials only to
    // documents from admitted origins. Fetchable scripts then read them from the
    // document instead of carrying them to every requester.
    private (string Token, string SessionCredential) GetBootstrapCredentialExpressions() =>
        _embeddedHost?.SupportsDocumentStartScript == true
            ? ($"({DocumentBootstrapProperty}?.token ?? 0)", $"({DocumentBootstrapProperty}?.sessionCredential ?? \"\")")
            : (_token.ToString(System.Globalization.CultureInfo.InvariantCulture), EncodeJavaScriptString(_sessionCredential));

    private string FrameAncestorsPolicy => _frameAncestorsPolicy ??= CreateFrameAncestorsPolicy(_securityPolicy?.AdditionalOrigins);

    // The additional origins are canonical (scheme://host:port), which is a valid CSP host source.
    internal static string CreateFrameAncestorsPolicy(IEnumerable<string>? additionalOrigins) =>
        string.Join(' ', (additionalOrigins ?? []).Order(StringComparer.Ordinal).Prepend("frame-ancestors 'self'"));

    private string CreateDocumentStartScript()
    {
        var url = Url ?? throw new InvalidOperationException("The surface must be started before opening a presentation.");
        var origins = (_securityPolicy?.AdditionalOrigins ?? Enumerable.Empty<string>())
            .Select(static origin => new Uri(origin, UriKind.Absolute))
            .Prepend(url)
            .Select(static origin => origin.IsDefaultPort
                ? $"{origin.Scheme}://{origin.IdnHost}"
                : $"{origin.Scheme}://{origin.IdnHost}:{origin.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
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
                  token: {{_token.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                  sessionCredential: {{EncodeJavaScriptString(_sessionCredential)}}
                })
              });
            })();
            """;
    }

    private async Task AcceptWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            ReportConnectionProgress(ConnectionProgress.WebSocketRejected);
            HandshakeLog.RecordRejection("not a WebSocket upgrade");
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }

        var acceptedSubprotocol = ValidateWebSocketAdmission(context);
        if (acceptedSubprotocol is null)
        {
            ReportConnectionProgress(ConnectionProgress.WebSocketRejected);
            HandshakeLog.RecordRejection(context.Response.StatusCode == StatusCodes.Status401Unauthorized
                ? "the session credential was missing"
                : "the origin is not allowed");
            return;
        }

        if (ChecksLaunchBinding && !_launchBinding.Admits(FindClient(context)))
        {
            ReportConnectionProgress(ConnectionProgress.WebSocketRejected);
            HandshakeLog.RecordRejection("the client is not the browser this window launched");
            RejectUnboundClient(context, "WebSocket");
            return;
        }

        bool admitted;
        lock (_connectionGate)
        {
            admitted = AllowMultipleClients || _activeConnections == 0 || YieldSilentConnections();
            if (admitted)
            {
                _activeConnections++;
            }
        }

        if (!admitted)
        {
            ReportConnectionProgress(ConnectionProgress.WebSocketRejected);
            HandshakeLog.RecordRejection("another WebSocket holds the only connection");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        try
        {
            var clientId = GetOrCreateClient(context);
            using var socket = await context.WebSockets.AcceptWebSocketAsync(
                acceptedSubprotocol.Length == 0 ? null : acceptedSubprotocol).ConfigureAwait(false);
            ReportConnectionProgress(ConnectionProgress.WebSocketOpened);
            var connectionId = checked((nuint)Interlocked.Increment(ref _nextConnectionId));
            HandshakeLog.Record($"WebSocket {connectionId} opened");
            var session = new WebUiSession(
                this,
                socket,
                clientId == 0 ? connectionId : clientId,
                connectionId,
                context.Request.Headers.Cookie.ToString(),
                WebUiApplication.BridgeHandshakeTimeout);
            if (!_sessions.TryAdd(session.Id, session))
            {
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
                _activeConnections--;
            }
        }
    }

    // Called under the connection gate when a new WebSocket finds the only
    // connection taken. A socket that never checked its token, such as one from a
    // replaced page, gives the connection up, and so does one whose token check
    // failed. An authenticated socket keeps it, and so does one still being accepted.
    // Returns whether the connection is now free.
    private bool YieldSilentConnections()
    {
        var holding = _activeConnections;
        foreach (var session in _sessions.Values)
        {
            if (session.IsHandshakeClosing || session.TryYieldSilentHandshake())
            {
                holding--;
            }
        }
        return holding <= 0;
    }

    private async Task PrepareResponseAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers.CacheControl = NoCache;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        // SEC-05: only the surface's own documents and its additional origins may frame its pages. Browsers that apply
        // frame-ancestors ignore X-Frame-Options; it covers those that do not.
        context.Response.Headers.ContentSecurityPolicy = FrameAncestorsPolicy;
        context.Response.Headers.XFrameOptions = "SAMEORIGIN";
        if ((_allowedPermissions & DesktopPermissionGrant.MediaCapture) == 0)
        {
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=()";
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            var clientId = GetOrCreateClient(context);
            context.Items[typeof(BrowserLaunchBinding)] = clientId;
            // Before the single-client check: the launched browser must reach its conflict check even when another
            // client already holds the connection.
            if (context.Request.Path.StartsWithSegments(BrowserLaunchBinding.PathSegment, out var code))
            {
                await ServeBrowserLaunchAsync(context, clientId, code.Value?.TrimStart('/') ?? string.Empty).ConfigureAwait(false);
                return;
            }
            if (UseClientCookies &&
                !AllowMultipleClients &&
                _sessions.Values.Any(session => session.ClientId != clientId))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await SendTextAsync(context, AccessDenied, "text/html; charset=utf-8").ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }

    // The installed browser was launched with a one-time code: bind its client, or detect that another client
    // used the code first (SEC-01).
    private async Task ServeBrowserLaunchAsync(HttpContext context, nuint clientId, string code)
    {
        HandshakeLog.Record($"{BrowserLaunchBinding.PathSegment} requested");
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        if (OperatingSystem.IsLinux() &&
            context.Connection.RemoteIpAddress is { } remoteAddress &&
            context.Connection.LocalIpAddress is { } localAddress &&
            LoopbackPeerOwner.Find(remoteAddress, context.Connection.RemotePort, localAddress, context.Connection.LocalPort) is { } owner &&
            owner != LoopbackPeerOwner.CurrentUser)
        {
            HandshakeLog.RecordRejection("another user opened the launch code");
            _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
                DesktopErrorCategory.AuthenticationDenied,
                "browser-launch-foreign-user",
                "Another user's process opened the browser launch address and was refused.",
                Retryable: false));
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await SendTextAsync(context, LaunchRefused, "text/html; charset=utf-8").ConfigureAwait(false);
            return;
        }

        switch (_launchBinding.Consume(code, clientId, out var target))
        {
            case BrowserLaunchBinding.Outcome.Bound or BrowserLaunchBinding.Outcome.AlreadyBound:
                ReportConnectionProgress(ConnectionProgress.DocumentRequested);
                context.Response.StatusCode = StatusCodes.Status303SeeOther;
                context.Response.Headers.Location = target!.AbsoluteUri;
                return;
            case BrowserLaunchBinding.Outcome.Conflict:
                HandshakeLog.RecordRejection("another client used the launch code first");
                RevokeLaunchedSessions();
                break;
            default:
                HandshakeLog.RecordRejection("the launch code is not valid");
                break;
        }
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await SendTextAsync(context, LaunchRefused, "text/html; charset=utf-8").ConfigureAwait(false);
    }

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
        _browserConnected.TrySetException(new DesktopException(
            DesktopErrorCategory.AuthenticationDenied,
            "browser-launch-conflict",
            message,
            retryable: true));
        foreach (var session in _sessions.Values)
        {
            _ = CloseBridgeIgnoringFailureAsync(session, CancellationToken.None);
        }
    }

    private void RejectUnboundClient(HttpContext context, string request)
    {
        _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
            DesktopErrorCategory.AuthenticationDenied,
            "client-not-bound",
            $"A {request} request came from a client other than the browser this window launched.",
            Retryable: false));
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
    }

    // Launch binding needs the client cookie, a credential to protect and a single client.
    private bool BindsBrowserLaunch =>
        _securityPolicy is { RequireSessionCredential: true } && !AllowMultipleClients && UseClientCookies;

    // Only while the surface is not in an embedded host, which has no access to the browser's cookie and
    // receives the credential from its document-start script instead.
    private bool ChecksLaunchBinding => _embeddedHost is null && _launchBinding.IsActive;

    // The client of a request that PrepareResponseAsync identified.
    private static nuint RequestClient(HttpContext context) =>
        context.Items.TryGetValue(typeof(BrowserLaunchBinding), out var client) && client is nuint id ? id : 0;

    // The client a WebSocket upgrade's cookie names, without creating one.
    private nuint FindClient(HttpContext context) =>
        context.Request.Cookies.TryGetValue(AuthCookieName, out var cookie) && _clients.TryGetValue(cookie, out var clientId)
            ? clientId
            : 0;

    private nuint GetOrCreateClient(HttpContext context)
    {
        if (!UseClientCookies)
        {
            return 0;
        }

        if (context.Request.Cookies.TryGetValue(AuthCookieName, out var existing) &&
            _clients.TryGetValue(existing, out var clientId))
        {
            return clientId;
        }

        var cookie = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        clientId = checked((nuint)Interlocked.Increment(ref _nextClientId));
        _clients[cookie] = clientId;
        context.Response.Cookies.Append(AuthCookieName, cookie, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = $"{_surface?.PathBase}/",
        });
        return clientId;
    }

    private string? ValidateWebSocketAdmission(HttpContext context)
    {
        if (_securityPolicy is null)
        {
            return string.Empty;
        }

        if (!_securityPolicy.AllowsOrigin(Url!, context.Request.Headers.Origin.ToString()))
        {
            _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
                DesktopErrorCategory.OriginDenied,
                "origin-not-allowed",
                "The browser origin is not allowed.",
                Retryable: false));
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return null;
        }

        if (!_securityPolicy.RequireSessionCredential)
        {
            return string.Empty;
        }

        var expected = $"runic-desktop.{_sessionCredential}";
        var offered = context.WebSockets.WebSocketRequestedProtocols;
        if (!OffersCredential(offered, expected))
        {
            _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
                DesktopErrorCategory.AuthenticationDenied,
                "credential-required",
                "A valid session credential is required.",
                Retryable: false));
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
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

    private bool AllowMultipleClients => _securityPolicy?.AllowMultipleClients ?? WebUiApplication.MultiClient;

    private bool UseClientCookies => _securityPolicy?.UseClientCookies ?? WebUiApplication.UseCookies;

    private bool WaitForConnection => _runtimeOptions?.WaitForConnection ?? WebUiApplication.ShowWaitConnection;

    private TimeSpan ConnectionTimeout => _runtimeOptions?.ConnectionTimeout ?? WebUiApplication.ConnectionTimeout;

    private int BrowserLaunchAttempts => _runtimeOptions?.BrowserLaunchAttempts ?? WebUiApplication.BrowserLaunchAttempts;

    internal const string BrowserLaunchStalledCode = "browser-launch-stalled";
    internal const string BridgeHandshakeExpiredCode = "bridge-handshake-expired";

    private string? BrowserFolder => _runtimeOptions?.BrowserFolder ?? WebUiApplication.GetBrowserFolder();

    private IWebUiEmbeddedHost CreateEmbeddedHost()
    {
        if (_runtimeOptions is null)
        {
            return WebUiApplication.CreateEmbeddedHost();
        }

        var factory = _runtimeOptions.EmbeddedHostFactory;
        if (!factory.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "No embedded WebView host is available. Install the platform runtime or configure a custom host factory.");
        }
        return factory.Create();
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

    private static string GetDecodedPath(PathString path)
    {
        var value = path.Value ?? "/";
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
    private void Redirect(HttpContext context, string location)
    {
        context.Response.StatusCode = StatusCodes.Status302Found;
        var pathBase = _surface?.PathBase;
        var target = pathBase is { Length: > 0 } && location.StartsWith('/')
            ? pathBase + location
            : location;
        context.Response.Headers.Location = target + context.Request.QueryString.ToUriComponent();
    }

    private static async Task SendVirtualContentAsync(
        HttpContext context,
        string path,
        WebUiContent content,
        string? contentSecurityPolicy,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = content.StatusCode;
        context.Response.ContentType = content.ContentType ?? GetContentType(path);
        if (content.Headers is not null)
        {
            foreach (var header in content.Headers)
            {
                // A handler's policy is sent beside the surface's framing policy instead of replacing it.
                if (string.Equals(header.Key, Microsoft.Net.Http.Headers.HeaderNames.ContentSecurityPolicy, StringComparison.OrdinalIgnoreCase))
                {
                    AddContentSecurityPolicy(context.Response.Headers, header.Value);
                    continue;
                }
                context.Response.Headers[header.Key] = header.Value;
            }
        }
        if (IsHtml(context.Response.ContentType))
        {
            AddContentSecurityPolicy(context.Response.Headers, contentSecurityPolicy);
        }
        context.Response.ContentLength = content.ContentLength;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await content.WriteBodyAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendFileAsync(HttpContext context, string filePath, string requestPath, string? contentSecurityPolicy)
    {
        var file = new FileInfo(filePath);
        context.Response.ContentType = GetContentType(requestPath);
        if (IsHtml(context.Response.ContentType))
        {
            AddContentSecurityPolicy(context.Response.Headers, contentSecurityPolicy);
        }
        context.Response.ContentLength = file.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.SendFileAsync(file.FullName, context.RequestAborted).ConfigureAwait(false);
        }
    }

    // SEC-06: DesktopSurfaceOptions.ContentSecurityPolicy and a handler's own policy are each a header of their
    // own next to the framing policy that PrepareResponseAsync sets, so every policy is enforced and none
    // drops frame-ancestors. A policy already sent is not repeated.
    private string? ContentSecurityPolicy => _runtimeOptions?.ContentSecurityPolicy;

    internal static void AddContentSecurityPolicy(IHeaderDictionary headers, string? policy)
    {
        if (string.IsNullOrEmpty(policy))
        {
            return;
        }
        var current = headers.ContentSecurityPolicy;
        foreach (var value in current)
        {
            if (string.Equals(value, policy, StringComparison.Ordinal))
            {
                return;
            }
        }
        headers.ContentSecurityPolicy = Microsoft.Extensions.Primitives.StringValues.Concat(current, policy);
    }

    private static bool IsHtml(string? contentType) =>
        contentType is not null &&
        contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) &&
        (contentType.Length == "text/html".Length || contentType["text/html".Length] is ';' or ' ');

    private static Task SendTextAsync(HttpContext context, string content, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        context.Response.ContentType = contentType;
        context.Response.ContentLength = bytes.Length;
        return HttpMethods.IsHead(context.Request.Method)
            ? Task.CompletedTask
            : context.Response.Body.WriteAsync(bytes, context.RequestAborted).AsTask();
    }

    private static Task SendNotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return SendTextAsync(context, ResourceUnavailable, "text/html; charset=utf-8");
    }

    private static string GetContentType(string path) =>
        ContentTypes.TryGetContentType(path, out var contentType)
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
            RandomNumberGenerator.Fill(bytes);
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
