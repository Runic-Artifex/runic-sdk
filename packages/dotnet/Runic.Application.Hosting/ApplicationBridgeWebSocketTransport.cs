using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Runic.Application.Bridge;

namespace Runic.Application.Hosting;

/// <summary>
/// Adapts one authoritative <see cref="ApplicationBridgeSession"/> to a binary
/// WebSocket endpoint without recreating protocol, revision, or reconnect state.
/// </summary>
public sealed class ApplicationBridgeWebSocketTransport : IAsyncDisposable
{
    private readonly ApplicationBridgeSession _session;
    private readonly ApplicationBridgeWebSocketOptions _options;
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    private readonly object _gate = new();
    private readonly HashSet<ActiveConnection> _connections = [];
    private ActiveConnection? _active;
    private ActiveConnection? _inFlightConnection;
    private List<BridgeHostEnvelope>? _inFlightEvents;
    private long _acceptedConnectionEpoch = -1;
    private int _disposed;

    /// <summary>Creates an ASP.NET Core adapter for an application-owned session.</summary>
    public ApplicationBridgeWebSocketTransport(
        ApplicationBridgeSession session,
        ApplicationBridgeWebSocketOptions? options = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? new();
        _options.Validate();
        _session.EventProduced += OnEventProduced;
    }

    /// <summary>Gets the endpoint configuration.</summary>
    public ApplicationBridgeWebSocketOptions Options => _options;
    /// <summary>Raised after an initialization snapshot is queued for the active connection.</summary>
    public event EventHandler? Activated;

    /// <summary>Runs one accepted binary WebSocket until it disconnects or violates the contract.</summary>
    public async Task RunAsync(WebSocket webSocket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(webSocket);
        var connection = new ActiveConnection(webSocket, _options.Limits.MaxPendingCommands + 1, cancellationToken);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                connection.Dispose();
                ThrowIfDisposed();
            }
            _connections.Add(connection);
        }
        Task sender = SendAsync(connection);
        try
        {
            while (!connection.Token.IsCancellationRequested && webSocket.State == WebSocketState.Open)
            {
                byte[]? frame = await ReceiveFrameAsync(webSocket, connection.Token).ConfigureAwait(false);
                if (frame is null) break;
                if (!ApplicationBridgeCodec.TryDecodeClient(frame, out BridgeClientEnvelope? envelope, _options.Limits) ||
                    !await DispatchAsync(connection, envelope!, connection.Token).ConfigureAwait(false))
                {
                    await CloseAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "Invalid Application Bridge frame.").ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (connection.Token.IsCancellationRequested) { }
        catch (InvalidDataException)
        {
            await CloseAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "Invalid Application Bridge frame.").ConfigureAwait(false);
        }
        catch (WebSocketException) { }
        finally
        {
            connection.Complete();
            lock (_gate)
            {
                _connections.Remove(connection);
                if (ReferenceEquals(_active, connection)) _active = null;
                if (ReferenceEquals(_inFlightConnection, connection))
                {
                    _inFlightConnection = null;
                    _inFlightEvents = null;
                }
            }
            try { await sender.ConfigureAwait(false); }
            catch (OperationCanceledException) when (connection.Token.IsCancellationRequested) { }
            catch (WebSocketException) { }
            connection.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _session.EventProduced -= OnEventProduced;
        lock (_gate)
        {
            foreach (ActiveConnection connection in _connections) connection.Cancel();
            _active = null;
            _inFlightConnection?.Cancel();
            _inFlightConnection = null;
            _inFlightEvents = null;
        }
        await _dispatch.WaitAsync().ConfigureAwait(false);
        _dispatch.Release();
        _dispatch.Dispose();
    }

    private async Task<bool> DispatchAsync(ActiveConnection connection, BridgeClientEnvelope envelope, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        await _dispatch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            if (!CanAccept(connection, envelope)) return false;
            lock (_gate)
            {
                _inFlightConnection = connection;
                _inFlightEvents = [];
            }
            BridgeHostEnvelope response = await _session.DispatchAsync(envelope, cancellationToken).ConfigureAwait(false);
            ActiveConnection? replaced = null;
            bool queued = true;
            bool activated = false;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0) return false;
                List<BridgeHostEnvelope> events = _inFlightEvents ?? [];
                _inFlightConnection = null;
                _inFlightEvents = null;
                if (envelope.Kind == "initialize" && response.Kind == "snapshot")
                {
                    replaced = _active;
                    _active = connection;
                    connection.ConnectionEpoch = envelope.ConnectionEpoch;
                    _acceptedConnectionEpoch = envelope.ConnectionEpoch;
                    activated = true;
                    if (ReferenceEquals(replaced, connection)) replaced = null;
                }
                List<BridgeHostEnvelope> output = [response, .. events];
                output.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
                // Queue the correlated response before releasing event routing:
                // EventProduced may otherwise publish sequence N+1 ahead of N.
                foreach (BridgeHostEnvelope item in output)
                    if (!connection.TryQueue(item)) queued = false;
            }
            replaced?.Cancel();
            if (activated)
            {
                try { Activated?.Invoke(this, EventArgs.Empty); }
                catch { }
            }
            return queued;
        }
        catch (ObjectDisposedException) { return false; }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inFlightConnection, connection))
                {
                    _inFlightConnection = null;
                    _inFlightEvents = null;
                }
            }
            _dispatch.Release();
        }
    }

    private bool CanAccept(ActiveConnection connection, BridgeClientEnvelope envelope)
    {
        lock (_gate)
        {
            if (connection.ConnectionEpoch is long epoch)
            {
                return envelope.ConnectionEpoch == epoch ||
                    (envelope.Kind == "initialize" && envelope.ConnectionEpoch > _acceptedConnectionEpoch);
            }
            return envelope.Kind == "initialize" && envelope.ConnectionEpoch > _acceptedConnectionEpoch;
        }
    }

    private void OnEventProduced(object? sender, BridgeHostEnvelope message)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_gate)
        {
            if (_inFlightConnection is not null)
            {
                if (_inFlightEvents is null || _inFlightEvents.Count >= _options.Limits.MaxPendingCommands)
                {
                    _inFlightConnection.Cancel();
                    return;
                }
                _inFlightEvents.Add(message);
                return;
            }
            _active?.TryQueue(message);
        }
    }

    private async Task SendAsync(ActiveConnection connection)
    {
        try
        {
            await foreach (BridgeHostEnvelope message in connection.Outbound.Reader.ReadAllAsync(connection.Token).ConfigureAwait(false))
            {
                byte[] frame = ApplicationBridgeCodec.EncodeHost(message, _options.Limits);
                await connection.Socket.SendAsync(frame, WebSocketMessageType.Binary, true, connection.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (connection.Token.IsCancellationRequested) { }
    }

    private async Task<byte[]?> ReceiveFrameAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(_options.Limits.MaxFrameBytes, 8192));
        try
        {
            var output = new ArrayBufferWriter<byte>();
            while (true)
            {
                ValueWebSocketReceiveResult result = await webSocket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType != WebSocketMessageType.Binary || output.WrittenCount + result.Count > _options.Limits.MaxFrameBytes)
                    throw new InvalidDataException("The Application Bridge frame is not a bounded binary message.");
                output.Write(buffer.AsSpan(0, result.Count));
                if (result.EndOfMessage) return output.WrittenSpan.ToArray();
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task CloseAsync(WebSocket webSocket, WebSocketCloseStatus status, string description)
    {
        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await webSocket.CloseOutputAsync(status, description, CancellationToken.None).ConfigureAwait(false); }
            catch (WebSocketException) { }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class ActiveConnection : IDisposable
    {
        private readonly CancellationTokenSource _shutdown;

        public ActiveConnection(WebSocket socket, int outboundCapacity, CancellationToken cancellationToken)
        {
            Socket = socket;
            _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Outbound = Channel.CreateBounded<BridgeHostEnvelope>(new BoundedChannelOptions(outboundCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                AllowSynchronousContinuations = false,
            });
        }

        public WebSocket Socket { get; }
        public Channel<BridgeHostEnvelope> Outbound { get; }
        public CancellationToken Token => _shutdown.Token;
        public long? ConnectionEpoch { get; set; }

        public bool TryQueue(BridgeHostEnvelope message)
        {
            if (Outbound.Writer.TryWrite(message)) return true;
            Cancel();
            return false;
        }

        public void Complete() => Outbound.Writer.TryComplete();
        public void Cancel() => _shutdown.Cancel();
        public void Dispose() => _shutdown.Dispose();
    }
}

/// <summary>Maps the binary Application Bridge WebSocket endpoint.</summary>
public static class ApplicationBridgeWebSocketEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps an endpoint that accepts only configured-origin WebSocket requests.
    /// Call <c>UseWebSockets</c> before mapping this endpoint.
    /// </summary>
    public static IEndpointConventionBuilder MapRunicApplicationBridge(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        ApplicationBridgeWebSocketTransport transport)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(transport);
        return endpoints.Map(pattern, async context =>
        {
            if (!transport.Options.IsOriginAllowed(context.Request.Headers.Origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await transport.RunAsync(socket, context.RequestAborted).ConfigureAwait(false);
        });
    }
}
