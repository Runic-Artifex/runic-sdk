using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

// The Views protocol 4 producer's request dispatcher of one window (specs/application/README.md). It serves the
// window's IBridgeTransport and owns what belongs to a connection rather than to a route:
//
// - the handshake: hello, the protocol and implementation check, the features each side implements, and the requests
//   that arrive before the hello, which wait for it (at most 5 seconds and 64 per connection) and are then admitted in
//   the order they arrived, or answer no-hello;
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
    // The features of this producer (specs/application/README.md, "Features").
    internal const string TextSpliceFeature = "text-splice/1";
    internal const int MaximumFeatures = 64;
    internal const int MaximumFeatureLength = 64;
    // A request that arrives before its connection's hello waits this long for it, and at most this many wait.
    internal static readonly TimeSpan HelloWaitLimit = TimeSpan.FromSeconds(5);
    internal const int MaximumHeldRequests = 64;

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
    // Connections that completed their hello without text-splice/1: while there is one, frames carry no splices.
    private int _connectionsWithoutTextSplice;
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

    // The minimum interval between the publication units that notifications request on each route of the window
    // (BridgeSnapshotDelivery). Measured in real time, whatever the window's TimeProvider. The conformance runner sets
    // it to zero, because its scenarios expect one unit per step.
    internal TimeSpan MinimumUnitInterval { get; set; } = BridgeSnapshotDelivery.DefaultMinimumUnitInterval;

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

    // Whether every connection that completed its hello uses text-splice/1, so a frame may carry splices.
    internal bool TextSpliceInUse => Volatile.Read(ref _connectionsWithoutTextSplice) == 0;

    // Sends a push to every connection. Nothing is sent while no connection completed its hello. A frame with splices
    // has a fallback, the same frame with sets, which is sent instead while a connection does not use text-splice/1:
    // a broadcast is one message for every connection.
    internal async ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, Func<byte[]>? fallback = null,
        CancellationToken cancellationToken = default)
    {
        bool withoutSplices;
        lock (_gate)
        {
            if (!_connections.Values.Any(static connection => connection.HelloCompleted)) return;
            withoutSplices = _connectionsWithoutTextSplice > 0;
        }
        if (withoutSplices && fallback is not null) message = fallback();
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
            if (envelope.Kind == "hello")
            {
                using (envelope) return Reply(Hello(connection, envelope.Root));
            }
            Task<ReadOnlyMemory<byte>>? held = null;
            lock (_gate)
            {
                // A receive is never held: a mailbox host script reads its no-hello as a replaced connection.
                if (!connection.HelloCompleted && envelope.Kind == "receive") { }
                // Requests behind a hello that has not completed, or behind requests that wait for it, wait in order.
                else if (!connection.HelloCompleted || connection.Held.Count > 0 || connection.Draining)
                    held = Hold(connection, envelope, cancellationToken);
            }
            if (held is not null) return new(held);
            if (!connection.HelloCompleted)
            {
                envelope.Dispose();
                return Reply(NoHello(connection, "receive"));
            }
            return Dispatch(connection, envelope, cancellationToken);
        }
        catch (Exception error)
        {
            ViewsLog.RequestFailed(_logger, error, error.GetType().FullName ?? error.GetType().Name);
            return Reply(BridgeMessages.Failure("failed", null, "The request failed."));
        }
    }

    // The no-hello reply, logged once per connection.
    private byte[] NoHello(BridgeConnection connection, string kind)
    {
        if (!connection.NoHelloLogged)
        {
            connection.NoHelloLogged = true;
            ViewsLog.RequestBeforeHello(_logger, kind, connection.Key);
        }
        return BridgeMessages.Rejected("no-hello", "The connection has not completed a hello.");
    }

    // Holds a request until the connection's hello completed; the gate is held. It owns the envelope from here on.
    private Task<ReadOnlyMemory<byte>> Hold(BridgeConnection connection, BridgeRequestEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (!connection.IsOpen)
        {
            var kind = envelope.Kind;
            envelope.Dispose();
            return Task.FromResult<ReadOnlyMemory<byte>>(NoHello(connection, kind));
        }
        if (connection.Held.Count >= MaximumHeldRequests)
        {
            envelope.Dispose();
            return Task.FromResult<ReadOnlyMemory<byte>>(BridgeMessages.Rejected("capacity",
                $"{MaximumHeldRequests} requests of this connection already wait for its hello."));
        }
        var held = new BridgeHeldRequest(envelope, cancellationToken);
        connection.Held.Add(held);
        held.Start(_timeProvider, HelloWaitLimit, () => Expire(connection, held));
        return held.Reply;
    }

    // A held request whose wait ended without a hello: its time ran out or the transport no longer needs its reply.
    private void Expire(BridgeConnection connection, BridgeHeldRequest held)
    {
        lock (_gate)
        {
            if (!connection.Held.Remove(held)) return;
        }
        if (held.TryTake()) held.Complete(NoHello(connection, held.Envelope.Kind));
    }

    // Answers every held request of a connection whose hello failed or that closed.
    private void RefuseHeld(BridgeConnection connection)
    {
        BridgeHeldRequest[] refused;
        lock (_gate)
        {
            if (connection.Held.Count == 0) return;
            refused = [.. connection.Held];
            connection.Held.Clear();
        }
        foreach (var held in refused)
            if (held.TryTake()) held.Complete(NoHello(connection, held.Envelope.Kind));
    }

    // Admits the held requests of a connection whose hello succeeded, in the order they arrived. Requests that arrive
    // meanwhile queue behind them, so the connection keeps its order.
    private void DrainHeld(BridgeConnection connection)
    {
        while (true)
        {
            BridgeHeldRequest held;
            lock (_gate)
            {
                if (connection.Held.Count == 0)
                {
                    connection.Draining = false;
                    return;
                }
                held = connection.Held[0];
                connection.Held.RemoveAt(0);
            }
            if (!held.TryTake()) continue;
            ValueTask<ReadOnlyMemory<byte>> reply;
            try { reply = Dispatch(connection, held.Envelope, held.CancellationToken); }
            catch (Exception error)
            {
                ViewsLog.RequestFailed(_logger, error, error.GetType().FullName ?? error.GetType().Name);
                held.Complete(BridgeMessages.Failure("failed", null, "The request failed."), routed: true);
                continue;
            }
            if (reply.IsCompletedSuccessfully) held.Complete(reply.Result, routed: true);
            else _ = ForwardAsync(reply, held);
        }
    }

    private static async Task ForwardAsync(ValueTask<ReadOnlyMemory<byte>> reply, BridgeHeldRequest held) =>
        held.Complete(await reply.ConfigureAwait(false), routed: true);

    // Routes a request of a connection that completed its hello. It owns the envelope and disposes it when done.
    private ValueTask<ReadOnlyMemory<byte>> Dispatch(BridgeConnection connection, BridgeRequestEnvelope envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            var disposeEnvelope = true;
            try
            {
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
        BridgeConnection? refusedHeld;
        lock (_gate)
        {
            if (!_connections.Remove(connectionKey, out var connection)) return;
            connection.IsOpen = false;
            if (connection.HelloCompleted && !connection.UsesTextSplice) _connectionsWithoutTextSplice--;
            refusedHeld = connection;
            if (connection.Session is { } session) RetainClosedSession(session);
            connection.Mailbox.Clear();
            connection.MailboxBytes = 0;
            var pending = connection.PendingReceive;
            connection.PendingReceive = null;
            pending?.TrySetResult(null);
            observers = [.. _closedObservers];
        }
        RefuseHeld(refusedHeld);
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
        var reply = HelloCore(connection, root, out var drain);
        // Requests that waited for a hello that failed answer no-hello; after one that succeeded they are admitted.
        if (drain) _ = Task.Run(() => DrainHeld(connection));
        else if (!connection.HelloCompleted) RefuseHeld(connection);
        return reply;
    }

    private byte[] HelloCore(BridgeConnection connection, JsonElement root, out bool drain)
    {
        drain = false;
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
        if (!TryReadFeatures(root, out var usesTextSplice))
            return BridgeMessages.Rejected("invalid-request",
                $"The hello's features are not an array of at most {MaximumFeatures} strings of at most {MaximumFeatureLength} characters.", hello: true);
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
            connection.UsesTextSplice = usesTextSplice;
            connection.HelloCompleted = true;
            if (!usesTextSplice) _connectionsWithoutTextSplice++;
            if (connection.Held.Count > 0) drain = connection.Draining = true;
        }
        return BridgeMessages.Hello();
    }

    // The client's features: absent, or an array of strings. A feature this producer does not implement is ignored.
    private static bool TryReadFeatures(JsonElement root, out bool usesTextSplice)
    {
        usesTextSplice = false;
        if (!root.TryGetProperty("features", out var features)) return true;
        if (features.ValueKind != JsonValueKind.Array || features.GetArrayLength() > MaximumFeatures) return false;
        foreach (var feature in features.EnumerateArray())
        {
            if (feature.ValueKind != JsonValueKind.String) return false;
            var name = feature.GetString()!;
            if (name.Length > MaximumFeatureLength) return false;
            if (name == TextSpliceFeature) usesTextSplice = true;
        }
        return true;
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
    // Whether the connection uses text-splice/1: both its hello and the producer list it.
    internal bool UsesTextSplice { get; set; }
    // Requests that wait for the hello, in arrival order, and whether the dispatcher is admitting them.
    internal List<BridgeHeldRequest> Held { get; } = [];
    internal bool Draining { get; set; }
    internal bool IsOpen { get; set; } = true;
    internal bool MismatchLogged { get; set; }
    internal bool NoHelloLogged { get; set; }
    internal bool MailboxOverflowLogged { get; set; }
    internal Queue<byte[]> Mailbox { get; } = new();
    internal long MailboxBytes { get; set; }
    internal TaskCompletionSource<byte[]?>? PendingReceive { get; set; }

    // The highest edit number admitted per route and member (specs/application/README.md, "Edits"), and, on a
    // connection that uses text-splice/1, the text value of that edit, which a later splice may name as its base.
    private readonly Dictionary<(string Route, string Member), (long Edit, string? Text)> _edits = [];

    // Admits a set request's edit number: true, and recorded with text (the edit's value, for a text member on a
    // connection that uses text-splice/1), when it is higher than every edit admitted before for the route and member
    // on this connection.
    internal bool AdmitEdit(string route, string member, long edit, string? text = null)
    {
        lock (_edits)
        {
            if (_edits.TryGetValue((route, member), out var highest) && edit <= highest.Edit) return false;
            _edits[(route, member)] = (edit, text);
            return true;
        }
    }

    // Whether an edit number is higher than every edit admitted for the route and member, without admitting it.
    internal bool IsNewEdit(string route, string member, long edit)
    {
        lock (_edits) return !_edits.TryGetValue((route, member), out var highest) || edit > highest.Edit;
    }

    // The text value of edit, when it is the highest edit admitted for the route and member and its value was text.
    internal string? EditText(string route, string member, long edit)
    {
        lock (_edits)
            return _edits.TryGetValue((route, member), out var highest) && highest.Edit == edit ? highest.Text : null;
    }
}

// A request that waits for its connection's hello. Exactly one of the hello, its time limit, the transport's
// cancellation and the connection's close takes it (TryTake) and completes its reply.
internal sealed class BridgeHeldRequest(BridgeRequestEnvelope envelope, CancellationToken cancellationToken)
{
    private readonly TaskCompletionSource<ReadOnlyMemory<byte>> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ITimer? _timer;
    private CancellationTokenRegistration _cancellation;
    private int _taken;

    internal BridgeRequestEnvelope Envelope { get; } = envelope;
    internal CancellationToken CancellationToken { get; } = cancellationToken;
    internal Task<ReadOnlyMemory<byte>> Reply => _reply.Task;

    internal void Start(TimeProvider timeProvider, TimeSpan limit, Action expire)
    {
        _timer = timeProvider.CreateTimer(static state => ((Action)state!)(), expire, limit, Timeout.InfiniteTimeSpan);
        if (CancellationToken.CanBeCanceled) _cancellation = CancellationToken.Register(expire);
    }

    internal bool TryTake()
    {
        if (Interlocked.Exchange(ref _taken, 1) != 0) return false;
        _timer?.Dispose();
        _cancellation.Dispose();
        return true;
    }

    // Completes the reply of a taken request. A request that was not routed releases its envelope here; a routed one
    // released it when its handler finished.
    internal void Complete(ReadOnlyMemory<byte> reply, bool routed = false)
    {
        if (!routed) Envelope.Dispose();
        _reply.TrySetResult(reply);
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
