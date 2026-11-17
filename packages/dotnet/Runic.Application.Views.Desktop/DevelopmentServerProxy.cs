using System.Net;
using System.Net.Http.Headers;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// The HTTP side of <see cref="DevelopmentServerForwarding"/> on Runic Desktop: sends a checked target to the development
/// server and passes a successful answer back. Only Runic Desktop proxies (CS-WebUI redirects), and only while
/// <see cref="DevelopmentServerForwarding.IsSupported"/>, so a trimmed or NativeAOT application links no HTTP client.
/// </summary>
internal static class DevelopmentServerProxy
{
    internal const long MaximumBodyBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan ResponseHeadersTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResponseBodyTimeout = TimeSpan.FromMinutes(2);

    // One client per process: the development server is one loopback origin, so its connections are pooled.
    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        UseCookies = false,
        UseProxy = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    });

    // The headers passed back from the development server; everything else (Set-Cookie, CORS, its own policies) stays.
    private static readonly string[] PassedResponseHeaders = ["Cache-Control", "ETag", "Last-Modified"];

    /// <summary>
    /// Sends <paramref name="target"/> to the development server, or returns null when it cannot be reached or does
    /// not answer with a success. The caller disposes the result.
    /// </summary>
    internal static async Task<Forwarded?> SendAsync(Uri target, bool head, string? accept, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, target);
        if (!string.IsNullOrEmpty(accept)) request.Headers.TryAddWithoutValidation("Accept", accept);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResponseHeadersTimeout);
        HttpResponseMessage response;
        try
        {
            response = await SharedClient.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        var status = (int)response.StatusCode;
        if (status is < 200 or > 299 or 204 or 205 or 206)
        {
            response.Dispose();
            return null;
        }
        if (response.Content.Headers.ContentLength is > MaximumBodyBytes)
        {
            response.Dispose();
            return null;
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Content-Type-Options"] = "nosniff",
        };
        foreach (var name in PassedResponseHeaders)
        {
            if ((Value(response.Headers, name) ?? Value(response.Content.Headers, name)) is { } value) headers[name] = value;
        }
        return new Forwarded(status, response.Content.Headers.ContentType?.ToString(),
            response.Content.Headers.ContentLength, headers, response);
    }

    private static string? Value(HttpHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    /// <summary>A successful answer of the development server; disposing it releases the connection.</summary>
    internal sealed class Forwarded(int statusCode, string? contentType, long? contentLength,
        IReadOnlyDictionary<string, string> headers, HttpResponseMessage response) : IDisposable
    {
        internal int StatusCode { get; } = statusCode;
        internal string? ContentType { get; } = contentType;
        internal long? ContentLength { get; } = contentLength;
        internal IReadOnlyDictionary<string, string> Headers { get; } = headers;

        /// <summary>Opens the body; disposing the stream releases the response.</summary>
        internal async Task<Stream> OpenAsync(CancellationToken cancellationToken)
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new OwningStream(stream, response, MaximumBodyBytes, ResponseBodyTimeout);
        }

        public void Dispose() => response.Dispose();
    }

    // The body of an answer: at most maximumBytes, read within timeout; disposing it releases the response.
    private sealed class OwningStream(Stream inner, IDisposable owner, long maximumBytes, TimeSpan timeout) : Stream
    {
        private readonly CancellationTokenSource _deadline = new(timeout);
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_deadline.IsCancellationRequested) throw new IOException("The development server's answer took too long.");
            return Count(inner.Read(buffer));
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
            try
            {
                return Count(await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (_deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new IOException("The development server's answer took too long.");
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(int read)
        {
            _read += read;
            if (_read > maximumBytes) throw new IOException("The development server's answer is too large.");
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _deadline.Dispose();
                inner.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
