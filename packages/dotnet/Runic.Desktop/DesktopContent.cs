using System.Text;
using Runic.Desktop.Internal;

namespace Runic.Desktop;

/// <summary>Resolves one surface-relative request, or returns <see langword="null"/> to use local-content fallback.</summary>
public delegate ValueTask<ContentResponse?> ContentHandler(
    ContentRequest request,
    CancellationToken cancellationToken);

/// <summary>Opens a request-owned response stream.</summary>
public delegate ValueTask<Stream> ContentStreamFactory(CancellationToken cancellationToken);

/// <summary>Describes one request presented to a surface content handler.</summary>
public sealed class ContentRequest
{
    internal ContentRequest(
        string path,
        string method,
        IReadOnlyDictionary<string, string> headers,
        IServiceProvider services,
        PresentationRequestCancellation? cancellation)
    {
        Path = path;
        Method = method;
        Headers = headers;
        Services = services;
        _cancellation = cancellation;
    }

    private readonly PresentationRequestCancellation? _cancellation;

    /// <summary>Gets the decoded absolute path within the surface.</summary>
    public string Path { get; }

    /// <summary>Gets the HTTP request method.</summary>
    public string Method { get; }

    /// <summary>Gets the immutable request-header snapshot using case-insensitive names.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Gets the request-scoped service provider.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Gets the cancellation cause observed by this request.</summary>
    public RequestCancellationReason CancellationReason =>
        _cancellation?.Reason ?? RequestCancellationReason.None;
}

/// <summary>Identifies the first terminal cancellation cause observed by a content request.</summary>
public enum RequestCancellationReason
{
    None,
    CallerCancelled,
    RequesterDisconnected,
    DeadlineExceeded,
    SessionClosed,
    SurfaceClosing,
    HostStopping,
}

/// <summary>Represents an empty, fixed, or streaming content response.</summary>
public sealed class ContentResponse
{
    private readonly ContentStreamFactory? _streamFactory;

    /// <summary>Creates a fixed response.</summary>
    public ContentResponse(
        ReadOnlyMemory<byte> body,
        string? contentType = null,
        int statusCode = 200,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        ValidateStatusCode(statusCode);
        Body = body;
        ContentLength = body.Length;
        ContentType = contentType;
        StatusCode = statusCode;
        Headers = headers;
    }

    private ContentResponse(
        ContentStreamFactory streamFactory,
        string? contentType,
        int statusCode,
        long? contentLength,
        IReadOnlyDictionary<string, string>? headers)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        ValidateStatusCode(statusCode);
        if (contentLength is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentLength));
        }

        _streamFactory = streamFactory;
        ContentLength = contentLength;
        ContentType = contentType;
        StatusCode = statusCode;
        Headers = headers;
    }

    /// <summary>Gets the fixed body, or empty memory for a streaming response.</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>Gets whether this response opens a request-scoped stream.</summary>
    public bool IsStreaming => _streamFactory is not null;

    /// <summary>Gets the body length when known before streaming.</summary>
    public long? ContentLength { get; }

    /// <summary>Gets the response content type.</summary>
    public string? ContentType { get; }

    /// <summary>Gets the HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>Gets additional response headers.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; }

    /// <summary>Creates a UTF-8 text response.</summary>
    public static ContentResponse Text(
        string text,
        string contentType = "text/html; charset=utf-8",
        int statusCode = 200)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ContentResponse(Encoding.UTF8.GetBytes(text), contentType, statusCode);
    }

    /// <summary>Creates a response whose stream is opened once per <c>GET</c> request.</summary>
    public static ContentResponse Stream(
        ContentStreamFactory streamFactory,
        string? contentType = null,
        int statusCode = 200,
        long? contentLength = null,
        IReadOnlyDictionary<string, string>? headers = null)
        => new(streamFactory, contentType, statusCode, contentLength, headers);

    internal WebUiContent ToCompatibilityContent() => _streamFactory is null
        ? new WebUiContent(Body, ContentType, StatusCode, Headers)
        : WebUiContent.FromStream(
            cancellationToken => _streamFactory(cancellationToken),
            ContentType,
            StatusCode,
            ContentLength,
            Headers);

    private static void ValidateStatusCode(int statusCode)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(statusCode, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(statusCode, 599);
    }
}
