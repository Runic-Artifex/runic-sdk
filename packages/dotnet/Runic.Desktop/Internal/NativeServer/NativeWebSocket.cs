using System.Net.WebSockets;
using System.Threading.Channels;
using System.Threading.Tasks.Sources;
using static Runic.Desktop.Internal.NativeServer.NativeServerInterop;

namespace Runic.Desktop.Internal.NativeServer;

// A WebSocket the native server accepted. The library reassembles messages and leases each to this socket until it
// was read: ReceiveAsync copies straight from the library's buffer into the caller's, once, and then consumes the
// message, which frees its place in the connection's receive window (backpressure towards the peer). Sends are copied
// into the library's bounded queue; a full queue makes the send wait for the library's completion.
//
// Writing is coalesced as Kestrel's output pipe does: a send only queues, and the send that finds no write in progress
// schedules one flush, which writes everything queued by then in one vectored write. The flush is a work item on the
// sending thread's own thread-pool queue, so it usually runs on that thread once the current work item returns (a
// reply right after its handler, a burst of pushes after the last one) and wakes no other thread.
internal sealed class NativeWebSocket : WebSocket, IValueTaskSource<bool>, IThreadPoolWorkItem, IWholeMessageSocket
{
    private readonly NativePresentationServer _server;
    private readonly Channel<Incoming> _incoming = Channel.CreateUnbounded<Incoming>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    // Guards the leased message memory against release while a receive copies from it.
    private readonly object _gate = new();
    private ManualResetValueTaskSourceCore<bool> _send = new() { RunContinuationsAsynchronously = true };
    private Incoming? _current;
    private int _offset;
    private byte[]? _fragments;
    private int _fragmentLength;
    private WebSocketState _state = WebSocketState.Open;
    private WebSocketCloseStatus? _closeStatus;
    private string? _closeDescription;
    private bool _released;

    internal NativeWebSocket(NativePresentationServer server, ulong id, string? subProtocol)
    {
        _server = server;
        Id = id;
        SubProtocol = subProtocol;
    }

    internal ulong Id { get; }

    public override WebSocketCloseStatus? CloseStatus => _closeStatus;
    public override string? CloseStatusDescription => _closeDescription;
    public override WebSocketState State => _state;
    public override string? SubProtocol { get; }

    public int RemainingInMessage => _current is { } current ? current.Length - _offset : 0;

    // Library thread: queue only.
    internal unsafe void Deliver(ulong message, uint kind, byte* data, nuint length) =>
        _incoming.Writer.TryWrite(new Incoming(message, kind == MessageText ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
            (nint)data, checked((int)length), CloseCode: 0, Description: null));

    internal void PeerClosed(int code, string description)
    {
        _incoming.Writer.TryWrite(new Incoming(0, WebSocketMessageType.Close, 0, 0, code, description));
        _incoming.Writer.TryComplete();
    }

    internal void SendCompleted(bool accepted) => _send.SetResult(accepted);

    // The server is stopping and will free the leased memory.
    internal void AbortFromServer()
    {
        lock (_gate)
        {
            _released = true;
            _current = null;
            if (_state is not (WebSocketState.Closed or WebSocketState.Aborted)) _state = WebSocketState.Aborted;
        }
        _incoming.Writer.TryComplete();
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        var result = await ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        return result.MessageType == WebSocketMessageType.Close
            ? new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, _closeStatus, _closeDescription)
            : new WebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage);
    }

    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ThrowIfReceiveClosed();
        if (_current is null)
        {
            Incoming next;
            try
            {
                next = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                _state = WebSocketState.Aborted;
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "The WebSocket connection was aborted.");
            }
            if (next.Type == WebSocketMessageType.Close)
            {
                if (next.CloseCode < 0)
                {
                    _state = WebSocketState.Aborted;
                    throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely,
                        "The remote party closed the WebSocket connection without completing the close handshake.");
                }
                _closeStatus = (WebSocketCloseStatus)next.CloseCode;
                _closeDescription = next.Description;
                _state = _state == WebSocketState.CloseSent ? WebSocketState.Closed : WebSocketState.CloseReceived;
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }
            _current = next;
            _offset = 0;
        }
        var current = _current.Value;
        var count = Copy(current, buffer.Span);
        _offset += count;
        var end = _offset == current.Length;
        if (end)
        {
            _current = null;
            try
            {
                Consume(_server.Handle, Id, current.Message);
            }
            catch (ObjectDisposedException)
            {
            }
        }
        return new ValueWebSocketReceiveResult(count, current.Type, end);
    }

    private unsafe int Copy(Incoming current, Span<byte> destination)
    {
        lock (_gate)
        {
            if (_released)
            {
                throw new WebSocketException(WebSocketError.InvalidState, "The WebSocket was released.");
            }
            var count = Math.Min(destination.Length, current.Length - _offset);
            new ReadOnlySpan<byte>((byte*)current.Data + _offset, count).CopyTo(destination);
            return count;
        }
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
        CancellationToken cancellationToken) =>
        SendAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken).AsTask();

    public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (messageType == WebSocketMessageType.Close)
        {
            throw new ArgumentException("Use CloseOutputAsync to close.", nameof(messageType));
        }
        if (_state is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            throw new WebSocketException(WebSocketError.InvalidState, $"The WebSocket is {_state}.");
        }
        // The library sends whole messages: fragments are joined until the last one.
        if (!endOfMessage || _fragments is not null)
        {
            Append(buffer.Span);
            if (!endOfMessage) return;
            var whole = _fragments.AsMemory(0, _fragmentLength);
            try
            {
                await SendWholeAsync(whole, messageType).ConfigureAwait(false);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(_fragments!);
                _fragments = null;
                _fragmentLength = 0;
            }
            return;
        }
        await SendWholeAsync(buffer, messageType).ConfigureAwait(false);
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_fragments is null || _fragmentLength + data.Length > _fragments.Length)
        {
            var grown = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(_fragmentLength + data.Length, 4096) * 2);
            if (_fragments is not null)
            {
                _fragments.AsSpan(0, _fragmentLength).CopyTo(grown);
                System.Buffers.ArrayPool<byte>.Shared.Return(_fragments);
            }
            _fragments = grown;
        }
        data.CopyTo(_fragments.AsSpan(_fragmentLength));
        _fragmentLength += data.Length;
    }

    private ValueTask SendWholeAsync(ReadOnlyMemory<byte> message, WebSocketMessageType type)
    {
        _send.Reset();
        var result = QueueNative(message.Span, type);
        if (result == Flush)
        {
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
            result = Ok;
        }
        return CompleteAsync(result);
    }

    private unsafe int QueueNative(ReadOnlySpan<byte> message, WebSocketMessageType type)
    {
        try
        {
            fixed (byte* data = message)
            {
                return Queue(_server.Handle, Id, type == WebSocketMessageType.Text ? MessageText : MessageBinary, data,
                    (nuint)message.Length, NativePresentationServer.SocketToken(Id));
            }
        }
        catch (ObjectDisposedException)
        {
            return Closed;
        }
    }

    // The flush a send scheduled: writes the library's queue from this thread.
    void IThreadPoolWorkItem.Execute()
    {
        try
        {
            FlushQueue(_server.Handle, Id);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private unsafe int CloseNative(WebSocketCloseStatus status, byte[] reason)
    {
        try
        {
            fixed (byte* pointer = reason)
            {
                return Close(_server.Handle, Id, (ushort)status, new RunicStr { Pointer = pointer, Length = (nuint)reason.Length },
                    NativePresentationServer.SocketToken(Id));
            }
        }
        catch (ObjectDisposedException)
        {
            return Closed;
        }
    }

    private ValueTask CompleteAsync(int result) =>
        result == Ok ? ValueTask.CompletedTask : CompleteSlowAsync(result);

    private async ValueTask CompleteSlowAsync(int result)
    {
        if (result == Pending)
        {
            result = await new ValueTask<bool>(this, _send.Version).ConfigureAwait(false) ? Ok : Closed;
        }
        if (result != Ok)
        {
            _state = WebSocketState.Aborted;
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely,
                result == Closed ? "The WebSocket connection is closed." : $"The WebSocket send failed: {LastErrorMessage()}");
        }
    }

    public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_state is WebSocketState.CloseSent or WebSocketState.Closed or WebSocketState.Aborted)
        {
            return;
        }
        var reason = statusDescription ?? string.Empty;
        // A close reason has at most 123 bytes of UTF-8.
        while (System.Text.Encoding.UTF8.GetByteCount(reason) > 123) reason = reason[..^1];
        _send.Reset();
        var result = CloseNative(closeStatus, System.Text.Encoding.UTF8.GetBytes(reason));
        _state = _state == WebSocketState.CloseReceived ? WebSocketState.Closed : WebSocketState.CloseSent;
        if (result == Closed || result == UnknownId) return;
        await CompleteAsync(result).ConfigureAwait(false);
    }

    public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
        CancellationToken cancellationToken)
    {
        await CloseOutputAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[256];
        while (_state == WebSocketState.CloseSent)
        {
            var result = await ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) break;
        }
    }

    public override void Abort()
    {
        if (_state is WebSocketState.Closed or WebSocketState.Aborted) return;
        _state = WebSocketState.Aborted;
        try
        {
            AbortWebSocket(_server.Handle, Id);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public override void Dispose()
    {
        lock (_gate)
        {
            if (_released) return;
            _released = true;
            _current = null;
        }
        if (_state is not (WebSocketState.Closed or WebSocketState.Aborted)) _state = WebSocketState.Closed;
        _incoming.Writer.TryComplete();
        _server.Forget(this);
        try
        {
            Release(_server.Handle, Id);
        }
        catch (ObjectDisposedException)
        {
        }
        if (_fragments is not null)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(_fragments);
            _fragments = null;
        }
    }

    private void ThrowIfReceiveClosed()
    {
        if (_state is WebSocketState.CloseReceived or WebSocketState.Closed or WebSocketState.Aborted || _released)
        {
            throw new WebSocketException(WebSocketError.InvalidState, $"The WebSocket is {_state}.");
        }
    }

    bool IValueTaskSource<bool>.GetResult(short token) => _send.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _send.GetStatus(token);
    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _send.OnCompleted(continuation, state, token, flags);

    private readonly struct Incoming(ulong message, WebSocketMessageType type, nint data, int length, int CloseCode,
        string? Description)
    {
        internal ulong Message { get; } = message;
        internal WebSocketMessageType Type { get; } = type;
        internal nint Data { get; } = data;
        internal int Length { get; } = length;
        internal int CloseCode { get; } = CloseCode;
        internal string? Description { get; } = Description;
    }
}
