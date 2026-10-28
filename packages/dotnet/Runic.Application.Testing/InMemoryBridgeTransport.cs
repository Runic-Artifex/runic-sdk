using System.Text;
using Runic.Application.Views;

namespace Runic.Application.Testing;

/// <summary>Arguments delivered to a generated Bridge route by a headless test.</summary>
/// <remarks>
/// They address the Views protocol 3 routes that <see cref="InMemoryBridgeTransport.Call"/> reaches while the generated
/// Bridges still speak protocol 3 internally. Protocol 4 tests send requests with
/// <see cref="InMemoryBridgeTransport.Request"/>.
/// </remarks>
public sealed record BridgeTestArguments(
    long? Int64Value = null,
    bool? BooleanValue = null,
    string? StringValue = null,
    string? ClientKey = null,
    string? ConnectionKey = null) : IBridgeArguments
{
    /// <inheritdoc />
    public long GetInt64() => Int64Value ?? throw new InvalidOperationException("No integer argument was supplied.");
    /// <inheritdoc />
    public bool GetBoolean() => BooleanValue ?? throw new InvalidOperationException("No Boolean argument was supplied.");
    /// <inheritdoc />
    public string GetString() => StringValue ?? throw new InvalidOperationException("No string argument was supplied.");
}

/// <summary>A push that <see cref="InMemoryBridgeTransport"/> received from its window.</summary>
/// <param name="Json">The push, one JSON object.</param>
/// <param name="ConnectionKey">The connection a targeted push was sent to, or <see langword="null"/> for a broadcast to every connection.</param>
public sealed record RunicBridgeMessage(string Json, string? ConnectionKey);

/// <summary>
/// An in-memory <see cref="IBridgeTransport"/> for headless tests: requests run the window's real request handler and
/// generated Bridges, and pushes are recorded instead of reaching a page. No native UI runs.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Request"/> and <see cref="RequestAsync"/> send one Views protocol 4 request as JSON on a connection and
/// return the reply. A connection opens with its first request and stays open until <see cref="CloseConnection"/>;
/// like a page, it sends <c>hello</c> first. <see cref="Messages"/> lists the pushes the window sent, broadcasts and
/// pushes to one connection alike, in order.
/// </para>
/// <para>
/// Until the generated Bridges speak protocol 4, <see cref="Call"/>, <see cref="CallAsync"/>, <see cref="Routes"/> and
/// <see cref="TakePublications"/> reach their protocol 3 routes and state frames, as <see cref="RunicViewDriver{TModel}"/> does.
/// </para>
/// </remarks>
public sealed class InMemoryBridgeTransport : IBridgeConnectionTransport, IDisposable
{
    /// <summary>The connection key of requests that name none.</summary>
    public const string DefaultConnectionKey = "connection-1";

    /// <summary>The client key of requests that name none.</summary>
    public const string DefaultClientKey = "client-1";

    private readonly object _gate = new();
    private readonly HashSet<string> _connections = new(StringComparer.Ordinal);
    private readonly List<RunicBridgeMessage> _messages = [];
    private readonly List<(string Route, string Json)> _publications = [];
    private TaskCompletionSource _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _messaged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IBridgeRequestHandler? _handler;
    private bool _disposed;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The transport already serves a handler.</exception>
    /// <exception cref="ObjectDisposedException">The transport was disposed.</exception>
    public IDisposable Serve(IBridgeRequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_handler is not null) throw new InvalidOperationException("The transport already serves a request handler.");
            _handler = handler;
        }
        // Protocol 3 state frames of the generated Bridges reach TakePublications.
        if (handler is BridgeDispatcher dispatcher) dispatcher.Legacy.SetPublisher(PublishLegacyAsync);
        return new ServeLease(this, handler);
    }

    /// <summary>Records a push to every connection.</summary>
    public ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(new(Encoding.UTF8.GetString(message.Span), null));
        return ValueTask.CompletedTask;
    }

    /// <summary>Records a push to one open connection.</summary>
    public ValueTask<bool> SendAsync(string connectionKey, ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionKey);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            if (_disposed || !_connections.Contains(connectionKey)) return ValueTask.FromResult(false);
        Record(new(Encoding.UTF8.GetString(message.Span), connectionKey));
        return ValueTask.FromResult(true);
    }

    /// <summary>Sends one request on a connection and waits for its reply.</summary>
    /// <param name="json">The request, one JSON object.</param>
    /// <param name="connectionKey">The connection; it opens with its first request.</param>
    /// <param name="clientKey">The client of a connection's first request; by default <see cref="DefaultClientKey"/>.</param>
    /// <returns>The reply's JSON.</returns>
    /// <exception cref="InvalidOperationException">No window serves the transport.</exception>
    public string Request(string json, string connectionKey = DefaultConnectionKey, string? clientKey = null) =>
        RequestAsync(json, connectionKey, clientKey).AsTask().GetAwaiter().GetResult();

    /// <summary>Sends one request on a connection and returns its reply when it arrives.</summary>
    /// <param name="json">The request, one JSON object.</param>
    /// <param name="connectionKey">The connection; it opens with its first request.</param>
    /// <param name="clientKey">The client of a connection's first request; by default <see cref="DefaultClientKey"/>.</param>
    /// <param name="cancellationToken">Cancels the request, as a closing page does.</param>
    /// <returns>The reply's JSON.</returns>
    /// <exception cref="InvalidOperationException">No window serves the transport.</exception>
    public async ValueTask<string> RequestAsync(string json, string connectionKey = DefaultConnectionKey,
        string? clientKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrEmpty(connectionKey);
        IBridgeRequestHandler handler;
        lock (_gate)
        {
            ThrowIfDisposed();
            handler = _handler ?? throw new InvalidOperationException("No window serves this transport.");
            _connections.Add(connectionKey);
        }
        var reply = await handler.HandleAsync(
            new BridgeRequest(Encoding.UTF8.GetBytes(json), clientKey ?? DefaultClientKey, connectionKey),
            cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(reply.Span);
    }

    /// <summary>Closes a connection, as a page that reloads or loses its connection does, and reports it to the window.</summary>
    /// <param name="connectionKey">The connection to close.</param>
    public void CloseConnection(string connectionKey = DefaultConnectionKey)
    {
        ArgumentNullException.ThrowIfNull(connectionKey);
        IBridgeRequestHandler? handler;
        lock (_gate)
        {
            if (!_connections.Remove(connectionKey)) return;
            handler = _handler;
        }
        handler?.ConnectionClosed(connectionKey);
    }

    /// <summary>The pushes the window sent, in order, that <see cref="TakeMessages"/> has not taken.</summary>
    public IReadOnlyList<RunicBridgeMessage> Messages
    {
        get { lock (_gate) return [.. _messages]; }
    }

    /// <summary>Returns and removes the pushes the window sent since the previous call, in order.</summary>
    public IReadOnlyList<RunicBridgeMessage> TakeMessages()
    {
        lock (_gate)
        {
            RunicBridgeMessage[] messages = [.. _messages];
            _messages.Clear();
            return messages;
        }
    }

    /// <summary>Names of the currently bound Views protocol 3 routes of the generated Bridges.</summary>
    public IReadOnlyList<string> Routes => Dispatcher?.Legacy.Names ?? [];

    /// <summary>Calls a synchronous Views protocol 3 route of a generated Bridge.</summary>
    /// <exception cref="KeyNotFoundException">The route is not bound.</exception>
    /// <exception cref="InvalidOperationException">The route is asynchronous.</exception>
    public string Call(string name, BridgeTestArguments? arguments = null)
    {
        var (sync, _) = Find(name);
        if (sync is null) throw new InvalidOperationException($"Route '{name}' is asynchronous.");
        return sync(arguments ?? new());
    }

    /// <summary>Calls an asynchronous Views protocol 3 route of a generated Bridge and observes its result.</summary>
    /// <exception cref="KeyNotFoundException">The route is not bound.</exception>
    /// <exception cref="InvalidOperationException">The route is synchronous.</exception>
    public ValueTask<string> CallAsync(string name, BridgeTestArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var (_, asynchronous) = Find(name);
        if (asynchronous is null) throw new InvalidOperationException($"Route '{name}' is synchronous.");
        return asynchronous(arguments ?? new(), cancellationToken);
    }

    // Completes at the next publication. Drivers wait on it instead of polling.
    internal Task NextPublication
    {
        get { lock (_gate) return _published.Task; }
    }

    // Completes at the next push.
    internal Task NextMessage
    {
        get { lock (_gate) return _messaged.Task; }
    }

    // Whether a bound route is asynchronous, such as an awaited command.
    internal bool IsAsync(string name) => Find(name).Async is not null;

    /// <summary>
    /// Returns and removes the Views protocol 3 state frames published since the previous call, in order, across all
    /// routes. Drivers and trackers then do not see them.
    /// </summary>
    /// <exception cref="InvalidOperationException">A delta frame names a collection change kind the client does not know.</exception>
    public IReadOnlyList<RunicViewPublication> TakePublications() =>
        TakeFrames().Select(frame => new RunicViewPublication(frame.Route, frame.Json)).ToArray();

    // Unparsed frames, so the drivers' log parses each route's frames only when that route takes them.
    internal (string Route, string Json)[] TakeFrames()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var result = _publications.ToArray();
            _publications.Clear();
            return result;
        }
    }

    /// <summary>Stops serving the window and removes all recorded pushes and publications.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_handler is BridgeDispatcher dispatcher) dispatcher.Legacy.SetPublisher(null);
            _handler = null;
            _connections.Clear();
            _messages.Clear();
            _publications.Clear();
        }
    }

    private BridgeDispatcher? Dispatcher
    {
        get { lock (_gate) return _handler as BridgeDispatcher; }
    }

    private (Func<IBridgeArguments, string>? Sync, Func<IBridgeArguments, CancellationToken, ValueTask<string>>? Async) Find(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        BridgeDispatcher? dispatcher;
        lock (_gate)
        {
            ThrowIfDisposed();
            dispatcher = _handler as BridgeDispatcher;
        }
        if (dispatcher is null || !dispatcher.Legacy.TryFind(name, out var sync, out var asynchronous))
            throw new KeyNotFoundException($"Route '{name}' is not bound.");
        return (sync, asynchronous);
    }

    private ValueTask PublishLegacyAsync(string route, ReadOnlyMemory<byte> stateUtf8, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = Encoding.UTF8.GetString(stateUtf8.Span);
        lock (_gate)
        {
            ThrowIfDisposed();
            _publications.Add((route, json));
            _published.TrySetResult();
            _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return ValueTask.CompletedTask;
    }

    private void Record(RunicBridgeMessage message)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _messages.Add(message);
            _messaged.TrySetResult();
            _messaged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class ServeLease(InMemoryBridgeTransport owner, IBridgeRequestHandler handler) : IDisposable
    {
        private InMemoryBridgeTransport? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } transport) return;
            lock (transport._gate)
                if (ReferenceEquals(transport._handler, handler)) transport._handler = null;
            if (handler is BridgeDispatcher dispatcher) dispatcher.Legacy.SetPublisher(null);
        }
    }
}
