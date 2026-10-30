using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks.Sources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using static Runic.Desktop.Internal.NativeServer.NativeServerInterop;

namespace Runic.Desktop.Internal.NativeServer;

// One request from the native server, presented to the host core as the features of a DefaultHttpContext. The
// response starts with the first body write, a file, or completion; a streamed body is written in order with at most
// one write in flight, whose completion the library reports when its queue was full (backpressure).
internal sealed class NativeExchange :
    IDisposable,
    IThreadPoolWorkItem,
    IHttpRequestFeature,
    IHttpResponseFeature,
    IHttpResponseBodyFeature,
    IHttpWebSocketFeature,
    IHttpConnectionFeature,
    IHttpRequestLifetimeFeature,
    IHttpRequestIdentifierFeature,
    IValueTaskSource<bool>
{
    private readonly NativePresentationServer _server;
    private readonly FeatureCollection _features = new(9);
    private readonly HeaderDictionary _requestHeaders;
    private readonly HeaderDictionary _responseHeaders = new();
    private readonly CancellationTokenSource _aborted = new();
    private readonly bool _isWebSocketRequest;
    private ManualResetValueTaskSourceCore<bool> _write = new() { RunContinuationsAsynchronously = true };
    private List<(Func<object, Task> Callback, object State)>? _onStarting;
    private List<(Func<object, Task> Callback, object State)>? _onCompleted;
    private ResponseStream? _stream;
    private PipeWriter? _writer;
    private int _state; // 0 not started, 1 streaming, 2 complete (empty or file), 3 WebSocket
    private int _statusCode = StatusCodes.Status200OK;

    private NativeExchange(NativePresentationServer server, ulong id, string method, string path, string query,
        string rawTarget, HeaderDictionary headers, bool webSocket, IPAddress? remote, int remotePort, IPAddress? local,
        int localPort)
    {
        _server = server;
        Id = id;
        Method = method;
        Path = path;
        QueryString = query;
        RawTarget = rawTarget;
        _requestHeaders = headers;
        _isWebSocketRequest = webSocket;
        RemoteIpAddress = remote;
        RemotePort = remotePort;
        LocalIpAddress = local;
        LocalPort = localPort;
        ConnectionId = TraceIdentifier = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RequestAborted = _aborted.Token;
        _features.Set<IHttpRequestFeature>(this);
        _features.Set<IHttpResponseFeature>(this);
        _features.Set<IHttpResponseBodyFeature>(this);
        _features.Set<IHttpConnectionFeature>(this);
        _features.Set<IHttpRequestLifetimeFeature>(this);
        _features.Set<IHttpRequestIdentifierFeature>(this);
        if (webSocket)
        {
            _features.Set<IHttpWebSocketFeature>(this);
        }
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
        var headers = new HeaderDictionary((int)request->HeaderCount);
        for (nuint index = 0; index < request->HeaderCount; index++)
        {
            var header = request->Headers[index];
            // Latin-1 keeps every byte; HTTP header values are ASCII in practice.
            headers.Append(Encoding.ASCII.GetString(header.Name.Span), Encoding.Latin1.GetString(header.Value.Span));
        }
        var remote = IPAddress.TryParse(Encoding.ASCII.GetString(request->RemoteAddress.Span), out var remoteAddress) ? remoteAddress : null;
        var local = IPAddress.TryParse(Encoding.ASCII.GetString(request->LocalAddress.Span), out var localAddress) ? localAddress : null;
        return new NativeExchange(server, request->Id, Encoding.ASCII.GetString(request->Method.Span), path, query, target,
            headers, (request->Flags & RequestWebSocket) != 0, remote, request->RemotePort, local, request->LocalPort);
    }

    void IThreadPoolWorkItem.Execute() => _ = RunAsync();

    private async Task RunAsync()
    {
        var context = new DefaultHttpContext(_features);
        try
        {
            await _server.Dispatch(context).ConfigureAwait(false);
            if (_state != 3)
            {
                await CompleteCoreAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            if (_state == 0 && !_aborted.IsCancellationRequested)
            {
                _statusCode = StatusCodes.Status500InternalServerError;
                _responseHeaders.Clear();
                _responseHeaders.ContentLength = 0;
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
            if (_onCompleted is { } completed)
            {
                for (var index = completed.Count - 1; index >= 0; index--)
                {
                    try { await completed[index].Callback(completed[index].State).ConfigureAwait(false); }
                    catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Runic Desktop native server: an OnCompleted callback failed: {exception}"); }
                }
            }
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

    // IHttpRequestFeature

    public string Protocol { get; set; } = "HTTP/1.1";
    public string Scheme { get; set; } = "http";
    public string Method { get; set; }
    public string PathBase { get; set; } = string.Empty;
    public string Path { get; set; }
    public string QueryString { get; set; }
    public string RawTarget { get; set; }
    IHeaderDictionary IHttpRequestFeature.Headers { get => _requestHeaders; set => throw new NotSupportedException(); }
    // Runic Desktop answers GET and HEAD only; the library does not forward request bodies.
    Stream IHttpRequestFeature.Body { get; set; } = Stream.Null;

    // IHttpConnectionFeature

    public string ConnectionId { get; set; }
    public IPAddress? RemoteIpAddress { get; set; }
    public IPAddress? LocalIpAddress { get; set; }
    public int RemotePort { get; set; }
    public int LocalPort { get; set; }

    // IHttpRequestLifetimeFeature and IHttpRequestIdentifierFeature

    public CancellationToken RequestAborted { get; set; }
    public string TraceIdentifier { get; set; }

    public void Abort()
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

    // IHttpResponseFeature

    public int StatusCode
    {
        get => _statusCode;
        set
        {
            ThrowIfStarted();
            _statusCode = value;
        }
    }

    public string? ReasonPhrase { get; set; }
    IHeaderDictionary IHttpResponseFeature.Headers { get => _responseHeaders; set => throw new NotSupportedException(); }
    Stream IHttpResponseFeature.Body { get => Stream; set => throw new NotSupportedException(); }
    public bool HasStarted => _state != 0;

    public void OnStarting(Func<object, Task> callback, object state)
    {
        ThrowIfStarted();
        (_onStarting ??= []).Add((callback, state));
    }

    public void OnCompleted(Func<object, Task> callback, object state) => (_onCompleted ??= []).Add((callback, state));

    private void ThrowIfStarted()
    {
        if (_state != 0) throw new InvalidOperationException("The response has already started.");
    }

    // IHttpResponseBodyFeature

    public Stream Stream => _stream ??= new ResponseStream(this);

    public PipeWriter Writer => _writer ??= PipeWriter.Create(Stream, new StreamPipeWriterOptions(leaveOpen: true));

    public void DisableBuffering()
    {
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => StartStreamAsync().AsTask();

    public async Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_state == 0 && offset == 0 && count is null)
        {
            await RunOnStartingAsync().ConfigureAwait(false);
            // The library streams the file and answers a byte range of a 200 response.
            _state = 2;
            ThrowOnFailure(TryRespond(BodyFile, System.IO.Path.GetFullPath(path)));
            return;
        }
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1, useAsync: true);
        file.Seek(offset, SeekOrigin.Begin);
        var remaining = count ?? file.Length - offset;
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

    public Task CompleteAsync() => CompleteCoreAsync().AsTask();

    private async ValueTask CompleteCoreAsync()
    {
        if (_writer is not null)
        {
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        switch (_state)
        {
            case 0:
                await RunOnStartingAsync().ConfigureAwait(false);
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
    }

    private async ValueTask StartStreamAsync()
    {
        if (_state != 0) return;
        await RunOnStartingAsync().ConfigureAwait(false);
        _state = 1;
        ThrowOnFailure(TryRespond(BodyStream, null));
    }

    private async ValueTask RunOnStartingAsync()
    {
        if (_onStarting is not { } starting) return;
        _onStarting = null;
        for (var index = starting.Count - 1; index >= 0; index--)
        {
            await starting[index].Callback(starting[index].State).ConfigureAwait(false);
        }
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
        if (_state == 0) await StartStreamAsync().ConfigureAwait(false);
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

    // IHttpWebSocketFeature

    public bool IsWebSocketRequest => _isWebSocketRequest;

    public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
    {
        if (!_isWebSocketRequest || _state != 0)
        {
            throw new InvalidOperationException("Not a WebSocket request, or the response has started.");
        }
        var protocol = context.SubProtocol;
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

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            exchange._state == 0 ? exchange.StartStreamAsync().AsTask() : Task.CompletedTask;

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
