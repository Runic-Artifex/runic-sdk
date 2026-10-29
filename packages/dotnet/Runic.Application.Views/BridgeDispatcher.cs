using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

// The Views protocol 4 producer's request dispatcher of one window (specs/application/README.md). It serves the
// window's IBridgeTransport and owns what belongs to a connection rather than to a route:
//
// - the handshake: hello, the protocol and implementation check, and no-hello for requests before it;
// - the envelope: the input limit, the JSON profile and the members of each kind (BridgeRequestEnvelope);
// - routing: a request for a route goes to the route's handler (RegisterRoute); a route that nothing serves, because it
//   was never registered or was released, answers released; reserved routes (runic.*) belong to host services;
// - pushes: broadcasts, targeted pushes and, for a transport without targeted pushes, the per-connection mailbox that
//   the host script drains with receive;
// - the connection lifecycle: the transport reports closed connections, and the dispatcher releases their sessions and
//   tells the window's observers (OnConnectionClosed).
//
// Every bridge of a window shares the dispatcher of the window's transport (For).
internal sealed class BridgeDispatcher : IBridgeRequestHandler
{
    internal const int ProtocolVersion = 4;
    internal const string ImplementationName = "Runic.Application.Views";
    // A pending receive answers with an empty list after this long (specs/application/README.md, "receive").
    internal static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(25);
    // A connection's mailbox keeps at most this much; older pushes are dropped beyond it, with a warning.
    internal const int MaximumMailboxMessages = 1_024;
    internal const long MaximumMailboxBytes = 4 * 1_048_576;
    // Sessions of closed connections, which a hello cannot reuse.
    private const int RetainedClosedSessions = 4_096;

    private static readonly ConditionalWeakTable<IBridgeTransport, BridgeDispatcher> Dispatchers = new();

    private readonly object _gate = new();
    private readonly IBridgeTransport _transport;
    private readonly IBridgeConnectionTransport? _targeted;
    private readonly Dictionary<string, BridgeConnection> _connections = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closedSessions = new(StringComparer.Ordinal);
    private readonly Queue<string> _closedSessionOrder = new();
    private readonly Dictionary<string, IBridgeRouteHandler> _routes = new(StringComparer.Ordinal);
    private readonly List<Action<string>> _closedObservers = [];
    // Answers status, watch, unwatch, cancel and stream of a route nothing serves, for an operation the window knows.
    private Func<BridgeRouteRequest, ReadOnlyMemory<byte>?>? _releasedOperations;
    private int _maximumInputBytes = WindowContentSessionOptions.DefaultMaximumInputBytes;
    private ILogger _logger = TraceFallbackLogger.Instance;
    private TimeProvider _timeProvider = TimeProvider.System;

    private BridgeDispatcher(IBridgeTransport transport)
    {
        _transport = transport;
        _targeted = transport as IBridgeConnectionTransport;
        // A closing window can still attach content while accepted work drains, after its host disposed the
        // transport. Its dispatcher serves nothing then.
        try { _ = transport.Serve(this); }
        catch (ObjectDisposedException) { }
    }

    // The dispatcher of a transport, created and served on first use.
    internal static BridgeDispatcher For(IBridgeTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (Dispatchers.TryGetValue(transport, out var existing)) return existing;
        lock (Dispatchers)
        {
            if (Dispatchers.TryGetValue(transport, out existing)) return existing;
            var dispatcher = new BridgeDispatcher(transport);
            Dispatchers.Add(transport, dispatcher);
            return dispatcher;
        }
    }

    internal IBridgeTransport Transport => _transport;

    internal TimeProvider TimeProvider => _timeProvider;

    internal ILogger Logger => _logger;

    // Whether pushes to one connection travel through the mailbox.
    internal bool UsesMailbox => _targeted is null;

    // Applies the window's options. The window session calls this once it was created.
    internal void Configure(int maximumInputBytes, ILogger logger, TimeProvider timeProvider)
    {
        _maximumInputBytes = maximumInputBytes;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    // Serves a route: requests that name it go to the handler until the lease is disposed; after that they answer
    // released. A reserved route (runic.*) receives every request kind; a View's route receives the View request kinds
    // with checked envelopes.
    internal IDisposable RegisterRoute(string route, IBridgeRouteHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!BridgeRequestEnvelope.IsRoute(route))
            throw new ArgumentException($"'{route}' is not a Views route name.", nameof(route));
        lock (_gate)
        {
            if (!_routes.TryAdd(route, handler))
                throw new InvalidOperationException($"Route '{route}' is already served.");
        }
        return new RouteLease(this, route, handler);
    }

    // Whether a handler serves the route.
    internal bool Serves(string route)
    {
        lock (_gate) return _routes.ContainsKey(route);
    }

    // Answers the operation requests (status, watch, unwatch, cancel, stream) of routes nothing serves: answer returns
    // the reply for an operation the window knows, or null, which answers released. One per window, until disposed.
    internal IDisposable ServeReleasedOperations(Func<BridgeRouteRequest, ReadOnlyMemory<byte>?> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        lock (_gate) _releasedOperations = answer;
        return new ReleasedOperationsLease(this, answer);
    }

    private sealed class ReleasedOperationsLease(BridgeDispatcher owner, Func<BridgeRouteRequest, ReadOnlyMemory<byte>?> answer) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
                if (ReferenceEquals(owner._releasedOperations, answer)) owner._releasedOperations = null;
        }
    }

    // Calls observer with the key of each connection that closes, until the lease is disposed.
    internal IDisposable OnConnectionClosed(Action<string> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate) _closedObservers.Add(observer);
        return new ObserverLease(this, observer);
    }

    // The connection, if it is open and completed its hello.
    internal BridgeConnection? FindConnection(string connectionKey)
    {
        lock (_gate)
            return _connections.TryGetValue(connectionKey, out var connection) && connection.HelloCompleted ? connection : null;
    }

    // Sends a push to every connection. Nothing is sent while no connection completed its hello.
    internal async ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            if (!_connections.Values.Any(static connection => connection.HelloCompleted)) return;
        try { await _transport.BroadcastAsync(message, cancellationToken).ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
    }

    // Sends a push to one connection that completed its hello: through the transport when it can address the
    // connection, or through the connection's mailbox. False when the connection is not open.
    internal async ValueTask<bool> SendAsync(string connectionKey, ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionKey);
        BridgeConnection? connection;
        lock (_gate)
        {
            if (!_connections.TryGetValue(connectionKey, out connection) || !connection.HelloCompleted) return false;
            if (_targeted is null) return Enqueue(connection, message);
        }
        try { return await _targeted.SendAsync(connectionKey, message, cancellationToken).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return false; }
    }

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>> HandleAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var connection = Connect(request);
            if (request.Message.Length > _maximumInputBytes)
                return Reply(BridgeMessages.Rejected("invalid-input",
                    $"The request is larger than the window's input limit of {_maximumInputBytes} bytes."));
            var envelope = BridgeRequestEnvelope.TryRead(request.Message, out var error);
            if (envelope is null) return Reply(BridgeMessages.Rejected("invalid-request", error!));
            var disposeEnvelope = true;
            try
            {
                if (envelope.Kind == "hello") return Reply(Hello(connection, envelope.Root));
                if (!connection.HelloCompleted)
                {
                    if (!connection.NoHelloLogged)
                    {
                        connection.NoHelloLogged = true;
                        ViewsLog.RequestBeforeHello(_logger, envelope.Kind, request.ConnectionKey);
                    }
                    return Reply(BridgeMessages.Rejected("no-hello", "The connection has not completed a hello."));
                }
                if (envelope.Kind == "receive") return Receive(connection, cancellationToken);
                if (envelope.Route is not { } route || !(envelope.IsServiceRoute || BridgeRequestEnvelope.IsViewKind(envelope.Kind)))
                    return Reply(BridgeMessages.Rejected("invalid-request", $"'{envelope.Kind}' is not a request kind of this producer."));
                IBridgeRouteHandler? handler;
                lock (_gate) _routes.TryGetValue(route, out handler);
                if (handler is null)
                {
                    // Operations belong to the window: one whose route was released stays observable.
                    Func<BridgeRouteRequest, ReadOnlyMemory<byte>?>? operations;
                    lock (_gate) operations = _releasedOperations;
                    return new(BridgeRequestEnvelope.IsOperationObservation(envelope.Kind)
                        && operations?.Invoke(new BridgeRouteRequest(envelope, connection, this)) is { } known
                        ? known : BridgeMessages.Released);
                }
                var routed = handler.HandleAsync(new BridgeRouteRequest(envelope, connection, this), cancellationToken);
                if (routed.IsCompletedSuccessfully) return new(routed.Result);
                disposeEnvelope = false;
                return AwaitRoute(routed, envelope);
            }
            finally
            {
                if (disposeEnvelope) envelope.Dispose();
            }
        }
        catch (Exception error)
        {
            ViewsLog.RequestFailed(_logger, error, error.GetType().FullName ?? error.GetType().Name);
            return Reply(BridgeMessages.Failure("failed", null, "The request failed."));
        }
    }

    /// <inheritdoc />
    public void ConnectionClosed(string connectionKey)
    {
        ArgumentNullException.ThrowIfNull(connectionKey);
        Action<string>[] observers;
        lock (_gate)
        {
            if (!_connections.Remove(connectionKey, out var connection)) return;
            connection.IsOpen = false;
            if (connection.Session is { } session) RetainClosedSession(session);
            connection.Mailbox.Clear();
            connection.MailboxBytes = 0;
            var pending = connection.PendingReceive;
            connection.PendingReceive = null;
            pending?.TrySetResult(null);
            observers = [.. _closedObservers];
        }
        foreach (var observer in observers)
        {
            try { observer(connectionKey); }
            catch (Exception error)
            {
                ViewsLog.RequestFailed(_logger, error, error.GetType().FullName ?? error.GetType().Name);
            }
        }
    }

    private BridgeConnection Connect(BridgeRequest request)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(request.ConnectionKey, out var connection)) return connection;
            connection = new BridgeConnection(request.ConnectionKey, request.ClientKey);
            _connections.Add(request.ConnectionKey, connection);
            return connection;
        }
    }

    private byte[] Hello(BridgeConnection connection, JsonElement root)
    {
        // The protocol first: a hello of another version is refused with protocol-mismatch whatever its other members.
        if (!root.TryGetProperty("protocol", out var protocolElement)
            || !BridgeRequestEnvelope.TryReadInteger(protocolElement, out var protocol) || protocol < 0)
            return BridgeMessages.Rejected("invalid-request", "The hello has no protocol version.", hello: true);
        if (protocol != ProtocolVersion)
        {
            lock (_gate)
            {
                if (!connection.MismatchLogged)
                {
                    connection.MismatchLogged = true;
                    ViewsLog.ProtocolMismatch(_logger, protocol, ProtocolVersion, connection.Key);
                }
            }
            return BridgeMessages.Failure("failed", "protocol-mismatch",
                $"This .NET host implements Views protocol {ProtocolVersion}, and the page's client implements protocol {protocol}. " +
                "Use the Runic packages of one release: update every Runic NuGet and npm package together, rebuild so the generated clients are regenerated, and reload the page.",
                hello: true);
        }
        if (!root.TryGetProperty("session", out var sessionElement) || sessionElement.ValueKind != JsonValueKind.String
            || !BridgeRequestEnvelope.IsIdentifier(sessionElement.GetString()!))
            return BridgeMessages.Rejected("invalid-request", "The hello's session is not an identifier.", hello: true);
        if (!root.TryGetProperty("client", out var client) || client.ValueKind != JsonValueKind.Object
            || !TryReadName(client, "name", 128, out var clientName) || !TryReadName(client, "version", 64, out var clientVersion))
            return BridgeMessages.Rejected("invalid-request", "The hello's client is not {name, version}.", hello: true);
        var session = sessionElement.GetString()!;
        lock (_gate)
        {
            if (connection.HelloCompleted)
                return BridgeMessages.Rejected("invalid-request", "The connection already completed a hello.", hello: true);
            if (_closedSessions.Contains(session))
                return BridgeMessages.Failure("released", null, "The connection of this session closed.", hello: true);
            if (_connections.Values.Any(other => other != connection && other.Session == session))
                return BridgeMessages.Rejected("invalid-request", "Another connection uses this session.", hello: true);
            connection.Session = session;
            connection.ClientName = clientName;
            connection.ClientVersion = clientVersion;
            connection.HelloCompleted = true;
        }
        return BridgeMessages.Hello();
    }

    private static bool TryReadName(JsonElement owner, string name, int maximumLength, out string value)
    {
        value = "";
        if (!owner.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString()!;
        return value.Length >= 1 && value.Length <= maximumLength;
    }

    private ValueTask<ReadOnlyMemory<byte>> Receive(BridgeConnection connection, CancellationToken cancellationToken)
    {
        // A transport that pushes to one connection directly never queues anything.
        if (_targeted is not null) return new(BridgeMessages.EmptyReceive);
        TaskCompletionSource<byte[]?> pending;
        lock (_gate)
        {
            if (connection.Mailbox.Count > 0) return new(Drain(connection));
            // At most one receive is pending per connection; another answers at once.
            if (connection.PendingReceive is not null || !connection.IsOpen) return new(BridgeMessages.EmptyReceive);
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.PendingReceive = pending;
        }
        return WaitAsync(connection, pending, cancellationToken);
    }

    private async ValueTask<ReadOnlyMemory<byte>> WaitAsync(BridgeConnection connection,
        TaskCompletionSource<byte[]?> pending, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(ReceiveTimeout, _timeProvider);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        await using var registration = stop.Token.Register(() =>
        {
            lock (_gate)
                if (connection.PendingReceive == pending) connection.PendingReceive = null;
            pending.TrySetResult(null);
        }).ConfigureAwait(false);
        var messages = await pending.Task.ConfigureAwait(false);
        return messages ?? BridgeMessages.EmptyReceive;
    }

    // Queues a targeted push; the gate is held.
    private bool Enqueue(BridgeConnection connection, ReadOnlyMemory<byte> message)
    {
        if (!connection.IsOpen) return false;
        connection.Mailbox.Enqueue(message.ToArray());
        connection.MailboxBytes += message.Length;
        var dropped = 0;
        while (connection.Mailbox.Count > 1
            && (connection.Mailbox.Count > MaximumMailboxMessages || connection.MailboxBytes > MaximumMailboxBytes))
        {
            connection.MailboxBytes -= connection.Mailbox.Dequeue().Length;
            dropped++;
        }
        if (dropped > 0 && !connection.MailboxOverflowLogged)
        {
            connection.MailboxOverflowLogged = true;
            ViewsLog.MailboxOverflowed(_logger, connection.Key, dropped);
        }
        if (connection.PendingReceive is { } pending)
        {
            connection.PendingReceive = null;
            pending.TrySetResult(Drain(connection));
        }
        return true;
    }

    // Takes the mailbox's messages as a receive reply; the gate is held.
    private static byte[] Drain(BridgeConnection connection)
    {
        var messages = connection.Mailbox.ToArray();
        connection.Mailbox.Clear();
        connection.MailboxBytes = 0;
        return BridgeMessages.Receive(messages);
    }

    private void RetainClosedSession(string session)
    {
        if (!_closedSessions.Add(session)) return;
        _closedSessionOrder.Enqueue(session);
        while (_closedSessionOrder.Count > RetainedClosedSessions) _closedSessions.Remove(_closedSessionOrder.Dequeue());
    }

    private async ValueTask<ReadOnlyMemory<byte>> AwaitRoute(ValueTask<ReadOnlyMemory<byte>> routed,
        BridgeRequestEnvelope envelope)
    {
        try { return await routed.ConfigureAwait(false); }
        catch (Exception error)
        {
            ViewsLog.RequestFailed(_logger, error, error.GetType().FullName ?? error.GetType().Name);
            return BridgeMessages.Failure("failed", null, "The request failed.");
        }
        finally { envelope.Dispose(); }
    }

    private static ValueTask<ReadOnlyMemory<byte>> Reply(byte[] reply) => new(reply);

    private void Remove(string route, IBridgeRouteHandler handler)
    {
        lock (_gate)
            if (_routes.TryGetValue(route, out var current) && ReferenceEquals(current, handler)) _routes.Remove(route);
    }

    private sealed class RouteLease(BridgeDispatcher owner, string route, IBridgeRouteHandler handler) : IDisposable
    {
        private BridgeDispatcher? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Remove(route, handler);
    }

    private sealed class ObserverLease(BridgeDispatcher owner, Action<string> observer) : IDisposable
    {
        private BridgeDispatcher? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } dispatcher) return;
            lock (dispatcher._gate) dispatcher._closedObservers.Remove(observer);
        }
    }
}

// One connection of a window, from its first request until the transport reports it closed.
internal sealed class BridgeConnection(string key, string clientKey)
{
    internal string Key { get; } = key;
    internal string ClientKey { get; } = clientKey;
    // The session the hello named.
    internal string? Session { get; set; }
    internal string? ClientName { get; set; }
    internal string? ClientVersion { get; set; }
    internal bool HelloCompleted { get; set; }
    internal bool IsOpen { get; set; } = true;
    internal bool MismatchLogged { get; set; }
    internal bool NoHelloLogged { get; set; }
    internal bool MailboxOverflowLogged { get; set; }
    internal Queue<byte[]> Mailbox { get; } = new();
    internal long MailboxBytes { get; set; }
    internal TaskCompletionSource<byte[]?>? PendingReceive { get; set; }

    // The highest edit number admitted per route and member (specs/application/README.md, "Edits").
    private readonly Dictionary<(string Route, string Member), long> _edits = [];

    // Admits a set request's edit number: true, and recorded, when it is higher than every edit admitted before for
    // the route and member on this connection.
    internal bool AdmitEdit(string route, string member, long edit)
    {
        lock (_edits)
        {
            if (_edits.TryGetValue((route, member), out var highest) && edit <= highest) return false;
            _edits[(route, member)] = edit;
            return true;
        }
    }
}

// A request routed to the handler of its route. The envelope is valid until the handler's task completes.
internal sealed class BridgeRouteRequest(BridgeRequestEnvelope envelope, BridgeConnection connection, BridgeDispatcher dispatcher)
{
    internal BridgeRequestEnvelope Envelope { get; } = envelope;
    internal BridgeConnection Connection { get; } = connection;
    internal BridgeDispatcher Dispatcher { get; } = dispatcher;
    internal string Kind => Envelope.Kind;
    internal string Route => Envelope.Route!;
    internal string? Member => Envelope.Member;
    internal JsonElement Root => Envelope.Root;
}

// Handles the requests of one route (RegisterRoute). It admits a request before its returned task first yields, so
// the dispatcher keeps a connection's admission order. It returns the reply as UTF-8 JSON.
internal interface IBridgeRouteHandler
{
    ValueTask<ReadOnlyMemory<byte>> HandleAsync(BridgeRouteRequest request, CancellationToken cancellationToken);
}
