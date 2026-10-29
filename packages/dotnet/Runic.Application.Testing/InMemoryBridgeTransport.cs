using System.Text;
using Runic.Application.Views;

namespace Runic.Application.Testing;

/// <summary>A push that <see cref="InMemoryBridgeTransport"/> received from its window.</summary>
/// <param name="Json">The push, one JSON object.</param>
/// <param name="ConnectionKey">The connection a targeted push was sent to, or <see langword="null"/> for a broadcast to every connection.</param>
/// <remarks>
/// <see cref="Kind"/> and <see cref="Route"/> are read from <see cref="Json"/>, so a test can select the pushes of one
/// route: <c>host.Transport.TakeMessages().Where(message =&gt; message.Route == editor.Route)</c>.
/// </remarks>
public sealed record RunicBridgeMessage(string Json, string? ConnectionKey)
{
    /// <summary>
    /// The push's <c>kind</c>, such as <c>state</c>, <c>frame</c>, <c>failure</c> or <c>operation</c>, or
    /// <see langword="null"/> when it names none.
    /// </summary>
    public string? Kind => RunicViewPublication.HeaderOf(Json).Kind;

    /// <summary>The route the push belongs to (its <c>route</c>), or <see langword="null"/> when it names none.</summary>
    public string? Route => RunicViewPublication.HeaderOf(Json).Route;
}

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
/// <see cref="RunicViewDriver{TModel}"/> and <see cref="RunicWindowTestHost{TViewModel}"/> drive Views through this
/// transport by member, as the generated TypeScript clients do.
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
    // The hello the drivers sent on each connection, completed when it was answered.
    private readonly Dictionary<string, Task> _greeted = new(StringComparer.Ordinal);
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

    /// <summary>Sends one request on a connection and blocks the calling thread until its reply.</summary>
    /// <param name="json">The request, one JSON object.</param>
    /// <param name="connectionKey">The connection; it opens with its first request.</param>
    /// <param name="clientKey">The client of a connection's first request; by default <see cref="DefaultClientKey"/>.</param>
    /// <returns>The reply's JSON.</returns>
    /// <remarks>
    /// The calling thread waits while the window handles the request, and the window's own asynchronous work, such as
    /// a model turn or an awaited command, continues on thread-pool threads. A suite that runs many tests in parallel on
    /// thread-pool threads can starve the pool this way: each blocked test holds a thread that the replies it waits for
    /// need. Use <see cref="RequestAsync"/> in such suites; it never blocks.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No window serves the transport.</exception>
    public string Request(string json, string connectionKey = DefaultConnectionKey, string? clientKey = null) =>
        RequestAsync(json, connectionKey, clientKey).AsTask().GetAwaiter().GetResult();

    /// <summary>Sends one request on a connection and returns its reply when it arrives, without blocking a thread.</summary>
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
            _greeted.Remove(connectionKey);
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

    private readonly Dictionary<(string Connection, string Route, string Member), int> _edits = [];

    // The next edit number of a member on a connection, as a generated setter numbers its edits.
    internal int NextEdit(string connectionKey, string route, string member)
    {
        lock (_gate)
        {
            var next = _edits.GetValueOrDefault((connectionKey, route, member)) + 1;
            _edits[(connectionKey, route, member)] = next;
            return next;
        }
    }

    // Whether the window serves the route.
    internal bool Serves(string route)
    {
        lock (_gate) return _handler is BridgeDispatcher dispatcher && dispatcher.Serves(route);
    }

    private int _sessions;

    // Sends the hello of a connection the drivers use, once; concurrent requests on the connection wait for the same
    // hello. Each hello names a new session "{key}-session{n}": a reconnected connection cannot reuse the session of the
    // one that closed.
    internal async ValueTask EnsureHelloAsync(string connectionKey, string? clientKey = null,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? hello = null;
        Task pending;
        int session = 0;
        lock (_gate)
        {
            if (!_greeted.TryGetValue(connectionKey, out pending!))
            {
                hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _greeted.Add(connectionKey, pending = hello.Task);
                session = ++_sessions;
            }
        }
        if (hello is null)
        {
            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        try
        {
            // Not cancelled by this caller: other requests on the connection wait for the same hello.
            var reply = await RequestAsync(RunicJson.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("kind", "hello");
                writer.WriteNumber("protocol", 4);
                writer.WriteString("session", $"{connectionKey}-session{session}");
                writer.WriteStartObject("client");
                writer.WriteString("name", "Runic.Application.Testing");
                writer.WriteString("version", "1.0.0");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }), connectionKey, clientKey, CancellationToken.None).ConfigureAwait(false);
            // A test may have sent the connection's hello itself.
            if (!reply.StartsWith("{\"ok\":true", StringComparison.Ordinal)
                && !reply.Contains("already completed a hello", StringComparison.Ordinal))
                throw new InvalidOperationException($"The window refused the test connection's hello: {reply}");
            hello.SetResult();
        }
        catch (Exception error)
        {
            lock (_gate)
                if (_greeted.TryGetValue(connectionKey, out var current) && ReferenceEquals(current, hello.Task))
                    _greeted.Remove(connectionKey);
            hello.SetException(error);
            // Observed: the waiting requests rethrow it.
            _ = hello.Task.Exception;
            throw;
        }
    }

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
            _handler = null;
            _connections.Clear();
            _messages.Clear();
            _publications.Clear();
        }
    }

    private void Record(RunicBridgeMessage message)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _messages.Add(message);
            // Broadcast states, frames and failures also reach the drivers' publication log.
            if (message.ConnectionKey is null && RunicViewPublication.HeaderOf(message.Json) is ("state" or "frame" or "failure", { } route))
            {
                _publications.Add((route, message.Json));
                _published.TrySetResult();
                _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
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
        }
    }
}
