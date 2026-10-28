using System.Text;
using System.Threading.Channels;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests.Conformance;

// The transport of a producer scenario (specs/application/conformance/README.md, "Steps"). It keeps, per connection,
// the messages the producer sent to it in the order it sent them: each reply when the handler's task completes,
// tagged with the request step's id, and each push, broadcast to every open connection or sent to one.
//
// delivery: hold makes the transport stop taking pushes: a push sent while it is held is neither recorded nor
// completed until Release, which records the held pushes in the order they were sent and then completes them.
// Replies are never held.
internal sealed class ScenarioTransport : IBridgeConnectionTransport, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ScenarioConnection> _connections = new(StringComparer.Ordinal);
    private readonly List<HeldPush> _held = [];
    private IBridgeRequestHandler? _handler;
    private bool _holding;
    private bool _disposed;

    public IDisposable Serve(IBridgeRequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handler is not null) throw new InvalidOperationException("The transport already serves a request handler.");
            _handler = handler;
        }
        return new Lease(this, handler);
    }

    public ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
        new(Push(null, message));

    public ValueTask<bool> SendAsync(string connectionKey, ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionKey);
        return new(Push(connectionKey, message));
    }

    // Opens a connection with a fresh connection key and client key.
    internal ScenarioConnection Connect(string name)
    {
        lock (_gate)
        {
            var number = _connections.Count + 1;
            var connection = new ScenarioConnection(name, $"connection-{number}", $"client-{number}");
            _connections.Add(connection.Key, connection);
            return connection;
        }
    }

    // Closes the connection and reports it to the window; replies still pending on it are dropped.
    internal void Disconnect(ScenarioConnection connection)
    {
        IBridgeRequestHandler? handler;
        lock (_gate)
        {
            connection.IsOpen = false;
            handler = _handler;
        }
        connection.Closed.Cancel();
        handler?.ConnectionClosed(connection.Key);
    }

    // Delivers a request on the connection; its reply is recorded when the handler's task completes.
    internal void Send(ScenarioConnection connection, byte[] message, string requestId)
    {
        IBridgeRequestHandler handler;
        lock (_gate) handler = _handler ?? throw new InvalidOperationException("No window serves the scenario transport.");
        var reply = handler.HandleAsync(new BridgeRequest(message, connection.ClientKey, connection.Key), connection.Closed.Token);
        if (reply.IsCompleted) RecordReply(connection, requestId, reply.Result);
        else _ = RecordReplyAsync(connection, requestId, reply);
    }

    internal void Hold()
    {
        lock (_gate) _holding = true;
    }

    internal void Release()
    {
        HeldPush[] held;
        lock (_gate)
        {
            _holding = false;
            held = [.. _held];
            _held.Clear();
            foreach (var push in held) push.Completion.SetResult(Record(push.ConnectionKey, push.Json));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _handler = null;
            foreach (var push in _held) push.Completion.TrySetResult(false);
            _held.Clear();
        }
    }

    private async Task RecordReplyAsync(ScenarioConnection connection, string requestId, ValueTask<ReadOnlyMemory<byte>> reply)
    {
        // The handler's task does not fault (IBridgeRequestHandler.HandleAsync); a fault still reaches the runner as a
        // message it does not expect.
        try { RecordReply(connection, requestId, await reply.ConfigureAwait(false)); }
        catch (Exception error) { RecordReply(connection, requestId, Encoding.UTF8.GetBytes($"\"handler faulted: {error.GetType().Name}\"")); }
    }

    private void RecordReply(ScenarioConnection connection, string requestId, ReadOnlyMemory<byte> reply)
    {
        lock (_gate)
            if (connection.IsOpen) connection.Messages.Writer.TryWrite(new(requestId, Encoding.UTF8.GetString(reply.Span)));
    }

    private Task<bool> Push(string? connectionKey, ReadOnlyMemory<byte> message)
    {
        // The message is valid only until the call completes; a held push keeps its own copy.
        var json = Encoding.UTF8.GetString(message.Span);
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(false);
            if (!_holding) return Task.FromResult(Record(connectionKey, json));
            var push = new HeldPush(connectionKey, json, new(TaskCreationOptions.RunContinuationsAsynchronously));
            _held.Add(push);
            return push.Completion.Task;
        }
    }

    // Records a push to one connection, or to every open one; the gate is held.
    private bool Record(string? connectionKey, string json)
    {
        if (connectionKey is not null)
        {
            if (!_connections.TryGetValue(connectionKey, out var target) || !target.IsOpen) return false;
            target.Messages.Writer.TryWrite(new(null, json));
            return true;
        }
        foreach (var connection in _connections.Values)
            if (connection.IsOpen) connection.Messages.Writer.TryWrite(new(null, json));
        return true;
    }

    private sealed record HeldPush(string? ConnectionKey, string Json, TaskCompletionSource<bool> Completion);

    private sealed class Lease(ScenarioTransport owner, IBridgeRequestHandler handler) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
                if (ReferenceEquals(owner._handler, handler)) owner._handler = null;
        }
    }
}

// One connection of a scenario, named by its connect step.
internal sealed class ScenarioConnection(string name, string key, string clientKey)
{
    internal string Name { get; } = name;
    internal string Key { get; } = key;
    internal string ClientKey { get; } = clientKey;
    internal bool IsOpen { get; set; } = true;
    // Cancels the requests of the connection when it closes, as a transport does.
    internal CancellationTokenSource Closed { get; } = new();
    // The messages the producer sent to the connection that no receive step has taken yet.
    internal Channel<ScenarioMessage> Messages { get; } = Channel.CreateUnbounded<ScenarioMessage>();
}

// A message the producer sent: the reply to the request step ReplyTo, or a push when ReplyTo is null.
internal sealed record ScenarioMessage(string? ReplyTo, string Json);
