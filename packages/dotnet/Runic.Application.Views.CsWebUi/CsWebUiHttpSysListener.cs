using System.Net;
using System.Runtime.Versioning;

namespace Runic.Application.Views.CsWebUi;

// The CS-WebUI blob endpoint's listener on Windows: an HttpListener, which is http.sys there. http.sys lets a user
// without administrator rights listen on localhost only, and listens on every address of it. Only Windows uses it: the
// managed HttpListener of other systems brings the TLS stack into every NativeAOT application (LoopbackHttpServer).
[SupportedOSPlatform("windows")]
internal sealed class CsWebUiHttpSysListener : ICsWebUiBlobListener
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stop = new();

    private CsWebUiHttpSysListener(HttpListener listener) => _listener = listener;

    // Starts listening on prefix. Throws HttpListenerException when http.sys refuses it.
    internal static CsWebUiHttpSysListener Start(string prefix)
    {
        var listener = new HttpListener { IgnoreWriteExceptions = true };
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch
        {
            listener.Close();
            throw;
        }
        return new CsWebUiHttpSysListener(listener);
    }

    // The error of a listener that could not start.
    internal static bool IsListenerError(Exception error) => error is HttpListenerException;

    public void Serve(Func<ICsWebUiBlobRequest, Task> handle) => _ = Task.Run(() => AcceptAsync(handle));

    private async Task AcceptAsync(Func<ICsWebUiBlobRequest, Task> handle)
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(context, handle));
        }
    }

    // Every request ends with its response closed, or aborted when it failed midway. Its token cancels when the listener
    // stops and when a read or write of the request fails, as one does when the client disconnects.
    private async Task HandleAsync(HttpListenerContext context, Func<ICsWebUiBlobRequest, Task> handle)
    {
        var response = context.Response;
        CancellationTokenSource aborted;
        try { aborted = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); }
        catch (ObjectDisposedException)
        {
            response.Abort();
            return;
        }
        using (aborted)
        {
            try { await handle(new Exchange(context, aborted)).ConfigureAwait(false); }
            catch (Exception error) when (error is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                try { response.Abort(); } catch (ObjectDisposedException) { }
                return;
            }
            try { response.Close(); }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        try { _listener.Close(); }
        catch (ObjectDisposedException) { }
        _stop.Dispose();
    }

    // An HttpListener request as a Bridge blob exchange. A response without a length is sent chunked. HttpListener has
    // no token of the client's disconnect; a failed read or write of the request stands for it and cancels
    // RequestAborted.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001",
        Justification = "The wrapped streams belong to the HttpListener request and response, which HandleAsync closes.")]
    private sealed class Exchange(HttpListenerContext context, CancellationTokenSource aborted) : ICsWebUiBlobRequest
    {
        private bool _lengthSet;
        private Stream? _requestBody;
        private Stream? _responseBody;

        public string Path => context.Request.Url?.AbsolutePath ?? "/";
        public IPEndPoint? RemoteEndPoint => context.Request.RemoteEndPoint;
        public string Method => context.Request.HttpMethod;
        public string GetRequestHeader(string name) => context.Request.Headers[name] ?? string.Empty;
        public Stream RequestBody => _requestBody ??= new AbortingStream(context.Request.InputStream, aborted);

        public int StatusCode
        {
            get => context.Response.StatusCode;
            set => context.Response.StatusCode = value;
        }

        public string? ContentType { set => context.Response.ContentType = value; }

        public long? ContentLength
        {
            set
            {
                if (value is not { } length) return;
                context.Response.ContentLength64 = length;
                _lengthSet = true;
            }
        }

        public void SetResponseHeader(string name, string value) => context.Response.Headers[name] = value;
        public void AppendResponseHeader(string name, string value) => context.Response.Headers.Add(name, value);

        public Stream ResponseBody
        {
            get
            {
                if (_responseBody is not null) return _responseBody;
                if (!_lengthSet) context.Response.SendChunked = true;
                return _responseBody = new AbortingStream(context.Response.OutputStream, aborted);
            }
        }

        public CancellationToken RequestAborted => aborted.Token;
    }

    // A request or response stream whose failure cancels the request's token.
    private sealed class AbortingStream(Stream inner, CancellationTokenSource aborted) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { Abort(); throw; }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { Abort(); throw; }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void Abort()
        {
            try { aborted.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}
