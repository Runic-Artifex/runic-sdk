using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks.Sources;
using static Runic.Desktop.Internal.NativeServer.NativeServerInterop;

namespace Runic.Desktop.Internal.NativeServer;

// One request from the native server as a PresentationExchange. The response starts with the first body write, a file,
// or completion; a streamed body is written in order with at most one write in flight, whose completion the library
// reports when its queue was full (backpressure).
internal sealed class NativeExchange : PresentationExchange, IDisposable, IThreadPoolWorkItem, IValueTaskSource<bool>
{
    private readonly NativePresentationServer _server;
    private readonly Dictionary<string, string> _requestHeaders;
    private readonly List<KeyValuePair<string, string>> _responseHeaders = [];
    private readonly CancellationTokenSource _aborted = new();
    private readonly bool _isWebSocketRequest;
    private ManualResetValueTaskSourceCore<bool> _write = new() { RunContinuationsAsynchronously = true };
    private ResponseStream? _stream;
    private int _state; // 0 not started, 1 streaming, 2 complete (empty or file), 3 WebSocket
    private int _statusCode = PresentationStatus.Ok;

    private NativeExchange(NativePresentationServer server, ulong id, string method, string path, string query,
        Dictionary<string, string> headers, bool webSocket, IPAddress? remote, int remotePort, IPAddress? local,
        int localPort)
        : base(method, path, query)
    {
        _server = server;
        Id = id;
        _requestHeaders = headers;
        _isWebSocketRequest = webSocket;
        RemoteIpAddress = remote;
        RemotePort = remotePort;
        LocalIpAddress = local;
        LocalPort = localPort;
        RequestAborted = _aborted.Token;
    }

    internal ulong Id { get; }

    // Copies the request out of the library's memory; null when its target cannot be represented (400).
    internal static unsafe NativeExchange? Create(NativePresentationServer server, RunicRequest* request)
    {
        var target = Encoding.ASCII.GetString(request->Target.Span);
        if (!NativeRequestTarget.TryParse(target, out var path, out var query))
        {
            return null;
        }
        var headers = new Dictionary<string, string>((int)request->HeaderCount, StringComparer.OrdinalIgnoreCase);
        for (nuint index = 0; index < request->HeaderCount; index++)
        {
            var header = request->Headers[index];
            // Latin-1 keeps every byte; HTTP header values are ASCII in practice. A repeated header reads as its values
            // joined with commas, as ASP.NET Core presents it.
            var name = Encoding.ASCII.GetString(header.Name.Span);
            var value = Encoding.Latin1.GetString(header.Value.Span);
            headers[name] = headers.TryGetValue(name, out var previous) ? $"{previous},{value}" : value;
        }
        var remote = IPAddress.TryParse(Encoding.ASCII.GetString(request->RemoteAddress.Span), out var remoteAddress) ? remoteAddress : null;
        var local = IPAddress.TryParse(Encoding.ASCII.GetString(request->LocalAddress.Span), out var localAddress) ? localAddress : null;
        return new NativeExchange(server, request->Id, Encoding.ASCII.GetString(request->Method.Span), path, query,
            headers, (request->Flags & RequestWebSocket) != 0, remote, request->RemotePort, local, request->LocalPort);
    }

    void IThreadPoolWorkItem.Execute() => _ = RunAsync();

    private async Task RunAsync()
    {
        try
        {
            await _server.Dispatch(this).ConfigureAwait(false);
            if (_state != 3)
            {
                await CompleteCoreAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            if (_state == 0 && !_aborted.IsCancellationRequested)
            {
                _statusCode = PresentationStatus.InternalServerError;
                _responseHeaders.Clear();
                ContentLength = 0;
                TryRespond(BodyEmpty, null);
            }
            else if (_state == 1)
            {
                Abort();
            }
            if (exception is not (OperationCanceledException or IOException or WebSocketException))
            {
                System.Diagnostics.Debug.WriteLine($"Runic Desktop native server: request {Id} failed: {exception}");
            }
        }
        finally
        {
            _server.Forget(this);
            Dispose();
        }
    }

    public void Dispose() => _aborted.Dispose();

    // The library saw the requester leave. Cancellation callbacks run on the thread pool, never on the library's thread.
    internal void RequesterDisconnected() =>
        ThreadPool.UnsafeQueueUserWorkItem(static exchange => exchange.CancelAborted(), this, preferLocal: false);

    internal void AbortFromServer() => CancelAborted();

    private void CancelAborted()
    {
        try
        {
            _aborted.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal override IPAddress? RemoteIpAddress { get; }

    internal override int RemotePort { get; }

    internal override IPAddress? LocalIpAddress { get; }

    internal override int LocalPort { get; }

    internal override CancellationToken RequestAborted { get; set; }

    internal override bool IsWebSocketRequest => _isWebSocketRequest;

    internal override string GetRequestHeader(string name) =>
        _requestHeaders.TryGetValue(name, out var value) ? value : string.Empty;

    internal override IEnumerable<KeyValuePair<string, string>> RequestHeaders => _requestHeaders;

    internal void Abort()
    {
        CancelAborted();
        try
        {
            NativeServerInterop.Abort(_server.Handle, Id);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal override int StatusCode
    {
        get => _statusCode;
        set
        {
            ThrowIfStarted();
            _statusCode = value;
        }
    }

    internal override string? ContentType
    {
        get => GetResponseHeader("Content-Type") is [var value, ..] ? value : null;
        set => SetOrRemove("Content-Type", value);
    }

    internal override long? ContentLength
    {
        get => GetResponseHeader("Content-Length") is [var value, ..] &&
            AsciiNumbers.TryParse(value, out var length) ? length : null;
        set => SetOrRemove("Content-Length", value is { } length ? AsciiNumbers.Format(length) : null);
    }

    internal override void SetResponseHeader(string name, string value) => SetOrRemove(name, value);

    internal override void AppendResponseHeader(string name, string value)
    {
        ThrowIfStarted();
        _responseHeaders.Add(new(name, value));
    }

    internal override IReadOnlyList<string> GetResponseHeader(string name)
    {
        List<string>? values = null;
        foreach (var header in _responseHeaders)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                (values ??= []).Add(header.Value);
            }
        }
        return values ?? (IReadOnlyList<string>)[];
    }

    private void SetOrRemove(string name, string? value)
    {
        ThrowIfStarted();
        _responseHeaders.RemoveAll(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase));
        if (value is not null)
        {
            _responseHeaders.Add(new(name, value));
        }
    }

    private void ThrowIfStarted()
    {
        if (_state != 0) throw new InvalidOperationException("The response has already started.");
    }

    internal override Stream ResponseBody => _stream ??= new ResponseStream(this);

    internal override async Task SendFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_state == 0)
        {
            // The library streams the file and answers a byte range of a 200 response.
            _state = 2;
            ThrowOnFailure(TryRespond(BodyFile, System.IO.Path.GetFullPath(path)));
            return;
        }
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1, useAsync: true);
        var remaining = file.Length;
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (remaining > 0)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;
                await WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private ValueTask CompleteCoreAsync()
    {
        switch (_state)
        {
            case 0:
                _state = 2;
                TryRespond(BodyEmpty, null);
                break;
            case 1:
                _state = 2;
                try
                {
                    End(_server.Handle, Id);
                }
                catch (ObjectDisposedException)
                {
                }
                break;
        }
        return ValueTask.CompletedTask;
    }

    private void StartStream()
    {
        if (_state != 0) return;
        _state = 1;
        ThrowOnFailure(TryRespond(BodyStream, null));
    }

    private int TryRespond(uint body, string? filePath)
    {
        try
        {
            return _server.Respond(Id, _statusCode, _responseHeaders, body, filePath);
        }
        catch (ObjectDisposedException)
        {
            return Closed;
        }
    }

    private void ThrowOnFailure(int result)
    {
        if (result is Ok or Pending) return;
        CancelAborted();
        throw result is Closed or UnknownId
            ? new IOException("The requester disconnected.")
            : new InvalidOperationException($"The native server refused the response: {LastErrorMessage()}");
    }

    internal async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_state == 0) StartStream();
        if (_state != 1) throw new InvalidOperationException("The response body is complete.");
        if (data.IsEmpty) return;
        _write.Reset();
        var result = WriteNative(data.Span);
        if (result == Pending)
        {
            if (!await new ValueTask<bool>(this, _write.Version).ConfigureAwait(false))
            {
                result = Closed;
            }
            else
            {
                return;
            }
        }
        ThrowOnFailure(result);
    }

    private unsafe int WriteNative(ReadOnlySpan<byte> data)
    {
        try
        {
            fixed (byte* pointer = data)
            {
                return Write(_server.Handle, Id, pointer, (nuint)data.Length, NativePresentationServer.ResponseToken(Id));
            }
        }
        catch (ObjectDisposedException)
        {
            return Closed;
        }
    }

    internal void WriteCompleted(bool accepted) => _write.SetResult(accepted);

    bool IValueTaskSource<bool>.GetResult(short token) => _write.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _write.GetStatus(token);
    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _write.OnCompleted(continuation, state, token, flags);

    internal override Task<WebSocket> AcceptWebSocketAsync(string? protocol)
    {
        if (!_isWebSocketRequest || _state != 0)
        {
            throw new InvalidOperationException("Not a WebSocket request, or the response has started.");
        }
        var socket = new NativeWebSocket(_server, Id, protocol);
        _server.Register(socket);
        int result;
        try
        {
            result = _server.AcceptWebSocket(Id, protocol, _responseHeaders);
        }
        catch (ObjectDisposedException)
        {
            result = Closed;
        }
        if (result != Ok)
        {
            _server.Forget(socket);
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely,
                $"The WebSocket could not be accepted: {LastErrorMessage()}");
        }
        _state = 3;
        return Task.FromResult<WebSocket>(socket);
    }

    private sealed class ResponseStream(NativeExchange exchange) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            exchange.StartStream();
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            exchange.WriteAsync(buffer, cancellationToken);
    }
}
