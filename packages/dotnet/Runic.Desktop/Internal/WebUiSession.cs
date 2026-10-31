using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;

namespace Runic.Desktop.Internal;

internal sealed class WebUiSession : IAsyncDisposable
{
    private const int ReceiveBufferSize = 16 * 1024;
    private const int MaximumMessageSize = 64_000_000;
    private const int MaximumArgumentCount = 17;
    private static readonly TimeSpan MaximumYieldGrace = TimeSpan.FromSeconds(2);

    private readonly WebUiWindow _window;
    private readonly WebSocket _socket;
    private readonly object _admissionGate = new();
    private readonly CancellationTokenSource _receiveStop = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _eventGate = new(1, 1);
    private readonly HashSet<TaskCompletionSource> _pendingCalls = [];
    private readonly TaskCompletionSource _callbackDrainStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<ScriptResult>> _pendingScripts = new();
    private int _authenticated;
    private int _revoked;
    private bool _everAuthenticated;
    private int _disposed;
    private int _nextScriptId;
    private readonly TimeSpan _handshakeTimeout;
    private readonly WebUiHandshake _handshake = new();
    private int _framesReceived;
    // The window's clock (PresentationSurfaceRuntimeOptions.TimeProvider), which tests replace.
    private readonly TimeProvider _time;
    private readonly long _openedAt;

    public WebUiSession(
        WebUiWindow window,
        WebSocket socket,
        nuint clientId,
        nuint connectionId,
        string cookies,
        TimeSpan handshakeTimeout,
        TimeProvider time)
    {
        _handshakeTimeout = handshakeTimeout;
        _time = time;
        _openedAt = time.GetTimestamp();
        _window = window;
        _socket = socket;
        ClientId = clientId;
        ConnectionId = connectionId;
        Cookies = cookies;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public nuint ConnectionId { get; }

    public nuint ClientId { get; }

    public string Cookies { get; }

    public bool IsAuthenticated => Volatile.Read(ref _revoked) == 0 && Volatile.Read(ref _authenticated) != 0;

    // Completes once the ended receive loop has snapshotted the accepted callbacks
    // it drains before Disconnected. Tests use it instead of a timed wait.
    public Task CallbackDrainStarted => _callbackDrainStarted.Task;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _receiveStop.Token);
        cancellationToken = receiveCancellation.Token;
        using var handshakeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = EnforceHandshakeDeadlineAsync(handshakeDeadline.Token);
        ArrayBufferWriter<byte>? multiPacket = null;
        var multiExpected = 0;
        var receiveBuffer = new byte[ReceiveBufferSize];
        string ending = "the server stopped receiving";
        try
        {
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var messageResult = await ReceiveMessageAsync(receiveBuffer, cancellationToken).ConfigureAwait(false);
                if (!messageResult.HasValue)
                {
                    ending = _socket.CloseStatus is { } status
                        ? $"the peer closed it ({(int)status} {status})"
                        : "it closed";
                    return;
                }
                var message = messageResult.Value;
                if (Interlocked.Increment(ref _framesReceived) == 1)
                {
                    RecordHandshake($"first message, {message.Length} bytes{DescribeCommand(message.Span)}");
                }

                if (multiPacket is not null)
                {
                    if (multiPacket.WrittenCount + message.Length > multiExpected)
                    {
                        await CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "A WebUI MULTI packet exceeded its declared length.", cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    multiPacket.Write(message.Span);
                    if (multiPacket.WrittenCount == multiExpected)
                    {
                        var completed = multiPacket.WrittenMemory;
                        multiPacket = null;
                        multiExpected = 0;
                        await DispatchAsync(completed, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                // An unauthenticated peer must not reserve a large reassembly buffer;
                // the bridge sends multi-part packets only after token admission.
                if (TryGetMultiPacketLength(message.Span, out multiExpected))
                {
                    if (IsAuthenticated)
                    {
                        multiPacket = new ArrayBufferWriter<byte>(Math.Min(multiExpected, ReceiveBufferSize));
                    }
                    continue;
                }

                await DispatchAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_receiveStop.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ending = $"the connection failed ({exception.GetType().Name})";
            throw;
        }
        finally
        {
            await handshakeDeadline.CancelAsync().ConfigureAwait(false);
            // Ending first means a deadline that fires later cannot log a close for this socket.
            if (_handshake.TryEnd() && !_everAuthenticated)
            {
                RecordHandshake($"ended before authentication: {ending}");
            }
            RevokeAdmission();
            // Async callbacks release the admission turn before completing. A
            // disconnect still follows every callback accepted by this session.
            Task[] pendingCalls;
            lock (_admissionGate) pendingCalls = _pendingCalls.Select(static call => call.Task).ToArray();
            _callbackDrainStarted.TrySetResult();
            await Task.WhenAll(pendingCalls).ConfigureAwait(false);
            if (_everAuthenticated)
            {
                await DispatchEventAsync(WebUiEventType.Disconnected, string.Empty, [], CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    // A page sends its token check as soon as its socket opens (#35). A socket that
    // sends none is closed after the handshake timeout, or sooner when a newer socket
    // needs the only connection; the page's Bridge then reconnects. A socket whose
    // token check fails is closed at once. A socket whose token matched is never closed
    // here, so only a socket that failed or never checked its token gives up the slot.
    // A local client that holds a valid token can still evict a page that stays silent
    // for the grace period (see the README security notes).
    public bool IsHandshakeClosing => _handshake.IsClosing;

    // Whether this socket no longer holds the only connection while it is removed: it is
    // being closed, or it ended before a token check, which leaves no callbacks or
    // disconnect to finish.
    public bool HasGivenUpConnection => _handshake.State is WebUiHandshake.Expired or WebUiHandshake.Rejected or WebUiHandshake.Ended;

    // Whether TryYieldSilentHandshake would close this socket now. It changes nothing.
    public bool CanYieldSilentHandshake =>
        _handshake.State == WebUiHandshake.Pending
        && _handshakeTimeout > TimeSpan.Zero
        && _time.GetElapsedTime(_openedAt) >= TimeSpan.FromTicks(Math.Min(_handshakeTimeout.Ticks, MaximumYieldGrace.Ticks));

    // Closes this socket for a newer one if it is still silent after the grace
    // period (the shorter of two seconds and the handshake timeout).
    public bool TryYieldSilentHandshake() =>
        CanYieldSilentHandshake && TryExpireHandshake("a newer WebSocket needed the only connection");

    private async Task EnforceHandshakeDeadlineAsync(CancellationToken cancellationToken)
    {
        if (_handshakeTimeout <= TimeSpan.Zero || _handshakeTimeout == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        try
        {
            await Task.Delay(_handshakeTimeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        TryExpireHandshake(string.Create(
            CultureInfo.InvariantCulture,
            $"no token check within {_handshakeTimeout.TotalSeconds:0.###} seconds"));
    }

    // Callers may hold the connection gate, so this only changes the state. The
    // logging, revocation and close run on the thread pool.
    private bool TryExpireHandshake(string reason)
    {
        if (!_handshake.TryExpire())
        {
            return false;
        }

        _ = Task.Run(() => ExpireHandshakeAsync(reason));
        return true;
    }

    // Nothing awaits this task, so it observes its own failures: a logger that throws
    // must not keep the socket open.
    private async Task ExpireHandshakeAsync(string reason)
    {
        try
        {
            var frames = Volatile.Read(ref _framesReceived);
            RecordHandshake(string.Create(
                CultureInfo.InvariantCulture,
                $"closed by the server: {reason} ({frames} {(frames == 1 ? "message" : "messages")} received)"));
            RevokeAdmission();
            _window.ReportHandshakeExpired(ConnectionId, _time.GetElapsedTime(_openedAt), reason);
        }
        catch (Exception exception)
        {
            RevokeAdmission();
            Trace.TraceWarning($"Reporting the expired Bridge WebSocket {ConnectionId} failed: {exception}");
        }
        finally
        {
            await CloseHandshakeAsync("No WebUI token check arrived in time.").ConfigureAwait(false);
        }
    }

    private async Task CloseHandshakeAsync(string message)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, message, deadline.Token)
                .ConfigureAwait(false);
            if (deadline.IsCancellationRequested) _socket.Abort();
            // Let a cooperative peer acknowledge the close; never wait on it for long.
            _receiveStop.CancelAfter(TimeSpan.FromSeconds(2));
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RecordHandshake(string description) =>
        _window.HandshakeLog.Record($"WebSocket {ConnectionId}: {description}");

    private static string DescribeCommand(ReadOnlySpan<byte> message)
    {
        if (message.SequenceEqual("ping"u8)) return " (ping)";
        if (message.Length < WebUiProtocol.HeaderSize || message[0] != WebUiProtocol.Signature) return " (not a WebUI packet)";
        return string.Create(CultureInfo.InvariantCulture, $" (command 0x{message[7]:X2})");
    }

    public Task SendBindingAsync(string element, CancellationToken cancellationToken)
        => SendTextCommandAsync(0, WebUiProtocol.AddBinding, element, cancellationToken);

    public Task RunJavaScriptAsync(string script, CancellationToken cancellationToken)
        => SendTextCommandAsync(0, WebUiProtocol.JavaScriptQuick, script, cancellationToken);

    // A Views message (Views protocol 4) to WebUiBridge's reserved raw-data function: the message's UTF-8 JSON, which
    // has no raw NUL. The page's bridge hands its text to the Views listeners (runic-desktop-views.js), without
    // evaluating script. The packet is built before this returns. False when the session is not authenticated.
    public Task<bool> PushViewsMessageAsync(ReadOnlySpan<byte> message, CancellationToken cancellationToken)
    {
        if (!IsAuthenticated) return Task.FromResult(false);
        const string function = WebUiBridge.ViewsPushFunction;
        var messageOffset = WebUiProtocol.DataOffset + function.Length + 1;
        // The header, the function name and NUL, the message, and the terminating NUL of every WebUI packet.
        var packet = new byte[messageOffset + message.Length + 1];
        WebUiProtocol.WriteHeader(packet, 0, WebUiProtocol.SendRaw);
        Encoding.ASCII.GetBytes(function, packet.AsSpan(WebUiProtocol.DataOffset));
        message.CopyTo(packet.AsSpan(messageOffset));
        return SendViewsPacketAsync(packet, cancellationToken);
    }

    private async Task<bool> SendViewsPacketAsync(byte[] packet, CancellationToken cancellationToken)
    {
        await SendPacketAsync(packet, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // An announcement to WebUiBridge's reserved raw-data function: "polite" or "assertive", NUL and the message as
    // UTF-8. The page's bridge adds the message to its live region of that politeness, without evaluating script.
    public Task AnnounceAsync(string message, bool assertive, CancellationToken cancellationToken)
        => IsAuthenticated ? SendPacketAsync(AnnouncementPacket(message, assertive), cancellationToken) : Task.CompletedTask;

    // An empty message adds the page's live regions without saying anything.
    private static byte[] AnnouncementPacket(string message, bool assertive)
    {
        const string function = WebUiBridge.AnnounceFunction;
        var politeness = assertive ? "assertive"u8 : "polite"u8;
        var messageOffset = WebUiProtocol.DataOffset + function.Length + 1 + politeness.Length + 1;
        // The header, the function name, the politeness, each followed by NUL, the message and the terminating NUL of
        // every WebUI packet.
        var packet = new byte[messageOffset + Encoding.UTF8.GetByteCount(message) + 1];
        WebUiProtocol.WriteHeader(packet, 0, WebUiProtocol.SendRaw);
        Encoding.ASCII.GetBytes(function, packet.AsSpan(WebUiProtocol.DataOffset));
        politeness.CopyTo(packet.AsSpan(WebUiProtocol.DataOffset + function.Length + 1));
        Encoding.UTF8.GetBytes(message, packet.AsSpan(messageOffset));
        return packet;
    }

    public async Task<byte[]> ExecuteJavaScriptAsync(
        string script,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var completion = new TaskCompletionSource<ScriptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ushort id;
        lock (_admissionGate)
        {
            if (!IsAuthenticated)
                throw new InvalidOperationException("No authenticated WebUI browser is connected.");
            id = ReserveScriptId(completion);
        }
        try
        {
            await SendTextCommandAsync(id, WebUiProtocol.JavaScript, script, cancellationToken).ConfigureAwait(false);
            ScriptResult result;
            if (timeout is null || timeout == TimeSpan.Zero)
            {
                result = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await completion.Task.WaitAsync(timeout.Value, cancellationToken).ConfigureAwait(false);
            }

            if (result.IsError)
            {
                throw new InvalidOperationException($"JavaScript execution failed: {Encoding.UTF8.GetString(result.Data)}");
            }

            return result.Data;
        }
        finally
        {
            _pendingScripts.TryRemove(id, out _);
        }
    }

    public Task NavigateAsync(string url, CancellationToken cancellationToken)
        => SendTextCommandAsync(0, WebUiProtocol.Navigation, url, cancellationToken);

    public Task SendRawAsync(string function, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(function);
        if (data.IsEmpty)
        {
            return Task.CompletedTask;
        }

        var functionBytes = Encoding.UTF8.GetBytes(function);
        var payload = new byte[functionBytes.Length + 1 + data.Length];
        functionBytes.CopyTo(payload, 0);
        data.CopyTo(payload.AsMemory(functionBytes.Length + 1));
        return SendPacketAsync(WebUiProtocol.CreatePacket(0, WebUiProtocol.SendRaw, payload), cancellationToken);
    }

    public async Task CloseBridgeAsync(CancellationToken cancellationToken)
    {
        // Revocation is authoritative even when the peer ignores the legacy command,
        // the caller cancels, or a transport send stalls. Never await the event gate:
        // a capability callback is allowed to close its own session.
        RevokeAdmission();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await SendPacketAsync(WebUiProtocol.CreatePacket(0, WebUiProtocol.Close, []), deadline.Token)
                .ConfigureAwait(false);
            await CloseAsync(WebSocketCloseStatus.NormalClosure, "The presentation session was revoked.", deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            // CloseOutput does not wait for a peer acknowledgement. End a pending
            // receive too, so an uncooperative peer cannot retain the server session.
            if (deadline.IsCancellationRequested) _socket.Abort();
            await _receiveStop.CancelAsync().ConfigureAwait(false);
        }
    }

    // Not inline in DispatchCallIgnoringFailureAsync's finally: ILLink 10.0 rewrites that method (it folds
    // YieldAwaiter.IsCompleted) and drops a try/finally nested in a finally handler, here the lock's Monitor.Exit
    // (dotnet/runtime#136414), so a trimmed application kept the gate after its first call and hung on the next.
    private void ForgetCall(TaskCompletionSource completion)
    {
        lock (_admissionGate) _pendingCalls.Remove(completion);
    }

    private void RevokeAdmission()
    {
        lock (_admissionGate)
        {
            Volatile.Write(ref _revoked, 1);
            Volatile.Write(ref _authenticated, 0);
        }
        FailPendingScripts(new IOException("The WebUI browser connection was closed."));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        RevokeAdmission();
        await _receiveStop.CancelAsync().ConfigureAwait(false);
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await CloseAsync(WebSocketCloseStatus.NormalClosure, "The managed window is closing.", CancellationToken.None)
                .ConfigureAwait(false);
        }

        _socket.Dispose();
    }

    // Each message is its own array, which the dispatched call keeps: its arguments are slices of it. A message that
    // fits the receive buffer is copied once, at its size; a longer one is received straight into its growing array.
    private async Task<ReadOnlyMemory<byte>?> ReceiveMessageAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (!await AcceptFrameAsync(result.MessageType, 0, result.Count, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (result.EndOfMessage)
        {
            return buffer.AsSpan(0, result.Count).ToArray();
        }

        var message = new ArrayBufferWriter<byte>(2 * ReceiveBufferSize);
        message.Write(buffer.AsSpan(0, result.Count));
        do
        {
            result = await _socket.ReceiveAsync(message.GetMemory(ReceiveBufferSize), cancellationToken).ConfigureAwait(false);
            if (!await AcceptFrameAsync(result.MessageType, message.WrittenCount, result.Count, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            message.Advance(result.Count);
        }
        while (!result.EndOfMessage);

        return message.WrittenMemory;
    }

    private async ValueTask<bool> AcceptFrameAsync(WebSocketMessageType type, int received, int count, CancellationToken cancellationToken)
    {
        if (type == WebSocketMessageType.Close)
        {
            return false;
        }

        if (type != WebSocketMessageType.Binary)
        {
            await CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Only binary WebUI packets are supported.", cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        if (received + count > MaximumMessageSize)
        {
            await CloseAsync(WebSocketCloseStatus.MessageTooBig, "The WebUI packet is too large.", cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private async Task DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _revoked) != 0) return;
        if (payload.Span.SequenceEqual("ping"u8))
        {
            return;
        }

        if (payload.Length < WebUiProtocol.HeaderSize || payload.Span[0] != WebUiProtocol.Signature)
        {
            return;
        }

        var id = WebUiProtocol.ReadId(payload.Span);
        var command = payload.Span[7];
        var tokenIsValid = WebUiProtocol.ReadToken(payload.Span) == _window.Token;
        if (command == WebUiProtocol.CheckToken)
        {
            // A socket that is closing (expired or rejected) or has ended gets no reply.
            var before = _handshake.CheckToken(tokenIsValid);
            if (before is WebUiHandshake.Expired or WebUiHandshake.Rejected or WebUiHandshake.Ended) return;
            if (before == WebUiHandshake.Pending || !tokenIsValid)
            {
                RecordHandshake(tokenIsValid ? "token check, token matched" : "token check, token did not match");
            }
            await SendTokenResultAsync(id, tokenIsValid, cancellationToken).ConfigureAwait(false);
            if (!tokenIsValid)
            {
                // A rejected socket is never authenticated again. Revoke it and close it, so
                // the connection slot is released for the next socket.
                RevokeAdmission();
                RecordHandshake("closed by the server: the token did not match");
                // The close outlives this request, so it is not tied to its cancellation token.
                _ = Task.Run(() => CloseHandshakeAsync("The WebUI token did not match."), CancellationToken.None);
                return;
            }

            if (IsAuthenticated)
            {
                // Queued here on the receive path, as a call's admission is: a call that the page sent right behind its
                // token check (webui.js requestAfterToken) is admitted after the Connected event.
                Task admission;
                try { admission = _eventGate.WaitAsync(cancellationToken); }
                catch (Exception error) { admission = Task.FromException(error); }
                _ = DispatchConnectedIgnoringFailureAsync(admission, cancellationToken);
            }

            return;
        }

        if (!tokenIsValid || !IsAuthenticated)
        {
            return;
        }

        switch (command)
        {
            case WebUiProtocol.JavaScript:
                CompleteScript(id, payload.Span);
                break;
            case WebUiProtocol.Click:
                if (TryReadText(payload.Span[WebUiProtocol.DataOffset..], out var element))
                {
                    _ = DispatchEventIgnoringFailureAsync(WebUiEventType.MouseClick, element, [], cancellationToken);
                }
                break;
            case WebUiProtocol.Navigation:
                if (TryReadText(payload.Span[WebUiProtocol.DataOffset..], out var url))
                {
                    _ = DispatchEventIgnoringFailureAsync(
                        WebUiEventType.Navigation,
                        string.Empty,
                        [Encoding.UTF8.GetBytes(url)],
                        cancellationToken);
                }
                break;
            case WebUiProtocol.CallFunction:
                TaskCompletionSource completion;
                lock (_admissionGate)
                {
                    if (!IsAuthenticated) return;
                    completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingCalls.Add(completion);
                }
                // Calls are admitted in the order they arrived: the gate's waiters are queued here, on the receive
                // path, and SemaphoreSlim releases asynchronous waiters first in, first out. Waiting on a thread-pool
                // thread instead let two calls sent back to back race for the gate, so a second keystroke could
                // reach .NET before the first.
                Task admission;
                try { admission = _eventGate.WaitAsync(cancellationToken); }
                catch (Exception error) { admission = Task.FromException(error); }
                _ = DispatchCallIgnoringFailureAsync(id, payload, completion, admission, cancellationToken);
                break;
            case WebUiProtocol.WindowDrag:
                _ = _window.BeginEmbeddedWindowMoveAsync(cancellationToken);
                break;
        }
    }

    private async Task DispatchCallIgnoringFailureAsync(
        ushort id,
        ReadOnlyMemory<byte> payload,
        TaskCompletionSource completion,
        Task admission,
        CancellationToken cancellationToken)
    {
        // The handler's synchronous prefix runs on a thread-pool thread, never on the receive path.
        await Task.Yield();
        try
        {
            await admission.ConfigureAwait(false);
            ValueTask<WebUiResult> invocation;
            try
            {
                if (!IsAuthenticated) return;
                if (!TryDecodeCall(payload[WebUiProtocol.DataOffset..], out var element, out var arguments))
                {
                    await SendCallResultAsync(id, WebUiResult.None, cancellationToken).ConfigureAwait(false);
                    return;
                }

                // Serialize callback admission and its synchronous prefix, not
                // the entire async execution. A long command or operation wait
                // must leave later cancellation/control callbacks available.
                invocation = _window.InvokeCallbackAsync(this, element, arguments, cancellationToken);
            }
            finally
            {
                _eventGate.Release();
            }
            var result = await invocation.ConfigureAwait(false);
            await SendCallResultAsync(id, result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            try
            {
                await SendCallResultAsync(id, WebUiResult.None, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        finally
        {
            ForgetCall(completion);
            completion.TrySetResult();
        }
    }

    private async Task DispatchConnectedIgnoringFailureAsync(Task admission, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await admission.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }

        try
        {
            if (IsAuthenticated)
            {
                await _window.DispatchEventAsync(this, WebUiEventType.Connected, string.Empty, [], cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            _eventGate.Release();
        }
    }

    private async Task DispatchEventIgnoringFailureAsync(
        WebUiEventType eventType,
        string element,
        ReadOnlyMemory<byte>[] arguments,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await DispatchEventAsync(eventType, element, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
        }
    }

    private async Task DispatchEventAsync(
        WebUiEventType eventType,
        string element,
        ReadOnlyMemory<byte>[] arguments,
        CancellationToken cancellationToken)
    {
        await _eventGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (eventType != WebUiEventType.Disconnected && !IsAuthenticated) return;
            await _window.DispatchEventAsync(this, eventType, element, arguments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _eventGate.Release();
        }
    }

    private async Task SendTokenResultAsync(ushort id, bool accepted, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || _socket.State != WebSocketState.Open)
            {
                RecordHandshake($"token reply not sent: the socket is {(Volatile.Read(ref _disposed) != 0 ? "disposed" : _socket.State)}");
                return;
            }

            lock (_admissionGate)
            {
                if (Volatile.Read(ref _revoked) != 0)
                {
                    RecordHandshake("token reply not sent: the session was revoked");
                    return;
                }
                Volatile.Write(ref _authenticated, accepted ? 1 : 0);
                _everAuthenticated |= accepted;
            }
            var names = accepted ? _window.GetBindingNames() : [];
            var bindingBytes = names.Length > 0
                ? Encoding.UTF8.GetBytes(string.Join(',', names) + ',')
                : [];
            var payload = new byte[1 + bindingBytes.Length];
            payload[0] = accepted ? (byte)1 : (byte)0;
            bindingBytes.CopyTo(payload, 1);
            await SendPacketUnderGateAsync(WebUiProtocol.CreatePacket(id, WebUiProtocol.CheckToken, payload), cancellationToken)
                .ConfigureAwait(false);
            if (accepted)
            {
                _window.NotifyAuthenticated();
                // The page announces DesktopWindow.AnnounceAsync while it has the focus on WebView2 (I-184). Its live
                // regions are added now, so that the screen reader knows them before the first message.
                if (_window.AnnouncesInPage)
                {
                    await SendPacketUnderGateAsync(AnnouncementPacket(string.Empty, assertive: false), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private Task SendCallResultAsync(ushort id, WebUiResult result, CancellationToken cancellationToken)
    {
        // UTF-8 text, such as a Views reply, is copied once, into the packet.
        if (result.Kind == WebUiResultKind.Utf8)
        {
            return IsAuthenticated
                ? SendPacketAsync(WebUiProtocol.CreatePacket(id, WebUiProtocol.CallFunction, result.Utf8Value.Span), cancellationToken)
                : Task.CompletedTask;
        }

        var response = result.Kind switch
        {
            WebUiResultKind.None => string.Empty,
            WebUiResultKind.Int64 => result.Int64Value.ToString(CultureInfo.InvariantCulture),
            WebUiResultKind.Double => result.DoubleValue.ToString("R", CultureInfo.InvariantCulture),
            WebUiResultKind.Boolean => result.BooleanValue ? "1" : "0",
            WebUiResultKind.String => result.StringValue,
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };

        return SendTextCommandAsync(id, WebUiProtocol.CallFunction, response, cancellationToken);
    }

    private Task SendTextCommandAsync(ushort id, byte command, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!IsAuthenticated && command != WebUiProtocol.CheckToken)
        {
            return Task.CompletedTask;
        }

        return SendPacketAsync(WebUiProtocol.CreateTextPacket(id, command, text), cancellationToken);
    }

    private async Task SendPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendPacketUnderGateAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task SendPacketUnderGateAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        if (_socket.State == WebSocketState.Open)
        {
            await _socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        }
    }

    private void CompleteScript(ushort id, ReadOnlySpan<byte> packet)
    {
        if (packet.Length < WebUiProtocol.HeaderSize + 2 || !_pendingScripts.TryRemove(id, out var completion))
        {
            return;
        }

        var data = packet[(WebUiProtocol.DataOffset + 1)..];
        if (!data.IsEmpty && data[^1] == 0)
        {
            data = data[..^1];
        }

        completion.TrySetResult(new ScriptResult(packet[WebUiProtocol.DataOffset] != 0, data.ToArray()));
    }

    private ushort ReserveScriptId(TaskCompletionSource<ScriptResult> completion)
    {
        for (var attempt = 0; attempt <= ushort.MaxValue; attempt++)
        {
            var id = unchecked((ushort)(Interlocked.Increment(ref _nextScriptId) - 1));
            if (_pendingScripts.TryAdd(id, completion))
            {
                return id;
            }
        }

        throw new InvalidOperationException("All WebUI JavaScript correlation identifiers are in use.");
    }

    private void FailPendingScripts(Exception exception)
    {
        foreach (var pending in _pendingScripts.ToArray())
        {
            if (_pendingScripts.TryRemove(pending.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private static bool TryGetMultiPacketLength(ReadOnlySpan<byte> packet, out int length)
    {
        length = 0;
        if (packet.Length < WebUiProtocol.HeaderSize + 2 ||
            packet[0] != WebUiProtocol.Signature ||
            packet[7] != WebUiProtocol.Multi)
        {
            return false;
        }

        var value = packet[WebUiProtocol.DataOffset..];
        var terminator = value.IndexOf((byte)0);
        return terminator > 0 &&
            int.TryParse(Encoding.ASCII.GetString(value[..terminator]), NumberStyles.None, CultureInfo.InvariantCulture, out length) &&
            length is > 0 and <= MaximumMessageSize;
    }

    private static bool TryReadText(ReadOnlySpan<byte> data, out string text)
    {
        var terminator = data.IndexOf((byte)0);
        if (terminator < 0)
        {
            text = string.Empty;
            return false;
        }

        text = Encoding.UTF8.GetString(data[..terminator]);
        return true;
    }

    // The arguments are slices of the packet, which belongs to this call alone.
    private static bool TryDecodeCall(ReadOnlyMemory<byte> packet, out string element, out ReadOnlyMemory<byte>[] arguments)
    {
        element = string.Empty;
        arguments = [];
        var payload = packet.Span;
        var offset = 0;

        var elementEnd = payload.IndexOf((byte)0);
        if (elementEnd < 1)
        {
            return false;
        }

        element = Encoding.UTF8.GetString(payload[..elementEnd]);
        payload = payload[(elementEnd + 1)..];
        offset += elementEnd + 1;

        var lengthsEnd = payload.IndexOf((byte)0);
        if (lengthsEnd < 0)
        {
            return false;
        }

        var lengthsText = Encoding.ASCII.GetString(payload[..lengthsEnd]);
        payload = payload[(lengthsEnd + 1)..];
        offset += lengthsEnd + 1;
        if (lengthsText.Length == 0)
        {
            return payload.IsEmpty;
        }

        var lengthParts = lengthsText.Split(';');
        if (lengthParts.Length > MaximumArgumentCount)
        {
            return false;
        }

        arguments = new ReadOnlyMemory<byte>[lengthParts.Length];
        for (var index = 0; index < lengthParts.Length; index++)
        {
            if (!int.TryParse(lengthParts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                length < 0 || length >= payload.Length || payload[length] != 0)
            {
                arguments = [];
                return false;
            }

            arguments[index] = packet.Slice(offset, length);
            payload = payload[(length + 1)..];
            offset += length + 1;
        }

        return payload.IsEmpty;
    }

    private async Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
    {
        try
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await _socket.CloseOutputAsync(status, description, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _sendGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private sealed record ScriptResult(bool IsError, byte[] Data);
}
