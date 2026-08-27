using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;

namespace CsWebUi.Managed.Internal;

internal sealed class WebUiSession : IAsyncDisposable
{
    private const int ReceiveBufferSize = 16 * 1024;
    private const int MaximumMessageSize = 64_000_000;
    private const int MaximumArgumentCount = 17;

    private readonly WebUiWindow _window;
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _eventGate = new(1, 1);
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<ScriptResult>> _pendingScripts = new();
    private int _authenticated;
    private int _disposed;
    private int _nextScriptId;

    public WebUiSession(WebUiWindow window, WebSocket socket, nuint connectionId, string cookies)
    {
        _window = window;
        _socket = socket;
        ConnectionId = connectionId;
        Cookies = cookies;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public nuint ConnectionId { get; }

    public nuint ClientId => ConnectionId;

    public string Cookies { get; }

    public bool IsAuthenticated => Volatile.Read(ref _authenticated) != 0;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte>? multiPacket = null;
        var multiExpected = 0;
        var receiveBuffer = new byte[ReceiveBufferSize];
        try
        {
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var messageResult = await ReceiveMessageAsync(receiveBuffer, cancellationToken).ConfigureAwait(false);
                if (!messageResult.HasValue)
                {
                    return;
                }
                var message = messageResult.Value;

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

                if (TryGetMultiPacketLength(message.Span, out multiExpected))
                {
                    multiPacket = new ArrayBufferWriter<byte>(multiExpected);
                    continue;
                }

                await DispatchAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            var wasAuthenticated = Interlocked.Exchange(ref _authenticated, 0) != 0;
            FailPendingScripts(new IOException("The WebUI browser connection was closed."));
            if (wasAuthenticated)
            {
                await DispatchEventAsync(WebUiEventType.Disconnected, string.Empty, [], CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    public Task SendBindingAsync(string element, CancellationToken cancellationToken)
        => SendTextCommandAsync(0, WebUiProtocol.AddBinding, element, cancellationToken);

    public Task RunJavaScriptAsync(string script, CancellationToken cancellationToken)
        => SendTextCommandAsync(0, WebUiProtocol.JavaScriptQuick, script, cancellationToken);

    public async Task<byte[]> ExecuteJavaScriptAsync(
        string script,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsAuthenticated)
        {
            throw new InvalidOperationException("No authenticated WebUI browser is connected.");
        }

        var completion = new TaskCompletionSource<ScriptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = ReserveScriptId(completion);
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

    public Task CloseBridgeAsync(CancellationToken cancellationToken)
        => SendPacketAsync(WebUiProtocol.CreatePacket(0, WebUiProtocol.Close, []), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        FailPendingScripts(new ObjectDisposedException(nameof(WebUiSession)));
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await CloseAsync(WebSocketCloseStatus.NormalClosure, "The managed window is closing.", CancellationToken.None)
                .ConfigureAwait(false);
        }

        _socket.Dispose();
    }

    private async Task<ReadOnlyMemory<byte>?> ReceiveMessageAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var message = new ArrayBufferWriter<byte>();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                await CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Only binary WebUI packets are supported.", cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            if (message.WrittenCount + result.Count > MaximumMessageSize)
            {
                await CloseAsync(WebSocketCloseStatus.MessageTooBig, "The WebUI packet is too large.", cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            message.Write(buffer.AsSpan(0, result.Count));
        }
        while (!result.EndOfMessage);

        return message.WrittenMemory;
    }

    private async Task DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
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
            await SendTokenResultAsync(id, tokenIsValid, cancellationToken).ConfigureAwait(false);
            if (tokenIsValid)
            {
                _ = DispatchEventIgnoringFailureAsync(WebUiEventType.Connected, string.Empty, [], cancellationToken);
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
                _ = DispatchCallIgnoringFailureAsync(id, payload, cancellationToken);
                break;
            case WebUiProtocol.WindowDrag:
                break;
        }
    }

    private async Task DispatchCallIgnoringFailureAsync(
        ushort id,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await _eventGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!TryDecodeCall(payload.Span[WebUiProtocol.DataOffset..], out var element, out var arguments))
                {
                    await SendCallResultAsync(id, WebUiResult.None, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var result = await _window.InvokeCallbackAsync(this, element, arguments, cancellationToken).ConfigureAwait(false);
                await SendCallResultAsync(id, result, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _eventGate.Release();
            }
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
    }

    private async Task DispatchEventIgnoringFailureAsync(
        WebUiEventType eventType,
        string element,
        byte[][] arguments,
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
        byte[][] arguments,
        CancellationToken cancellationToken)
    {
        await _eventGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
                return;
            }

            Volatile.Write(ref _authenticated, accepted ? 1 : 0);
            var names = accepted ? _window.GetBindingNames() : [];
            var bindingBytes = names.Length > 0
                ? Encoding.UTF8.GetBytes(string.Join(',', names) + ',')
                : [];
            var payload = new byte[1 + bindingBytes.Length];
            payload[0] = accepted ? (byte)1 : (byte)0;
            bindingBytes.CopyTo(payload, 1);
            await SendPacketUnderGateAsync(WebUiProtocol.CreatePacket(id, WebUiProtocol.CheckToken, payload), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private Task SendCallResultAsync(ushort id, WebUiResult result, CancellationToken cancellationToken)
    {
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

        return SendPacketAsync(
            WebUiProtocol.CreatePacket(id, command, Encoding.UTF8.GetBytes(text)),
            cancellationToken);
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

    private static bool TryDecodeCall(ReadOnlySpan<byte> payload, out string element, out byte[][] arguments)
    {
        element = string.Empty;
        arguments = [];

        var elementEnd = payload.IndexOf((byte)0);
        if (elementEnd < 1)
        {
            return false;
        }

        element = Encoding.UTF8.GetString(payload[..elementEnd]);
        payload = payload[(elementEnd + 1)..];

        var lengthsEnd = payload.IndexOf((byte)0);
        if (lengthsEnd < 0)
        {
            return false;
        }

        var lengthsText = Encoding.ASCII.GetString(payload[..lengthsEnd]);
        payload = payload[(lengthsEnd + 1)..];
        if (lengthsText.Length == 0)
        {
            return payload.IsEmpty;
        }

        var lengthParts = lengthsText.Split(';');
        if (lengthParts.Length > MaximumArgumentCount)
        {
            return false;
        }

        arguments = new byte[lengthParts.Length][];
        for (var index = 0; index < lengthParts.Length; index++)
        {
            if (!int.TryParse(lengthParts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                length < 0 || payload.Length < length + 1 || payload[length] != 0)
            {
                arguments = [];
                return false;
            }

            arguments[index] = payload[..length].ToArray();
            payload = payload[(length + 1)..];
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
