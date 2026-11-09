using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Runic.Application.Views.Desktop.Hosting;

// A message of the other side that breaks the profile (host-protocol.md, Messages): the channel closes with Status.
internal sealed class HostProtocolViolationException(WebSocketCloseStatus status, string message) : Exception(message)
{
    internal WebSocketCloseStatus Status { get; } = status;
}

// The host channel closed before a request was answered or while a message was sent.
internal sealed class HostChannelClosedException(string message, Exception? inner = null) : Exception(message, inner);

// A request of the other side; the handler answers it once with ReplyAsync or FailAsync.
internal sealed class HostRequest(HostChannel channel, long id, string kind, JsonObject message)
{
    private int _answered;

    internal long Id { get; } = id;
    internal string Kind { get; } = kind;
    internal JsonObject Message { get; } = message;
    internal bool Answered => Volatile.Read(ref _answered) != 0;

    // { "id": id, "ok": true, ...members }.
    internal Task ReplyAsync(JsonObject? members = null)
    {
        var reply = new JsonObject { ["id"] = Id, ["ok"] = true };
        if (members is not null)
            foreach (var (name, value) in members.ToArray())
            {
                members.Remove(name);
                reply[name] = value;
            }
        return AnswerAsync(reply);
    }

    internal Task FailAsync(string kind, string? reason, string message) =>
        AnswerAsync(HostProtocol.Error(Id, kind, reason, message));

    private Task AnswerAsync(JsonObject reply) =>
        Interlocked.Exchange(ref _answered, 1) != 0 ? Task.CompletedTask : channel.SendAsync(reply, CancellationToken.None);
}

// One authenticated host channel after the hello exchange: JSON text messages of at most 1 MiB, requests with ids
// answered by replies, and pushes. Both roles use it. Requests of the other side run concurrently, at most 64 at once.
internal sealed class HostChannel : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly Func<HostRequest, Task> _onRequest;
    private readonly Action<JsonObject> _onPush;
    private readonly Action<Exception>? _onHandlerError;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _waiting = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private long _nextId;
    private int _running;
    private int _closing;

    internal HostChannel(WebSocket socket, long firstRequestId, Func<HostRequest, Task> onRequest, Action<JsonObject> onPush,
        Action<Exception>? onHandlerError = null)
    {
        _socket = socket;
        _nextId = firstRequestId - 1;
        _onRequest = onRequest;
        _onPush = onPush;
        _onHandlerError = onHandlerError;
    }

    // Completes when the channel closed, for any reason.
    internal Task Closed => _closed.Task;

    internal bool IsOpen => !_closed.Task.IsCompleted;

    // The close status the other side sent, or the one this side closed with.
    internal WebSocketCloseStatus? CloseStatus { get; private set; }

    internal void Start() => _ = ReceiveLoopAsync();

    // Sends a request with a new id and returns its reply, ok or not.
    internal async Task<JsonObject> RequestAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        request["id"] = id;
        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = reply;
        try
        {
            if (!IsOpen) throw new HostChannelClosedException("The host channel is closed.");
            await SendAsync(request, cancellationToken).ConfigureAwait(false);
            return await reply.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _waiting.TryRemove(id, out _); }
    }

    internal Task PushAsync(JsonObject push, CancellationToken cancellationToken = default) => SendAsync(push, cancellationToken);

    internal async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsOpen || _socket.State != WebSocketState.Open) throw new HostChannelClosedException("The host channel is closed.");
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is WebSocketException or ObjectDisposedException or IOException)
        {
            throw new HostChannelClosedException("The host channel closed while a message was sent.", error);
        }
        finally { _sendGate.Release(); }
    }

    // Reads one message of the profile: a JSON object in a text message of at most 1 MiB. Null when the other side
    // closed the connection.
    internal static async Task<JsonObject?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16_384);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                ValueWebSocketReceiveResult result;
                try { result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false); }
                catch (WebSocketException) { return null; }
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType == WebSocketMessageType.Binary)
                    throw new HostProtocolViolationException(WebSocketCloseStatus.InvalidMessageType, "A binary message is not a message of the protocol.");
                if (message.Length + result.Count > HostProtocol.MaximumMessageBytes)
                    throw new HostProtocolViolationException(WebSocketCloseStatus.MessageTooBig, "The message is larger than 1 MiB.");
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                string text;
                try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(message.GetBuffer(), 0, (int)message.Length); }
                catch (DecoderFallbackException) { throw new HostProtocolViolationException(WebSocketCloseStatus.InvalidPayloadData, "The message is not UTF-8."); }
                return HostProtocol.ParseObject(text)
                    ?? throw new HostProtocolViolationException(WebSocketCloseStatus.InvalidPayloadData, "The message is not one JSON object.");
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    // Writes one message outside a channel, such as the hello exchange.
    internal static Task SendAsync(WebSocket socket, JsonObject message, CancellationToken cancellationToken) =>
        socket.SendAsync(new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(message.ToJsonString())), WebSocketMessageType.Text, true, cancellationToken).AsTask();

    // Closes the connection with a status, waiting briefly for the other side's close; never throws.
    internal static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(status, description, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException) { }
        socket.Dispose();
    }

    internal async Task CloseAsync(WebSocketCloseStatus status, string description)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            await Closed.ConfigureAwait(false);
            return;
        }
        CloseStatus ??= status;
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try { await CloseAsync(_socket, status, description).ConfigureAwait(false); }
        finally { _sendGate.Release(); }
        Finish();
    }

    public async ValueTask DisposeAsync() => await CloseAsync(WebSocketCloseStatus.NormalClosure, "").ConfigureAwait(false);

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (true)
            {
                JsonObject? message;
                try { message = await ReceiveAsync(_socket, _stop.Token).ConfigureAwait(false); }
                catch (HostProtocolViolationException violation)
                {
                    await CloseAsync(violation.Status, violation.Message).ConfigureAwait(false);
                    return;
                }
                catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or IOException) { message = null; }
                if (message is null)
                {
                    CloseStatus ??= _socket.CloseStatus;
                    if (Interlocked.Exchange(ref _closing, 1) == 0)
                    {
                        await _sendGate.WaitAsync().ConfigureAwait(false);
                        try { await CloseAsync(_socket, WebSocketCloseStatus.NormalClosure, "").ConfigureAwait(false); }
                        finally { _sendGate.Release(); }
                    }
                    Finish();
                    return;
                }
                Dispatch(message);
            }
        }
        catch (Exception error)
        {
            _onHandlerError?.Invoke(error);
            Finish();
        }
    }

    private void Dispatch(JsonObject message)
    {
        var id = HostProtocol.Integer(message, "id");
        if (HostProtocol.Boolean(message, "ok") is not null)
        {
            // A reply; one for no waiting request is ignored.
            if (id is { } replyId && _waiting.TryRemove(replyId, out var waiting)) waiting.TrySetResult(message);
            return;
        }
        var kind = HostProtocol.String(message, "kind");
        if (id is null)
        {
            // Pushes run in the receive loop, so a window's pushes apply in the order they were sent; handlers are short.
            if (kind is null) return;
            try { _onPush(message); }
            catch (Exception error) { _onHandlerError?.Invoke(error); }
            return;
        }
        if (id < 1 || kind is null)
        {
            if (id >= 1) _ = Answer(new HostRequest(this, id.Value, "", message), "rejected", "invalid-request", "A request needs a kind.");
            return;
        }
        var request = new HostRequest(this, id.Value, kind, message);
        if (Interlocked.Increment(ref _running) > HostProtocol.MaximumWaitingRequests)
        {
            Interlocked.Decrement(ref _running);
            _ = Answer(request, "rejected", "capacity", "Too many requests are waiting for replies.");
            return;
        }
        Run(async () =>
        {
            try
            {
                await _onRequest(request).ConfigureAwait(false);
                if (!request.Answered) await request.FailAsync("failed", null, "The request was not answered.").ConfigureAwait(false);
            }
            catch (HostChannelClosedException) { }
            catch (Exception)
            {
                if (!request.Answered) await Answer(request, "failed", null, "The request failed.").ConfigureAwait(false);
                throw;
            }
            finally { Interlocked.Decrement(ref _running); }
        });
    }

    private static async Task Answer(HostRequest request, string kind, string? reason, string message)
    {
        try { await request.FailAsync(kind, reason, message).ConfigureAwait(false); }
        catch (HostChannelClosedException) { }
    }

    private void Run(Func<Task> work) => _ = Task.Run(async () =>
    {
        try { await work().ConfigureAwait(false); }
        catch (HostChannelClosedException) { }
        catch (Exception error) { _onHandlerError?.Invoke(error); }
    });

    private void Finish()
    {
        if (!_closed.TrySetResult()) return;
        _stop.Cancel();
        foreach (var waiting in _waiting.Values)
            waiting.TrySetException(new HostChannelClosedException("The host channel closed before the reply."));
    }
}
