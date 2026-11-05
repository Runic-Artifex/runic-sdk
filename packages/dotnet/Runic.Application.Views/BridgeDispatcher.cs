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
// - the connection-level requests of presented/1: presented, which goes to the route's handler for its waits, and
//   closing-reply, which acknowledges the closing push of a closing window (BeginClosing);
// - the connection lifecycle: the transport reports closed connections, and the dispatcher releases their sessions and
//   tells the window's observers (OnConnectionClosed).
//
// Every bridge of a window shares the dispatcher of the window's transport (For).
internal sealed partial class BridgeDispatcher : IBridgeRequestHandler
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
    internal const string PresentationEpochFeature = "presentation-epoch/1";
    internal const string PresentedFeature = "presented/1";
    // validation-pending/1: validation may carry pending paths. The producer writes them in the route's state for
    // every connection (state is one value per route); a client without the feature ignores the member.
    internal const string ValidationPendingFeature = "validation-pending/1";
    // field-flush/1: the producer asks the pages to write their field drafts (flush) before a departure is decided.
    internal const string FieldFlushFeature = "field-flush/1";
    // field-paths/1: a checked write may name a path below its member: a nested member or a keyed collection's row cell.
    internal const string FieldPathsFeature = "field-paths/1";
    // How long a flush waits at most for the pages' flush-reply.
    internal static readonly TimeSpan FieldFlushLimit = TimeSpan.FromSeconds(1);
    // translation-text/1: the window's locale, pushed to the connections that use the feature (SetLocale), and the text
    // of validation errors, which is part of the route's state like pending.
    internal const string TranslationTextFeature = "translation-text/1";
    // The longest locale tag the window pushes (BCP 47 tags are far shorter).
    internal const int MaximumLocaleLength = 64;
    internal const string ViewRequestsFeature = "view-requests/1";
    // Every request names the session of its connection's hello (specs/application/README.md, "Request sessions").
    internal const string RequestSessionFeature = "request-session/1";
    internal const string RegionCollectionsFeature = "region-collections/1";
    internal const string ClientErrorsFeature = BridgeClientErrors.Feature;
    internal const string QueryCollectionsFeature = "query-collections/1";
    internal const string BlobsFeature = BridgeBlobs.Feature;
    internal const string NavigationPathFeature = "navigation-path/1";
    // client-languages/1: the hello lists the page's languages (navigator.languages), at most this many.
    internal const string ClientLanguagesFeature = "client-languages/1";
    internal const int MaximumClientLanguages = 16;
    internal const int MaximumFeatures = 64;
    internal const int MaximumFeatureLength = 64;
    // A request that arrives before its connection's hello waits this long for it, and at most this many wait.
    internal static readonly TimeSpan HelloWaitLimit = TimeSpan.FromSeconds(5);
    internal const int MaximumHeldRequests = 64;

    private static readonly ConditionalWeakTable<IBridgeTransport, BridgeDispatcher> Dispatchers = new();

    private readonly object _gate = new();
    private readonly IBridgeTransport _transport;
    private readonly IBridgeConnectionTransport? _targeted;
    // Open connections by the key their transport reports, and by their own key, which is unique for the dispatcher's
    // lifetime (Connect).
    private readonly Dictionary<string, BridgeConnection> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BridgeConnection> _byKey = new(StringComparer.Ordinal);
    // Numbers the connections of a transport whose keys may repeat (Connect); a transport whose keys never repeat
    // (IBridgeUniqueConnectionKeys) needs none.
    private long _connectionCount;
    // Transport keys whose connection a hello of another session replaced while the transport had not reported it closed:
    // the transport's next close of the key is the replaced connection's, which closed already (ReplaceConnection).
    private readonly Dictionary<string, int> _replacedCloses = new(StringComparer.Ordinal);
    private readonly bool _transportKeysUnique;
    private readonly HashSet<string> _closedSessions = new(StringComparer.Ordinal);
    private readonly Queue<string> _closedSessionOrder = new();
    private readonly Dictionary<string, IBridgeRouteHandler> _routes = new(StringComparer.Ordinal);
    private readonly List<Action<string>> _closedObservers = [];
    // Observers of connections that complete their hello (IRunicConnections), in the order the hellos completed.
    private readonly List<Action<BridgeConnection>> _openedObservers = [];
    // Answers status, watch, unwatch, cancel and stream of a route nothing serves, for an operation the window knows.
    private Func<BridgeRouteRequest, ReadOnlyMemory<byte>?>? _releasedOperations;
    // Connections that completed their hello without text-splice/1: while there is one, frames carry no splices.
    private int _connectionsWithoutTextSplice;
    // Connections that completed their hello with presentation-epoch/1: while there is one, state pushes carry epoch.
    private int _connectionsWithPresentationEpoch;
    // The window's close (BeginClosing): the connections whose closing-reply is outstanding, and its completion.
    private int _closingPending;
    // field-flush/1: the flushes waiting for flush-reply, by id, with the connections still to answer.
    private readonly Dictionary<string, (TaskCompletionSource Completion, HashSet<string> Waiting)> _flushes = new(StringComparer.Ordinal);
    private long _nextFlush;
    private TaskCompletionSource? _closing;
    // translation-text/1: the window's locale, or null when the page keeps its own. Pushes of it are queued under
    // _localeGate, so a connection that completes its hello while the locale changes receives the last value last.
    private readonly object _localeGate = new();
    private string? _locale;
    private int _maximumInputBytes = WindowContentSessionOptions.DefaultMaximumInputBytes;
    // WindowContentSessionOptions.FlowCultureFromLocale: requests run in the culture of the window's locale.
    private bool _flowCultureFromLocale;
    private ILogger _logger = TraceFallbackLogger.Instance;
    // client-errors/1: the category of the errors pages report.
    private ILogger _pageLogger = TraceFallbackLogger.Instance;
    // client-errors/1: the window's limit across its connections, so a page that reconnects keeps its budget.
    private readonly BridgeClientErrors.Limit _clientErrors = new();
    private TimeProvider _timeProvider = TimeProvider.System;

    private BridgeDispatcher(IBridgeTransport transport)
    {
        _transport = transport;
        _targeted = transport as IBridgeConnectionTransport;
        Blobs = new BridgeBlobs();
        _transportKeysUnique = transport is IBridgeUniqueConnectionKeys;
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

    // The Window's blobs and uploads (blobs/1). The host attaches its endpoint; the window session disposes it.
    internal BridgeBlobs Blobs { get; }

    internal TimeProvider TimeProvider => _timeProvider;

    internal ILogger Logger => _logger;

    // The minimum interval between the publication units that notifications request on each route of the window
    // (BridgeSnapshotDelivery). Measured in real time, whatever the window's TimeProvider. The conformance runner sets
    // it to zero, because its scenarios expect one unit per step.
    internal TimeSpan MinimumUnitInterval { get; set; } = BridgeSnapshotDelivery.DefaultMinimumUnitInterval;

    // Whether pushes to one connection travel through the mailbox.
    internal bool UsesMailbox => _targeted is null;

    // Applies the window's options. The window session calls this once it was created.
    internal void Configure(int maximumInputBytes, ILogger logger, TimeProvider timeProvider, ILogger? pageLogger = null,
        bool flowCultureFromLocale = false)
    {
        _maximumInputBytes = maximumInputBytes;
        _flowCultureFromLocale = flowCultureFromLocale;
        _logger = logger;
        _pageLogger = pageLogger ?? logger;
        _timeProvider = timeProvider;
        Blobs.Logger = logger;
        Blobs.TimeProvider = timeProvider;
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

    // Calls opened with each connection that completes its hello and closed with the key of each connection that closes,
    // until the lease is disposed, and returns the connections that completed their hello before, under the same lock,
    // so an observer misses none and sees none twice. The calls come after the dispatcher's own bookkeeping, outside its
    // lock: opened before the connection's hello reply and before the requests that waited for the hello are admitted.
    internal IDisposable ObserveConnections(Action<BridgeConnection> opened, Action<string> closed,
        out IReadOnlyList<BridgeConnection> current)
    {
        ArgumentNullException.ThrowIfNull(opened);
        ArgumentNullException.ThrowIfNull(closed);
        lock (_gate)
        {
            current = [.. _connections.Values.Where(static connection => connection.HelloCompleted && connection.IsOpen)];
            _openedObservers.Add(opened);
            _closedObservers.Add(closed);
        }
        return new ConnectionObserverLease(this, opened, closed);
    }

    private sealed class ConnectionObserverLease(BridgeDispatcher owner, Action<BridgeConnection> opened, Action<string> closed)
        : IDisposable
    {
        private BridgeDispatcher? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } dispatcher) return;
            lock (dispatcher._gate)
            {
                dispatcher._openedObservers.Remove(opened);
                dispatcher._closedObservers.Remove(closed);
            }
        }
    }

    // The handler of a route this window serves, or null.
    internal IBridgeRouteHandler? FindRoute(string route)
    {
        lock (_gate) return _routes.TryGetValue(route, out var handler) ? handler : null;
    }

    // The key of the open connection that the transport reports with this transport key, or null. Tests and host code
    // that know only the transport's key use it to name the window's connection.
    internal string? ConnectionKeyOf(string transportKey)
    {
        lock (_gate) return _connections.TryGetValue(transportKey, out var connection) ? connection.Key : null;
    }

    // The open connection with this connection key (BridgeConnection.Key, not the transport's key), if it completed its
    // hello. A key of a closed connection never finds a later connection, also when the transport reused its key.
    internal BridgeConnection? FindConnection(string connectionKey)
    {
        lock (_gate)
            return _byKey.TryGetValue(connectionKey, out var connection) && connection.HelloCompleted ? connection : null;
    }

    // Whether every connection that completed its hello uses text-splice/1, so a frame may carry splices.
    internal bool TextSpliceInUse => Volatile.Read(ref _connectionsWithoutTextSplice) == 0;

    // Whether a connection that completed its hello uses presentation-epoch/1, so a state push carries epoch: a
    // broadcast member that base receivers ignore (specs/application/README.md, "Presentation epochs").
    internal bool PresentationEpochInUse => Volatile.Read(ref _connectionsWithPresentationEpoch) > 0;

    // Pushes closing to every connection that uses presented/1 and returns a task that completes when each of them
    // answered closing-reply or closed. The first call pushes; later calls return the same task. A connection that
    // completes its hello with presented/1 afterwards gets closing too, but is not waited for.
    internal Task BeginClosing()
    {
        List<string> targets = [];
        Task closing;
        lock (_gate)
        {
            if (_closing is { } started) return started.Task;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _closing = completion;
            closing = completion.Task;
            foreach (var connection in _connections.Values)
            {
                if (!connection.HelloCompleted || !connection.UsesPresented || !connection.IsOpen) continue;
                connection.ClosingPending = true;
                _closingPending++;
                targets.Add(connection.Key);
            }
            if (_closingPending == 0) completion.TrySetResult();
        }
        foreach (var target in targets) BridgeTargetedPushes.For(_transport).Send(target, ClosingPush());
        return closing;
    }

    // The connections whose closing-reply is still outstanding.
    internal int UnacknowledgedClosings
    {
        get { lock (_gate) return _closingPending; }
    }

    // The keys of the open connections that completed their hello with navigation-path/1.
    internal List<string> NavigationPathConnections()
    {
        lock (_gate) return [.. _connections.Values.Where(static connection => connection.HelloCompleted && connection.IsOpen && connection.UsesNavigationPath)
            .Select(static connection => connection.Key)];
    }

    // The navigator whose regions this window presents (NavigationPresentationBinding), weakly.
    private WeakReference<Runic.Navigation.RunicNavigator>? _navigator;

    internal Runic.Navigation.RunicNavigator? Navigator
    {
        get => Volatile.Read(ref _navigator) is { } reference && reference.TryGetTarget(out var navigator) ? navigator : null;
        set => Volatile.Write(ref _navigator, value is null ? null : new(value));
    }

    private static byte[] ClosingPush() => """{"kind":"closing"}"""u8.ToArray();

    // translation-text/1: the window's locale, or null when the page keeps its own.
    internal string? Locale
    {
        get { lock (_localeGate) return _locale; }
    }

    // Sets the window's locale and pushes it to every connection that uses translation-text/1. False when it did not
    // change.
    internal bool SetLocale(string? locale)
    {
        if (locale is not null && !IsLocale(locale))
            throw new ArgumentException(
                $"'{locale}' is not a locale tag: use 1 to {MaximumLocaleLength} letters, digits and '-', such as \"de\" or \"pt-BR\".",
                nameof(locale));
        lock (_localeGate)
        {
            if (string.Equals(_locale, locale, StringComparison.Ordinal)) return false;
            _locale = locale;
            List<string> targets = [];
            lock (_gate)
                foreach (var connection in _connections.Values)
                    if (connection.HelloCompleted && connection.UsesTranslationText && connection.IsOpen) targets.Add(connection.Key);
            var push = LocalePush(locale);
            foreach (var target in targets) BridgeTargetedPushes.For(_transport).Send(target, push);
        }
        ViewsLog.WindowLocaleChanged(_logger, locale);
        return true;
    }

    // A locale tag: letters, digits and '-', starting with a letter, without an empty subtag.
    internal static bool IsLocale(string value)
    {
        if (value.Length is 0 or > MaximumLocaleLength || !char.IsAsciiLetter(value[0]) || value[^1] == '-') return false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '-' ? value[index - 1] == '-' : !char.IsAsciiLetterOrDigit(character)) return false;
        }
        return true;
    }

    private static byte[] LocalePush(string? locale) => BridgeMessages.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "locale");
        if (locale is null) writer.WriteNull("locale");
        else writer.WriteString("locale", locale);
        writer.WriteEndObject();
    });

    // A connection that acknowledged the window's close or closed; the gate is held.
    private void ClosingSettled(BridgeConnection connection)
    {
        if (!connection.ClosingPending) return;
        connection.ClosingPending = false;
        if (--_closingPending == 0) _closing?.TrySetResult();
    }

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
            if (!_byKey.TryGetValue(connectionKey, out connection) || !connection.HelloCompleted) return false;
            if (_targeted is null) return Enqueue(connection, message);
        }
        try { return await _targeted.SendAsync(connection.TransportKey, message, cancellationToken).ConfigureAwait(false); }
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
                // request-session/1: a request of another page never touches this connection, its mailbox included.
                if (!SessionMatches(connection, envelope)) return Reply(NoHello(connection, envelope.Kind));
                if (envelope.Kind == "receive") return Receive(connection, cancellationToken);
                if (envelope.Kind == "closing-reply") return Reply(ClosingReply(connection));
                if (envelope.Kind == "flush-reply") return Reply(FlushReply(connection, envelope));
                if (envelope.Kind == "presented") return Presented(connection, envelope);
                // navigation-path/1: the window's Back and the page's route path; a request kind only with the feature.
                if (envelope.Kind == "navigation")
                    return connection.UsesNavigationPath
                        ? NavigateInCulture(envelope, cancellationToken)
                        : Reply(BridgeMessages.Rejected("invalid-request", "The connection does not use navigation-path/1."));
                if (envelope.Kind == "client-error") return Reply(ClientError(connection, envelope));
                // view-requests/1: a presentation registers its view request handlers only on a connection that uses it.
                if (envelope.Kind == "view-requests" && !connection.UsesViewRequests)
                    return Reply(BridgeMessages.Rejected("invalid-request", "The connection does not use view-requests/1."));
                // presentation-epoch/1: on a connection that uses it, a command's epoch is a non-negative safe integer;
                // elsewhere the member is ignored.
                if (envelope.Kind == "command" && envelope.HasEpoch && envelope.Epoch is null && connection.UsesPresentationEpoch
                    && !envelope.IsServiceRoute)
                    return Reply(BridgeMessages.Rejected("invalid-request", "The request's epoch is not an integer from 0 to 2^53-1."));
                // region-collections/1: a region request is a request kind only on a connection that uses the feature.
                if (envelope.Kind == "region" && !connection.UsesRegionCollections && !envelope.IsServiceRoute)
                    return Reply(BridgeMessages.Rejected("invalid-request", "The connection does not use region-collections/1."));
                if (envelope.Route is not { } route || !(envelope.IsServiceRoute || BridgeRequestEnvelope.IsViewKind(envelope.Kind)))
                    return Reply(BridgeMessages.Rejected("invalid-request", $"'{envelope.Kind}' is not a request kind of this producer."));
                // query-collections/1: its request kinds exist only on a connection that uses it.
                if (BridgeRequestEnvelope.IsQueryKind(envelope.Kind) && !connection.UsesQueryCollections && !envelope.IsServiceRoute)
                    return Reply(BridgeMessages.Rejected("invalid-request", $"The connection does not use {QueryCollectionsFeature}."));
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
                ValueTask<ReadOnlyMemory<byte>> routed;
                // The setter, command, validation and publication of the request run in the window's culture.
                using (EnterCulture())
                    routed = handler.HandleAsync(new BridgeRouteRequest(envelope, connection, this), cancellationToken);
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

    // FlowCultureFromLocale: the culture of the window's locale, for an invocation that starts now.
    internal LocaleCultureScope EnterCulture() => _flowCultureFromLocale ? LocaleCultureScope.Enter(Locale) : default;

    private ValueTask<ReadOnlyMemory<byte>> NavigateInCulture(BridgeRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        using (EnterCulture()) return BridgeNavigation.HandleAsync(this, envelope, cancellationToken);
    }

    // closing-reply (presented/1): acknowledges the window's closing push; without one pending it answers ok as well.
    private byte[] ClosingReply(BridgeConnection connection)
    {
        if (!connection.UsesPresented)
            return BridgeMessages.Rejected("invalid-request", "The connection does not use presented/1.");
        lock (_gate) ClosingSettled(connection);
        return BridgeMessages.Ok.ToArray();
    }

    // field-flush/1: pushes flush to every connection that uses the feature and completes when each answered
    // flush-reply or closed (true), or after FieldFlushLimit (false). A window without such a connection completes at
    // once with true.
    internal async Task<bool> FlushFieldsAsync(CancellationToken cancellationToken = default)
    {
        string id;
        Task flushed;
        List<string> targets = [];
        lock (_gate)
        {
            foreach (var connection in _connections.Values)
                if (connection.HelloCompleted && connection.UsesFieldFlush && connection.IsOpen) targets.Add(connection.Key);
            if (targets.Count == 0) return true;
            id = "flush-" + (++_nextFlush).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _flushes[id] = (completion, new HashSet<string>(targets, StringComparer.Ordinal));
            flushed = completion.Task;
        }
        var push = System.Text.Encoding.UTF8.GetBytes($$"""{"kind":"flush","id":"{{id}}"}""");
        foreach (var target in targets) _ = SendFlushAsync(id, target, push);
        // Real time, whatever the window's TimeProvider: a departure waits for the user's pages, not for a test clock.
        try
        {
            await flushed.WaitAsync(FieldFlushLimit, TimeProvider.System, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            int unanswered;
            lock (_gate) unanswered = _flushes.TryGetValue(id, out var flush) ? flush.Waiting.Count : 0;
            ViewsLog.FieldFlushUnanswered(_logger, unanswered);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
        finally { lock (_gate) _flushes.Remove(id); }
    }

    // Sends one connection its flush. A connection the push does not reach cannot answer it: it counts as settled.
    // Sent directly rather than in the targeted pushes' order: a flush follows no frame.
    private async Task SendFlushAsync(string id, string connectionKey, byte[] push)
    {
        bool sent;
        try { sent = await SendAsync(connectionKey, push).ConfigureAwait(false); }
#pragma warning disable CA1031 // A failed send is an unreachable page, not a failed departure.
        catch (Exception) { sent = false; }
#pragma warning restore CA1031
        if (sent) return;
        lock (_gate)
            if (_flushes.TryGetValue(id, out var flush)) FlushSettled(flush, connectionKey);
    }

    // The window stops serving its pages (its session is disposed): flushes in progress complete at once.
    internal void SettleFlushes()
    {
        lock (_gate)
            foreach (var flush in _flushes.Values) flush.Completion.TrySetResult();
    }

    // flush-reply (field-flush/1): the page wrote its drafts. An id that no flush waits for answers ok as well.
    private byte[] FlushReply(BridgeConnection connection, BridgeRequestEnvelope envelope)
    {
        if (!connection.UsesFieldFlush)
            return BridgeMessages.Rejected("invalid-request", "The connection does not use field-flush/1.");
        if (!envelope.Root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            return BridgeMessages.Rejected("invalid-request", "The flush-reply has no id.");
        lock (_gate)
            if (_flushes.TryGetValue(idElement.GetString()!, out var flush)) FlushSettled(flush, connection.Key);
        return BridgeMessages.Ok.ToArray();
    }

    // A connection answered a flush or closed; the gate is held.
    private static void FlushSettled((TaskCompletionSource Completion, HashSet<string> Waiting) flush, string connectionKey)
    {
        if (flush.Waiting.Remove(connectionKey) && flush.Waiting.Count == 0) flush.Completion.TrySetResult();
    }

    // client-error (client-errors/1): the page reports an error, which the window logs; the reply is ok also when the
    // connection's limit suppressed it.
    private byte[] ClientError(BridgeConnection connection, BridgeRequestEnvelope envelope)
    {
        if (!connection.UsesClientErrors)
            return BridgeMessages.Rejected("invalid-request", "The connection does not use client-errors/1.");
        BridgeClientErrors.Report(connection, _clientErrors, envelope.Root, _pageLogger, _timeProvider);
        return BridgeMessages.Ok.ToArray();
    }

    // presented (presented/1): the page painted the route at the revision. The route's handler completes its waits.
    private ValueTask<ReadOnlyMemory<byte>> Presented(BridgeConnection connection, BridgeRequestEnvelope envelope)
    {
        if (!connection.UsesPresented)
            return Reply(BridgeMessages.Rejected("invalid-request", "The connection does not use presented/1."));
        IBridgeRouteHandler? handler;
        lock (_gate) _routes.TryGetValue(envelope.Route!, out handler);
        if (handler is not IBridgePresentedHandler presented) return new(BridgeMessages.Released);
        BridgeRequestEnvelope.TryReadInteger(envelope.Root.GetProperty("revision"), out var revision);
        presented.Presented(connection, revision);
        return new(BridgeMessages.Ok);
    }

    /// <inheritdoc />
    public void ConnectionClosed(string connectionKey)
    {
        ArgumentNullException.ThrowIfNull(connectionKey);
        Action<string>[] observers;
        BridgeConnection? closed;
        lock (_gate)
        {
            // The close of a connection that a hello of another session replaced already: the key's current connection
            // stays open.
            if (_replacedCloses.TryGetValue(connectionKey, out var replaced))
            {
                if (replaced == 1) _replacedCloses.Remove(connectionKey);
                else _replacedCloses[connectionKey] = replaced - 1;
                return;
            }
            if (!_connections.Remove(connectionKey, out closed)) return;
            observers = CloseLocked(closed);
        }
        Closed(closed, observers);
    }

    // Closes a connection that was removed from _connections: releases its state and mailbox. The caller holds _gate and
    // then calls Closed with the observers.
    private Action<string>[] CloseLocked(BridgeConnection connection)
    {
        _byKey.Remove(connection.Key);
        connection.IsOpen = false;
        if (connection.HelloCompleted && !connection.UsesTextSplice) _connectionsWithoutTextSplice--;
        if (connection.HelloCompleted && connection.UsesPresentationEpoch) _connectionsWithPresentationEpoch--;
        // field-flush/1: a closed connection cannot answer a flush; it counts as settled.
        foreach (var flush in _flushes.Values) FlushSettled(flush, connection.Key);
        // A closed connection counts as one that acknowledged the window's close.
        ClosingSettled(connection);
        if (connection.Session is { } session) RetainClosedSession(session);
        connection.Mailbox.Clear();
        connection.MailboxBytes = 0;
        var pending = connection.PendingReceive;
        connection.PendingReceive = null;
        pending?.TrySetResult(null);
        return [.. _closedObservers];
    }

    private void Closed(BridgeConnection connection, Action<string>[] observers)
    {
        RefuseHeld(connection);
        // blobs/1: the connection's upload token stops working and its unclaimed uploads are deleted, also when a hello of
        // another session replaced it. Its token and uploads are under its own key, which a later page never gets.
        Blobs.ConnectionClosed(connection.Key);
        foreach (var observer in observers)
        {
            try { observer(connection.Key); }
            catch (Exception error)
            {
                ViewsLog.RequestFailed(_logger, error, error.GetType().FullName ?? error.GetType().Name);
            }
        }
    }

    // A hello with another session on a connection that completed its hello with request-session/1: the transport gave
    // the key of a connection
    // that closed to a new page before it reported the close, as CS-WebUI does when WebUI reuses a slot. The old
    // connection closes as if the transport had reported it, and the hello opens a new connection with a new key; the
    // transport's late close of the old one is then ignored. Nothing of the old connection reaches the new page.
    private BridgeConnection ReplaceConnection(BridgeConnection old)
    {
        Action<string>[] observers;
        BridgeConnection replacement;
        lock (_gate)
        {
            if (!_connections.TryGetValue(old.TransportKey, out var current) || !ReferenceEquals(current, old)) return Connect(old.TransportKey, old.ClientKey);
            _connections.Remove(old.TransportKey);
            observers = CloseLocked(old);
            _replacedCloses[old.TransportKey] = _replacedCloses.GetValueOrDefault(old.TransportKey) + 1;
            replacement = AddConnection(old.TransportKey, old.ClientKey);
        }
        Closed(old, observers);
        return replacement;
    }

    // Whether a request may act for its connection: on a connection that uses request-session/1, a request other than
    // hello names the session of the connection's hello. Another page that got the connection's transport key before
    // the transport reported the close, as with a reused CS-WebUI slot, names its own session or none.
    private static bool SessionMatches(BridgeConnection connection, BridgeRequestEnvelope envelope) =>
        !connection.UsesRequestSession
        || envelope.Root.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.String
            && session.ValueEquals(connection.Session);

    private BridgeConnection Connect(BridgeRequest request) => Connect(request.ConnectionKey, request.ClientKey);

    private BridgeConnection Connect(string transportKey, string clientKey)
    {
        lock (_gate)
            return _connections.TryGetValue(transportKey, out var connection) ? connection : AddConnection(transportKey, clientKey);
    }

    // The caller holds _gate.
    private BridgeConnection AddConnection(string transportKey, string clientKey)
    {
        var connection = new BridgeConnection(NewConnectionKey(transportKey), clientKey, transportKey);
        _connections.Add(transportKey, connection);
        _byKey.Add(connection.Key, connection);
        return connection;
    }

    // The key of a new connection with this transport key, unique for the dispatcher's lifetime: the transport key itself
    // for a transport whose keys never repeat, otherwise "{transportKey}#{n}" with a counter of the dispatcher that only
    // grows, so no key is ever handed out twice and nothing is remembered. A transport may reuse a key once its connection
    // closed, as CS-WebUI reuses WebUI's connection slots and, without its cookie, client id 0. Interactions, view
    // requests, operation watches and IRunicConnections select a page by this key, so work that a closed connection's
    // command still runs never reaches the page that got its transport key next. The caller holds _gate.
    private string NewConnectionKey(string transportKey) =>
        _transportKeysUnique ? transportKey
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{transportKey}#{++_connectionCount}");

    private byte[] Hello(BridgeConnection connection, JsonElement root)
    {
        // A valid hello of another session on a connection that uses request-session/1 comes from a new page that got the
        // key before the transport reported the old connection closed. A transport whose keys never repeat gives a new
        // page a new key, so there a second hello stays invalid-request and the transport's one close stays the old
        // connection's.
        if (!_transportKeysUnique && connection.HelloCompleted && connection.UsesRequestSession && IsReplacingHello(connection, root))
            connection = ReplaceConnection(connection);
        var reply = HelloCore(connection, root, out var drain, out var opened);
        // Observers learn of the connection before its hello reply and its held requests, so a command it sends finds it.
        foreach (var observer in opened)
        {
            try { observer(connection); }
            catch (Exception error)
            {
                ViewsLog.RequestFailed(_logger, error, error.GetType().FullName ?? error.GetType().Name);
            }
        }
        // Requests that waited for a hello that failed answer no-hello; after one that succeeded they are admitted.
        if (drain) _ = Task.Run(() => DrainHeld(connection));
        else if (!connection.HelloCompleted) RefuseHeld(connection);
        return reply;
    }

    // Whether a hello would succeed on a new connection, with another session than the connection's: it is checked as
    // HelloCore checks it before the old connection is replaced, so an invalid hello replaces nothing and is answered as
    // a second hello of the connection.
    private bool IsReplacingHello(BridgeConnection connection, JsonElement root)
    {
        if (!root.TryGetProperty("protocol", out var protocolElement)
            || !BridgeRequestEnvelope.TryReadInteger(protocolElement, out var protocol) || protocol != ProtocolVersion)
            return false;
        if (!root.TryGetProperty("session", out var sessionElement) || sessionElement.ValueKind != JsonValueKind.String
            || !BridgeRequestEnvelope.IsIdentifier(sessionElement.GetString()!) || sessionElement.ValueEquals(connection.Session))
            return false;
        if (!root.TryGetProperty("client", out var client) || client.ValueKind != JsonValueKind.Object
            || !TryReadName(client, "name", 128, out _) || !TryReadName(client, "version", 64, out _)
            || !TryReadFeatures(root, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out var usesLanguages)
            || !TryReadLanguages(root, usesLanguages, out _))
            return false;
        var session = sessionElement.GetString()!;
        lock (_gate)
            return !_closedSessions.Contains(session) && !_connections.Values.Any(other => other.Session == session);
    }

    private byte[] HelloCore(BridgeConnection connection, JsonElement root, out bool drain,
        out Action<BridgeConnection>[] opened)
    {
        drain = false;
        opened = [];
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
        if (!TryReadFeatures(root, out var usesTextSplice, out var usesPresentationEpoch, out var usesPresented, out var usesViewRequests,
                out var usesRequestSession, out var usesRegionCollections, out var usesClientErrors, out var usesQueryCollections, out var usesBlobs,
                out var usesFieldFlush, out var usesFieldPaths, out var usesTranslationText, out var usesNavigationPath, out var usesClientLanguages))
            return BridgeMessages.Rejected("invalid-request",
                $"The hello's features are not an array of at most {MaximumFeatures} strings of at most {MaximumFeatureLength} characters.", hello: true);
        var usesAppearance = ListsAppearance(root);
        if (!TryReadLanguages(root, usesClientLanguages, out var languages))
            return BridgeMessages.Rejected("invalid-request",
                $"The hello's languages are not an array of at most {MaximumClientLanguages} strings.", hello: true);
        var session = sessionElement.GetString()!;
        bool closing;
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
            connection.UsesPresentationEpoch = usesPresentationEpoch;
            connection.UsesPresented = usesPresented;
            connection.UsesViewRequests = usesViewRequests;
            connection.UsesRequestSession = usesRequestSession;
            connection.UsesRegionCollections = usesRegionCollections;
            connection.UsesClientErrors = usesClientErrors;
            connection.UsesQueryCollections = usesQueryCollections;
            connection.UsesFieldFlush = usesFieldFlush;
            connection.UsesFieldPaths = usesFieldPaths;
            connection.UsesTranslationText = usesTranslationText;
            connection.UsesNavigationPath = usesNavigationPath;
            connection.UsesAppearance = usesAppearance;
            connection.Languages = languages;
            connection.HelloCompleted = true;
            if (!usesTextSplice) _connectionsWithoutTextSplice++;
            if (usesPresentationEpoch) _connectionsWithPresentationEpoch++;
            if (connection.Held.Count > 0) drain = connection.Draining = true;
            closing = usesPresented && _closing is not null;
            opened = [.. _openedObservers];
        }
        // A page that connects to a closing window is told so, after its hello reply.
        if (closing) BridgeTargetedPushes.For(_transport).Send(connection.Key, ClosingPush());
        // translation-text/1: a page that connects to a window with a locale is told it, after its hello reply. Under the
        // locale gate, so a change that raced the hello is queued after this push.
        if (usesTranslationText)
            lock (_localeGate)
                if (_locale is not null) BridgeTargetedPushes.For(_transport).Send(connection.Key, LocalePush(_locale));
        // appearance/1: the window's appearance, also while it is unknown.
        if (usesAppearance) PushAppearanceAfterHello(connection);
        // blobs/1: the connection's upload grant, when the host takes uploads.
        var grant = usesBlobs ? Blobs.Grant(connection.Key) : null;
        // navigation-path/1: the reply tells the page the window's route path, when the window has a path region.
        return usesNavigationPath && BridgeNavigation.TryGetPath(this, out var path) ? BridgeMessages.Hello(grant, path, navigationPath: true)
            : BridgeMessages.Hello(grant);
    }

    // The client's features: absent, or an array of strings. A feature this producer does not implement is ignored.
    private static bool TryReadFeatures(JsonElement root, out bool usesTextSplice, out bool usesPresentationEpoch,
        out bool usesPresented, out bool usesViewRequests, out bool usesRequestSession, out bool usesRegionCollections, out bool usesClientErrors,
        out bool usesQueryCollections, out bool usesBlobs, out bool usesFieldFlush, out bool usesFieldPaths, out bool usesTranslationText,
        out bool usesNavigationPath, out bool usesClientLanguages)
    {
        usesRequestSession = false;
        usesTextSplice = usesPresentationEpoch = usesPresented = usesViewRequests = usesRegionCollections = usesClientErrors = usesQueryCollections = usesBlobs = false;
        usesFieldFlush = usesFieldPaths = usesTranslationText = usesNavigationPath = usesClientLanguages = false;
        if (!root.TryGetProperty("features", out var features)) return true;
        if (features.ValueKind != JsonValueKind.Array || features.GetArrayLength() > MaximumFeatures) return false;
        foreach (var feature in features.EnumerateArray())
        {
            if (feature.ValueKind != JsonValueKind.String) return false;
            var name = feature.GetString()!;
            if (name.Length > MaximumFeatureLength) return false;
            if (name == TextSpliceFeature) usesTextSplice = true;
            else if (name == PresentationEpochFeature) usesPresentationEpoch = true;
            else if (name == PresentedFeature) usesPresented = true;
            else if (name == ViewRequestsFeature) usesViewRequests = true;
            else if (name == RequestSessionFeature) usesRequestSession = true;
            else if (name == RegionCollectionsFeature) usesRegionCollections = true;
            else if (name == ClientErrorsFeature) usesClientErrors = true;
            else if (name == QueryCollectionsFeature) usesQueryCollections = true;
            else if (name == BlobsFeature) usesBlobs = true;
            else if (name == FieldFlushFeature) usesFieldFlush = true;
            else if (name == FieldPathsFeature) usesFieldPaths = true;
            else if (name == TranslationTextFeature) usesTranslationText = true;
            else if (name == NavigationPathFeature) usesNavigationPath = true;
            else if (name == ClientLanguagesFeature) usesClientLanguages = true;
        }
        return true;
    }

    // client-languages/1: the hello's languages, absent or an array of at most MaximumClientLanguages strings. The
    // language tags among them are kept in BCP 47 case, each once, in their order; other strings, such as a tag longer
    // than 64 characters, are ignored. Without the feature the member is ignored.
    private static bool TryReadLanguages(JsonElement root, bool usesClientLanguages, out IReadOnlyList<string> languages)
    {
        languages = [];
        if (!usesClientLanguages || !root.TryGetProperty("languages", out var element)) return true;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaximumClientLanguages) return false;
        List<string> tags = [];
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return false;
            var tag = item.GetString()!;
            if (!IsLocale(tag)) continue;
            tag = CanonicalLocaleCase(tag);
            if (!tags.Contains(tag, StringComparer.Ordinal)) tags.Add(tag);
        }
        languages = tags.Count == 0 ? [] : Array.AsReadOnly(tags.ToArray());
        return true;
    }

    // A locale tag in the case BCP 47 recommends (RFC 5646 2.1.1): the language and extended language subtags lower case,
    // a script title case (zh-Hant), a region upper case (de-CH, es-419), and everything else, including the subtags
    // after an extension or private-use singleton, lower case: DE-ch becomes de-CH, ZH-hant-tw zh-Hant-TW.
    internal static string CanonicalLocaleCase(string tag)
    {
        var subtags = tag.Split('-');
        var afterSingleton = false;
        for (var index = 0; index < subtags.Length; index++)
        {
            var subtag = subtags[index];
            if (index == 0 || afterSingleton) subtags[index] = subtag.ToLowerInvariant();
            else if (subtag.Length == 1)
            {
                afterSingleton = true;
                subtags[index] = subtag.ToLowerInvariant();
            }
            else if (subtag.Length == 4 && char.IsAsciiLetter(subtag[0]))
                subtags[index] = char.ToUpperInvariant(subtag[0]) + subtag[1..].ToLowerInvariant();
            else if (subtag.Length == 2 || (subtag.Length == 3 && char.IsAsciiDigit(subtag[0])))
                subtags[index] = subtag.ToUpperInvariant();
            else subtags[index] = subtag.ToLowerInvariant();
        }
        return string.Join('-', subtags);
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
internal sealed class BridgeConnection(string key, string clientKey, string? transportKey = null)
{
    // The connection's key, unique among the dispatcher's connections for its lifetime: what selects the connection's
    // presentations and identifies it to application code. The transport's key for a transport whose keys never repeat,
    // otherwise the transport's key with a "#n" suffix (BridgeDispatcher.NewConnectionKey).
    internal string Key { get; } = key;
    // The key the transport reports the connection with, which its targeted pushes and close name.
    internal string TransportKey { get; } = transportKey ?? key;
    internal string ClientKey { get; } = clientKey;
    // The session the hello named.
    internal string? Session { get; set; }
    internal string? ClientName { get; set; }
    internal string? ClientVersion { get; set; }
    internal bool HelloCompleted { get; set; }
    // Whether the connection uses text-splice/1: both its hello and the producer list it.
    internal bool UsesTextSplice { get; set; }
    // Whether the connection uses presentation-epoch/1 and presented/1.
    internal bool UsesPresentationEpoch { get; set; }
    internal bool UsesPresented { get; set; }
    // Whether the connection uses field-flush/1.
    internal bool UsesFieldFlush { get; set; }
    // Whether the connection uses field-paths/1.
    internal bool UsesFieldPaths { get; set; }
    internal bool UsesTranslationText { get; set; }
    // Whether the connection uses appearance/1.
    internal bool UsesAppearance { get; set; }
    // Whether the connection uses view-requests/1.
    internal bool UsesViewRequests { get; set; }
    // Whether the connection uses request-session/1: every later request must name Session.
    internal bool UsesRequestSession { get; set; }
    // region-collections/1: the connection may send region requests.
    internal bool UsesRegionCollections { get; set; }
    // client-errors/1: whether the page may report errors, and the connection's limit (BridgeClientErrors), which the
    // window's limit guards.
    internal bool UsesClientErrors { get; set; }
    internal BridgeClientErrors.Limit ClientErrors { get; } = new();
    // query-collections/1: the connection reads query collection ranges (query-rows, query-set, query-index).
    internal bool UsesQueryCollections { get; set; }
    // query-collections/1: the requests in flight per route, member and kind, which the route bounds per connection.
    private readonly Dictionary<string, int> _queryRequests = new(StringComparer.Ordinal);

    // Admits a query request unless the connection already has limit of them in flight.
    internal bool TryEnterQuery(string key, int limit)
    {
        lock (_queryRequests)
        {
            var count = _queryRequests.GetValueOrDefault(key);
            if (count >= limit) return false;
            _queryRequests[key] = count + 1;
            return true;
        }
    }

    internal void ExitQuery(string key)
    {
        lock (_queryRequests)
            if (_queryRequests.TryGetValue(key, out var count) && count > 1) _queryRequests[key] = count - 1;
            else _queryRequests.Remove(key);
    }
    // navigation-path/1: the connection receives navigation-path pushes and may send navigation requests.
    internal bool UsesNavigationPath { get; set; }
    // client-languages/1: the page's languages, most preferred first; empty without the feature.
    internal IReadOnlyList<string> Languages { get; set; } = [];
    // Whether the window's closing push waits for this connection's closing-reply; the dispatcher's gate guards it.
    internal bool ClosingPending { get; set; }
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

// A route handler that receives the presented requests (presented/1) of its route: the page painted the route at
// revision or later. It does not wait.
internal interface IBridgePresentedHandler
{
    void Presented(BridgeConnection connection, long revision);
}

// Handles the requests of one route (RegisterRoute). It admits a request before its returned task first yields, so
// the dispatcher keeps a connection's admission order. It returns the reply as UTF-8 JSON.
internal interface IBridgeRouteHandler
{
    ValueTask<ReadOnlyMemory<byte>> HandleAsync(BridgeRouteRequest request, CancellationToken cancellationToken);
}
