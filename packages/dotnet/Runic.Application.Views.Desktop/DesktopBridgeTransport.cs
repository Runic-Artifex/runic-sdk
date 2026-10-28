using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Desktop;

namespace Runic.Application.Views.Desktop;

/// <summary>Carries the Views protocol of one window over a Desktop surface.</summary>
/// <remarks>
/// <para>
/// Requests arrive on one capability, <c>__runicViews</c>, which <c>runic-desktop-views.js</c> calls with each
/// request's JSON. Pushes travel as raw data to the page's bridge, to every page of the surface or to one connection,
/// and reach the listeners of <c>__runicBridge.onMessage</c> without evaluating script, so a Content-Security-Policy
/// without <c>'unsafe-eval'</c> receives them. A connection is one authenticated WebSocket: its key is the Desktop
/// connection id, which is also its client key, and a reconnect is a new connection.
/// </para>
/// <para>
/// The requests of one connection are admitted in the order the page sent them. Disposing the transport removes the
/// capability and stops pushes, while the surface itself stays open. A window uses this to close its Views before
/// accepted operations have drained.
/// </para>
/// </remarks>
public sealed class DesktopBridgeTransport : IBridgeConnectionTransport, IDisposable
{
    // The capability that runic-desktop-views.js calls with one request.
    internal const string RequestCapability = "__runicViews";

    // The log category of Desktop Bridge delivery failures.
    internal const string LogCategory = "Runic.Application.Views.Desktop";

    private readonly object _gate = new();
    private readonly DesktopSurface _surface;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _broadcast;
    private readonly Func<ulong, ReadOnlyMemory<byte>, CancellationToken, Task<bool>> _send;
    private readonly ILogger? _logger;
    private Serving? _serving;
    private bool _disposed;

    /// <summary>Creates a transport for <paramref name="surface"/>.</summary>
    public DesktopBridgeTransport(DesktopSurface surface)
        : this(surface ?? throw new ArgumentNullException(nameof(surface)), logger: null)
    {
    }

    // Without a logger, delivery failures are written to System.Diagnostics.Trace.
    internal DesktopBridgeTransport(DesktopSurface surface, ILogger? logger)
        : this(surface ?? throw new ArgumentNullException(nameof(surface)),
            (message, token) => surface.PushViewsMessageAsync(message, token),
            (connection, message, token) => surface.PushViewsMessageAsync(connection, message, token), logger)
    {
    }

    // The push senders are replaceable for headless tests of delivery order.
    internal DesktopBridgeTransport(DesktopSurface surface, Func<ReadOnlyMemory<byte>, CancellationToken, Task> broadcast,
        Func<ulong, ReadOnlyMemory<byte>, CancellationToken, Task<bool>> send, ILogger? logger = null)
    {
        _surface = surface;
        _broadcast = broadcast;
        _send = send;
        _logger = logger;
    }

    internal bool IsServing
    {
        get { lock (_gate) return _serving is not null; }
    }

    /// <inheritdoc />
    public IDisposable Serve(IBridgeRequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_serving is not null) throw new InvalidOperationException("The transport already serves a request handler.");
            var serving = new Serving(this, handler);
            try
            {
                // Connection events first: a page that connects meanwhile is reported when it disconnects.
                serving.Connections = _surface.SubscribeConnectionEvents(connection =>
                {
                    if (connection.Kind == DesktopConnectionEventKind.Disconnected)
                        handler.ConnectionClosed(connection.SessionId.ToString(CultureInfo.InvariantCulture));
                });
                serving.Capability = _surface.RegisterCapability(RequestCapability,
                    (invocation, token) => HandleAsync(handler, invocation, token));
            }
            catch
            {
                serving.Release();
                throw;
            }
            _serving = serving;
            return serving;
        }
    }

    /// <inheritdoc />
    public async ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        lock (_gate) if (_disposed) return;
        cancellationToken.ThrowIfCancellationRequested();
        try { await _broadcast(message, cancellationToken).ConfigureAwait(false); }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed)) { }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ReportDeliveryFailure(error);
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> SendAsync(string connectionKey, ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionKey);
        lock (_gate) if (_disposed) return false;
        cancellationToken.ThrowIfCancellationRequested();
        if (!ulong.TryParse(connectionKey, NumberStyles.None, CultureInfo.InvariantCulture, out var connection)) return false;
        try { return await _send(connection, message, cancellationToken).ConfigureAwait(false); }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed)) { return false; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ReportDeliveryFailure(error);
            return false;
        }
    }

    /// <summary>Removes the capability and stops pushes; the surface stays open.</summary>
    public void Dispose()
    {
        Serving? serving;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            serving = _serving;
            _serving = null;
        }
        serving?.Release();
    }

    // PresentationSession.Id is WebUiEvent.ConnectionId, allocated for an authenticated WebSocket connection. It is
    // deliberately not the engine's private session GUID. Desktop has one browser client for a connection, so the
    // trusted client and connection identities are the same value; reconnecting receives a different key.
    private static async ValueTask<PresentationResult> HandleAsync(IBridgeRequestHandler handler,
        PresentationInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.ArgumentCount < 1) return PresentationResult.None;
        var key = invocation.Session.Id.ToString(CultureInfo.InvariantCulture);
        // The argument is valid only while the invocation is active; the handler reads it before it first yields, and
        // the copy keeps it for an awaited request.
        var request = new BridgeRequest(invocation.GetBytes(0).ToArray(), key, key);
        var reply = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return PresentationResult.FromString(Encoding.UTF8.GetString(reply.Span));
    }

    private void ReportDeliveryFailure(Exception error)
    {
        if (_logger is null) Trace.TraceError($"Views push delivery failed: {error}");
        else DesktopLog.PushDeliveryFailed(_logger, error, error.GetType().FullName ?? error.GetType().Name);
    }

    private void Remove(Serving serving)
    {
        lock (_gate)
            if (ReferenceEquals(_serving, serving)) _serving = null;
    }

    private sealed class Serving(DesktopBridgeTransport owner, IBridgeRequestHandler handler) : IDisposable
    {
        private DesktopBridgeTransport? _owner = owner;
        private IDisposable? _connections;
        private IDisposable? _capability;

        public IBridgeRequestHandler Handler { get; } = handler;

        public IDisposable Connections { set => _connections = value; }

        public IDisposable Capability { set => _capability = value; }

        // The lease: stops serving and removes the capability.
        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Remove(this);
            Release();
        }

        public void Release()
        {
            Interlocked.Exchange(ref _capability, null)?.Dispose();
            Interlocked.Exchange(ref _connections, null)?.Dispose();
        }
    }
}
