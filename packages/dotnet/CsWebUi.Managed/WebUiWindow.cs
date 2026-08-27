using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using CsWebUi.Managed.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;

namespace CsWebUi.Managed;

/// <summary>Owns one managed CS-WebUI server and its browser connections.</summary>
public sealed class WebUiWindow : IDisposable, IAsyncDisposable
{
    private const string BridgePath = "/webui.js";
    private const string WebSocketPath = "/_webui_ws_connect";
    private const string AuthCookieName = "webui_auth";
    private const string NoCache = "no-cache, no-store, must-revalidate, private, max-age=0";
    private const string AccessDenied = "<html><head><title>Access Denied</title><script src=\"/webui.js\"></script></head><body><h2>&#9888; Access Denied</h2><p>This content is already in use and multi-client mode is disabled.</p></body></html>";
    private const string ResourceUnavailable = "<html><head><title>Resource Not Available</title><script src=\"/webui.js\"></script></head><body><h2>&#9888; Resource Not Available</h2><p>The requested resource is not available.</p></body></html>";
    private const string DefaultIcon = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"500\" viewBox=\"0 0 375 375\" height=\"500\"><path fill=\"#2a6699\" d=\"M22 22h330v330H22z\"/></svg>";
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly ConcurrentDictionary<string, BindingRegistration> _bindings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, WebUiSession> _sessions = new();
    private readonly ConcurrentDictionary<string, nuint> _clients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _connectionGate = new();
    private CancellationTokenSource _shutdown = new();
    private readonly uint _token = CreateToken();

    private WebApplication? _application;
    private string _rootFolder;
    private string? _embeddedHtml;
    private string? _entryFile;
    private Uri? _externalUrl;
    private WebUiFileHandler? _fileHandler;
    private bool _allowIndexFallback;
    private bool _isPublic;
    private int _requestedPort;
    private int _activeConnections;
    private long _nextClientId;
    private long _nextConnectionId;
    private long _nextEventNumber;
    private long _nextRegistrationId;
    private int _disposed;

    /// <summary>Creates a managed window using the current application default root folder.</summary>
    public WebUiWindow()
    {
        _rootFolder = WebUiApplication.GetDefaultRootFolder();
    }

    /// <summary>Gets the local server URL after the window has started.</summary>
    public Uri? Url { get; private set; }

    /// <summary>Gets the configured or bound HTTP port.</summary>
    public nuint Port => checked((nuint)(Url?.Port ?? _requestedPort));

    /// <summary>Gets whether the server accepts connections on non-loopback interfaces.</summary>
    public bool IsPublic => _isPublic;

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
            if (_application is not null)
            {
                return Url ?? throw new InvalidOperationException("The running managed window has no server URL.");
            }

            if (_shutdown.IsCancellationRequested)
            {
                _shutdown.Dispose();
                _shutdown = new CancellationTokenSource();
            }

            ConfigureContent(content);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                var address = _isPublic ? IPAddress.Any : IPAddress.Loopback;
                options.Listen(address, _requestedPort);
            });

            var application = builder.Build();
            application.UseWebSockets();
            application.Use(PrepareResponseAsync);
            application.MapMethods(BridgePath, [HttpMethods.Get, HttpMethods.Head], ServeBridgeAsync);
            application.MapGet(WebSocketPath, AcceptWebSocketAsync);
            application.MapMethods("/{**path}", [HttpMethods.Get, HttpMethods.Head], ServeContentAsync);

            _application = application;
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            var addresses = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = addresses?.SingleOrDefault()
                ?? throw new InvalidOperationException("Kestrel did not publish a listening address.");

            var boundAddress = new Uri(address, UriKind.Absolute);
            Url = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), boundAddress.Port).Uri;
            WebUiApplication.Started(this);
            return Url;
        }
        catch
        {
            if (_application is not null)
            {
                await _application.DisposeAsync().ConfigureAwait(false);
                _application = null;
            }

            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Starts the managed server and opens its URL in the default browser.</summary>
    public async Task<Uri> ShowAsync(string content, CancellationToken cancellationToken = default)
    {
        var url = await StartServerAsync(content, cancellationToken).ConfigureAwait(false);
        var browserUrl = GetBrowserUrl(url);
        Process.Start(new ProcessStartInfo(browserUrl.AbsoluteUri) { UseShellExecute = true });
        return url;
    }

    /// <summary>Starts the managed server and opens its URL in the default browser.</summary>
    public Uri Show(string content) => ShowAsync(content).GetAwaiter().GetResult();

    /// <summary>Sets the local root used for files and folder-mode index discovery.</summary>
    public void SetRootFolder(string path)
    {
        if (!TrySetRootFolder(path))
        {
            throw new DirectoryNotFoundException($"The managed WebUI root folder does not exist: {path}");
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
        if (port > ushort.MaxValue || _application is not null)
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
        if (_application is not null)
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
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await CloseCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await CloseCoreAsync(CancellationToken.None).ConfigureAwait(false);
        _shutdown.Dispose();
        _lifecycleGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task CloseCoreAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var application = _application;
            if (application is null)
            {
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
            try
            {
                await application.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await application.DisposeAsync().ConfigureAwait(false);
                _application = null;
                Url = null;
                lock (_connectionGate)
                {
                    _activeConnections = 0;
                }
                WebUiApplication.Stopped(this);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal uint Token => _token;

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
                ? WebUiResult.FromBoolean(false)
                : WebUiResult.None;
        }

        if (!_bindings.TryGetValue(element, out var registration))
        {
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
        try
        {
            return await registration.Handler(webUiEvent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            webUiEvent.Invalidate();
        }
    }

    private WebUiSession GetExecutionSession()
        => _sessions.Values.FirstOrDefault(static session => session.IsAuthenticated)
            ?? throw new InvalidOperationException("No authenticated WebUI browser is connected.");

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

    private async Task ServeContentAsync(HttpContext context)
    {
        var path = GetDecodedPath(context.Request.Path);
        var handler = Volatile.Read(ref _fileHandler);
        if (handler is not null)
        {
            var virtualContent = await handler(path, context.RequestAborted).ConfigureAwait(false);
            if (virtualContent is not null)
            {
                await SendVirtualContentAsync(context, path, virtualContent).ConfigureAwait(false);
                return;
            }

            var virtualIndex = await FindVirtualIndexAsync(handler, path, context.RequestAborted).ConfigureAwait(false);
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
            .Replace("__PORT__", Url?.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0", StringComparison.Ordinal);
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(script);
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.WriteAsync(script, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private async Task AcceptWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }

        lock (_connectionGate)
        {
            if (!WebUiApplication.MultiClient && _activeConnections > 0)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            _activeConnections++;
        }

        try
        {
            var clientId = GetOrCreateClient(context);
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
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
        context.Response.Headers.AccessControlAllowOrigin = "*";
        context.Response.Headers.CacheControl = NoCache;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (!context.WebSockets.IsWebSocketRequest)
        {
            var clientId = GetOrCreateClient(context);
            if (WebUiApplication.UseCookies &&
                !WebUiApplication.MultiClient &&
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
        if (!WebUiApplication.UseCookies)
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
            Path = "/",
        });
        return clientId;
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
        catch (Exception exception) when (exception is IOException or WebSocketException or ObjectDisposedException)
        {
        }
    }

    private static async Task DisposeSessionIgnoringFailureAsync(WebUiSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or WebSocketException or ObjectDisposedException)
        {
        }
    }

    private static void Redirect(HttpContext context, string location)
    {
        context.Response.StatusCode = StatusCodes.Status302Found;
        context.Response.Headers.Location = location;
    }

    private static async Task SendVirtualContentAsync(HttpContext context, string path, WebUiContent content)
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
        context.Response.ContentLength = content.Body.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(content.Body, context.RequestAborted).ConfigureAwait(false);
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
