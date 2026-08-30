using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Runic.Desktop.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace Runic.Desktop;

/// <summary>Owns one Runic Desktop server and its browser connections.</summary>
internal sealed class WebUiWindow : IDisposable, IAsyncDisposable
{
    private const string BridgePath = "/webui.js";
    private const string DesktopBootstrapPath = "/runic-desktop.js";
    private const string WebSocketPath = "/_webui_ws_connect";
    private const string AuthCookieName = "webui_auth";
    private const string NoCache = "no-cache, no-store, must-revalidate, private, max-age=0";
    private const string AccessDenied = "<html><head><title>Access Denied</title><script src=\"/webui.js\"></script></head><body><h2>&#9888; Access Denied</h2><p>This content is already in use and multi-client mode is disabled.</p></body></html>";
    private const string ResourceUnavailable = "<html><head><title>Resource Not Available</title><script src=\"/webui.js\"></script></head><body><h2>&#9888; Resource Not Available</h2><p>The requested resource is not available.</p></body></html>";
    private const string DefaultIcon = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"500\" viewBox=\"0 0 375 375\" height=\"500\"><path fill=\"#2a6699\" d=\"M22 22h330v330H22z\"/></svg>";
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private static readonly AsyncLocal<WebUiWindow?> CallbackWindow = new();

    private readonly ConcurrentDictionary<string, BindingRegistration> _bindings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, WebUiSession> _sessions = new();
    private readonly ConcurrentDictionary<string, nuint> _clients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
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
    private Process? _browserProcess;
    private IWebUiEmbeddedHost? _embeddedHost;
    private TaskCompletionSource _browserConnected = NewCompletionSource();
    private string _rootFolder;
    private string? _profileName;
    private string? _profilePath;
    private string? _generatedProfilePath;
    private string? _proxyServer;
    private IReadOnlyList<string> _customBrowserArguments = [];
    private string? _customBrowserParameters;
    private DesktopPermissionGrant _allowedPermissions;
    private string? _embeddedHtml;
    private string? _entryFile;
    private Uri? _externalUrl;
    private WebUiFileHandler? _fileHandler;
    private Func<HttpContext, string, CancellationToken, ValueTask<WebUiContent?>>? _requestHandler;
    private bool _allowIndexFallback;
    private bool _isPublic;
    private bool _profileConfigured;
    private bool _kiosk;
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
    private uint? _x;
    private uint? _y;
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
        _rootFolder = runtimeOptions?.RootFolder ?? WebUiApplication.GetDefaultRootFolder();
    }

    /// <summary>Gets the local server URL after the window has started.</summary>
    public Uri? Url { get; private set; }

    /// <summary>Gets the configured or bound HTTP port.</summary>
    public nuint Port => checked((nuint)(Url?.Port ?? _requestedPort));

    /// <summary>Gets whether the server accepts connections on non-loopback interfaces.</summary>
    public bool IsPublic => _isPublic;

    /// <summary>Gets whether this window currently owns a browser process or authenticated client.</summary>
    public bool IsShown =>
        (_browserProcess is { HasExited: false }) || (_embeddedHost?.IsOpen ?? false)
        || _sessions.Values.Any(static session => session.IsAuthenticated);

    /// <summary>Gets the operating-system identifier of the owned browser process.</summary>
    public nuint BrowserProcessId => _browserProcess is { HasExited: false } process
        ? checked((nuint)process.Id)
        : 0;

    /// <summary>Gets the native embedded-window handle, or zero outside WebView mode.</summary>
    public nint NativeWindowHandle => _embeddedHost?.NativeHandle ?? 0;

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
    public async Task<Uri> StartServerAsync(string content, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(content);

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

            ConfigureContent(content);
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
            if (_browserProcess is { HasExited: true } exitedProcess)
            {
                _browserProcess = null;
                exitedProcess.Dispose();
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
                var installation = WebUiBrowserDiscovery.Find(requestedBrowser, BrowserFolder)
                    ?? throw new InvalidOperationException($"No supported installation was found for {browser}.");
                var profilePath = GetOrCreateProfilePath(installation.Browser);
                var options = new WebUiBrowserLaunchOptions(
                    _profileName,
                    profilePath,
                    _proxyServer,
                    _customBrowserArguments,
                    _kiosk,
                    _hidden,
                    _width,
                    _height,
                    _x,
                    _y,
                    _allowedPermissions);
                _browserConnected = NewCompletionSource();
                Process process;
                try
                {
                    process = WebUiBrowserHost.Start(installation, browserUrl, options);
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
                _currentBrowser = installation.Browser;
                connection = _browserConnected.Task;
                _ = MonitorBrowserAsync(process);
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
            try
            {
                await connection.WaitAsync(ConnectionTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await CloseCoreAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        return url;
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
                host = CreateEmbeddedHost();
                host.Closed += EmbeddedHostClosed;
                _embeddedHost = host;
                _currentBrowser = WebUiBrowser.WebView;
                _browserConnected = NewCompletionSource();
                connection = _browserConnected.Task;
                try
                {
                    await host.ShowAsync(GetBrowserUrl(url), CreateEmbeddedHostOptions(), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    host.Closed -= EmbeddedHostClosed;
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
            try
            {
                await connection.WaitAsync(ConnectionTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await CloseCoreAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
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
                host = CreateEmbeddedHost();
                host.Closed += EmbeddedHostClosed;
                _embeddedHost = host;
                _currentBrowser = WebUiBrowser.WebView;
                _browserConnected = NewCompletionSource();
                connection = _browserConnected.Task;
                try
                {
                    host.ShowAsync(GetBrowserUrl(url), CreateEmbeddedHostOptions(), cancellationToken)
                        .AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                    host.Closed -= EmbeddedHostClosed;
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
            while (!connection.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessEmbeddedHostEvents();
                if (DateTime.UtcNow >= deadline)
                {
                    CloseCoreAsync(CancellationToken.None).GetAwaiter().GetResult();
                    throw new TimeoutException("The embedded WebView did not authenticate before the connection timeout.");
                }
                Thread.Sleep(10);
            }
            connection.GetAwaiter().GetResult();
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
    public void SetCustomParameters(string parameters)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(parameters);
        _customBrowserParameters = parameters.Length == 0 ? null : parameters;
        _customBrowserArguments = WebUiCommandLine.Split(parameters);
    }

    internal void SetAllowedPermissions(DesktopPermissionGrant permissions) => _allowedPermissions = permissions;

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
    public void SetPosition(uint x, uint y)
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
        return Encoding.UTF8.GetString(response.AsSpan(0, Math.Min(response.Length, responseBufferSize)));
    }

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

    internal bool HasEmbeddedHost => _embeddedHost is not null;

    internal bool RequiresMainThreadEventPump => _embeddedHost is IWebUiMainThreadHost;

    internal bool IsExecutingCallback => ReferenceEquals(CallbackWindow.Value, this);

    internal void NotifyAuthenticated() => _browserConnected.TrySetResult();

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

    internal ValueTask ResizePresentationAsync(uint width, uint height, CancellationToken cancellationToken)
    {
        _width = width;
        _height = height;
        return GetEmbeddedHost().SetSizeAsync(width, height, cancellationToken);
    }

    internal ValueTask MovePresentationAsync(uint x, uint y, CancellationToken cancellationToken)
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
            _runtimeOptions?.DiagnosticSink?.Invoke(new DesktopDiagnostic(
                DesktopErrorCategory.CapabilityDenied,
                "capability-not-admitted",
                "The presentation capability is not admitted for this session.",
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
            Width = _width ?? 800,
            Height = _height ?? 600,
            MinimumWidth = _minimumWidth,
            MinimumHeight = _minimumHeight,
            X = _x,
            Y = _y,
            Centered = _centered,
            Resizable = _resizable,
            Frameless = _frameless,
            Transparent = _transparent,
            Hidden = _hidden,
            Kiosk = _kiosk,
            HighContrast = _highContrast ?? WebUiSystemTheme.IsHighContrast,
            IconFile = _iconFile,
            ProfilePath = profilePath,
            CustomParameters = _customBrowserParameters,
            AllowedPermissions = _allowedPermissions,
        };
    }

    private void EmbeddedHostClosed(object? sender, EventArgs eventArgs)
    {
        if (sender is IWebUiEmbeddedHost host && ReferenceEquals(Volatile.Read(ref _embeddedHost), host))
        {
            _ = HandleEmbeddedHostClosedAsync(host);
        }
    }

    private async Task HandleEmbeddedHostClosedAsync(IWebUiEmbeddedHost host)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _embeddedHost, null, host), host))
        {
            return;
        }
        host.Closed -= EmbeddedHostClosed;
        await host.DisposeAsync().ConfigureAwait(false);
        _currentBrowser = WebUiBrowser.NoBrowser;
        if (_runtimeOptions is null)
        {
            await CloseCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private WebUiSession GetSession(Guid sessionId)
        => _sessions.TryGetValue(sessionId, out var session) && session.IsAuthenticated
            ? session
            : throw new InvalidOperationException("The WebUI browser connection is no longer active.");

    internal void RemoveBinding(string element, long registrationId)
    {
        if (_bindings.TryGetValue(element, out var registration) && registration.Id == registrationId)
        {
            ((ICollection<KeyValuePair<string, BindingRegistration>>)_bindings)
                .Remove(new KeyValuePair<string, BindingRegistration>(element, registration));
        }
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

        var basePath = Path.Combine(Path.GetTempPath(), "runic-desktop");
        Directory.CreateDirectory(basePath);
        _generatedProfilePath = Path.Combine(
            basePath,
            $"{browser.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_generatedProfilePath);
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
        var embeddedHost = Interlocked.Exchange(ref _embeddedHost, null);
        if (embeddedHost is not null)
        {
            embeddedHost.Closed -= EmbeddedHostClosed;
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
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
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
            for (var attempt = 0; attempt < 10 && !TryDeleteGeneratedProfile(profilePath); attempt++)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task HandleRequestAsync(HttpContext context) => PrepareResponseAsync(context, DispatchRequestAsync);

    private Task DispatchRequestAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path == BridgePath && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            return ServeBridgeAsync(context);
        }
        if (path == DesktopBootstrapPath && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            return ServeDesktopBootstrapAsync(context);
        }
        if (path == WebSocketPath && HttpMethods.IsGet(context.Request.Method))
        {
            return AcceptWebSocketAsync(context);
        }
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
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
                await SendVirtualContentAsync(context, path, response, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        var handler = Volatile.Read(ref _fileHandler);
        if (handler is not null)
        {
            var virtualContent = await handler(path, cancellationToken).ConfigureAwait(false);
            if (virtualContent is not null)
            {
                await SendVirtualContentAsync(context, path, virtualContent, cancellationToken).ConfigureAwait(false);
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

        if (path is "/favicon.ico" or "/favicon.svg")
        {
            var iconPath = ResolveLocalPath(path);
            if (iconPath is not null && File.Exists(iconPath))
            {
                await SendFileAsync(context, iconPath, path).ConfigureAwait(false);
                return;
            }

            if (path == "/favicon.ico")
            {
                Redirect(context, "/favicon.svg");
                return;
            }

            await SendTextAsync(context, DefaultIcon, "image/svg+xml").ConfigureAwait(false);
            return;
        }

        var localPath = ResolveLocalPath(path);
        if (localPath is not null && File.Exists(localPath))
        {
            await SendFileAsync(context, localPath, path).ConfigureAwait(false);
            return;
        }

        if (localPath is not null && Directory.Exists(localPath))
        {
            var index = FindPhysicalIndex(path, localPath);
            if (index is not null)
            {
                Redirect(context, index);
                return;
            }
        }

        await SendNotFoundAsync(context).ConfigureAwait(false);
    }

    private async Task ServeBridgeAsync(HttpContext context)
    {
        context.Response.ContentType = "text/javascript; charset=utf-8";
        var script = WebUiBridge.Script
            .Replace("__TOKEN__", _token.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__PORT__", Url?.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0", StringComparison.Ordinal)
            .Replace("__BASE_PATH__", _surface?.PathBase ?? string.Empty, StringComparison.Ordinal)
            .Replace("__SESSION_CREDENTIAL__", _sessionCredential, StringComparison.Ordinal)
            .Replace("__CUSTOM_WINDOW_DRAG__", _embeddedHost is not null && _frameless ? "true" : "false", StringComparison.Ordinal);
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(script);
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.WriteAsync(script, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private async Task ServeDesktopBootstrapAsync(HttpContext context)
    {
        context.Response.ContentType = "text/javascript; charset=utf-8";
        var webSocketPath = $"{_surface?.PathBase ?? string.Empty}{WebSocketPath}";
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
                  token: {{_token.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                  sessionCredential: {{EncodeJavaScriptString(_sessionCredential)}}
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

    private async Task AcceptWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }

        var acceptedSubprotocol = ValidateWebSocketAdmission(context);
        if (acceptedSubprotocol is null)
        {
            return;
        }

        lock (_connectionGate)
        {
            if (!AllowMultipleClients && _activeConnections > 0)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            _activeConnections++;
        }

        try
        {
            var clientId = GetOrCreateClient(context);
            using var socket = await context.WebSockets.AcceptWebSocketAsync(
                acceptedSubprotocol.Length == 0 ? null : acceptedSubprotocol).ConfigureAwait(false);
            var connectionId = checked((nuint)Interlocked.Increment(ref _nextConnectionId));
            var session = new WebUiSession(
                this,
                socket,
                clientId == 0 ? connectionId : clientId,
                connectionId,
                context.Request.Headers.Cookie.ToString());
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

    private async Task PrepareResponseAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers.CacheControl = NoCache;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if ((_allowedPermissions & DesktopPermissionGrant.MediaCapture) == 0)
        {
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=()";
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            var clientId = GetOrCreateClient(context);
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
        if (!offered.Contains(expected, StringComparer.Ordinal))
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

    private bool AllowMultipleClients => _securityPolicy?.AllowMultipleClients ?? WebUiApplication.MultiClient;

    private bool UseClientCookies => _securityPolicy?.UseClientCookies ?? WebUiApplication.UseCookies;

    private bool WaitForConnection => _runtimeOptions?.WaitForConnection ?? WebUiApplication.ShowWaitConnection;

    private TimeSpan ConnectionTimeout => _runtimeOptions?.ConnectionTimeout ?? WebUiApplication.ConnectionTimeout;

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
            return WebUiApplication.TryDeleteGeneratedProfile(path);
        }

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
            return false;
        }
    }

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
            : Path.GetFullPath(content, Volatile.Read(ref _rootFolder));
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
        if (path.Contains('\0'))
        {
            return null;
        }

        var root = Path.GetFullPath(Volatile.Read(ref _rootFolder));
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

    private void Redirect(HttpContext context, string location)
    {
        context.Response.StatusCode = StatusCodes.Status302Found;
        var pathBase = _surface?.PathBase;
        context.Response.Headers.Location = pathBase is { Length: > 0 } && location.StartsWith('/')
            ? pathBase + location
            : location;
    }

    private static async Task SendVirtualContentAsync(
        HttpContext context,
        string path,
        WebUiContent content,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = content.StatusCode;
        context.Response.ContentType = content.ContentType ?? GetContentType(path);
        if (content.Headers is not null)
        {
            foreach (var header in content.Headers)
            {
                context.Response.Headers[header.Key] = header.Value;
            }
        }
        context.Response.ContentLength = content.ContentLength;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await content.WriteBodyAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendFileAsync(HttpContext context, string filePath, string requestPath)
    {
        var file = new FileInfo(filePath);
        context.Response.ContentType = GetContentType(requestPath);
        context.Response.ContentLength = file.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.SendFileAsync(file.FullName, context.RequestAborted).ConfigureAwait(false);
        }
    }

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
