using System.Text;
using Runic.Desktop.Internal;

namespace Runic.Desktop;

/// <summary>Resolves one surface-relative request, or returns <see langword="null"/> to respond 404 Not Found.</summary>
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
    /// <summary>The request has not been cancelled.</summary>
    None,

    /// <summary>The caller that owns the request cancelled it.</summary>
    CallerCancelled,

    /// <summary>The requesting client disconnected or aborted the request.</summary>
    RequesterDisconnected,

    /// <summary>The request exceeded its deadline.</summary>
    DeadlineExceeded,

    /// <summary>The presentation session that issued the request closed.</summary>
    SessionClosed,

    /// <summary>The surface serving the request is closing.</summary>
    SurfaceClosing,

    /// <summary>The Desktop host is stopping.</summary>
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

/// <summary>The content a Desktop surface serves. Each case states its source, so nothing is inferred from a string.</summary>
/// <remarks>
/// <see cref="DesktopSurfaceOptions.Content"/> takes one case. To serve a packed asset archive, wrap its handler:
/// <c>new DesktopContent.Handler(assets.ToDesktopContentHandler())</c> from <c>Runic.Assets.Desktop</c>.
/// </remarks>
public abstract record DesktopContent
{
    private DesktopContent()
    {
    }

    /// <summary>Serves the files in a local directory.</summary>
    /// <param name="Root">The directory, absolute or relative to the current directory. It must exist when the surface is created.</param>
    /// <param name="Entry">
    /// The file the presentation opens, relative to <paramref name="Root"/>, such as <c>index.html</c>. When omitted,
    /// a request for a directory opens its <c>index.html</c>, <c>index.htm</c>, <c>index.ts</c> or <c>index.js</c>.
    /// </param>
    public sealed record Directory(string Root, string? Entry = null) : DesktopContent
    {
        /// <summary>Gets the directory, absolute or relative to the current directory.</summary>
        public string Root { get; init => field = RequireRoot(value, nameof(Root)); } = RequireRoot(Root, nameof(Root));

        /// <summary>Gets the file the presentation opens, relative to <see cref="Root"/>, or <see langword="null"/> for index discovery.</summary>
        public string? Entry { get; init => field = RequireRelativeEntry(value, nameof(Entry)); } = RequireRelativeEntry(Entry, nameof(Entry));
    }

    /// <summary>Serves one HTML document at the surface root. No local files are served.</summary>
    /// <param name="Document">The HTML. Include <c>&lt;script src="webui.js"&gt;&lt;/script&gt;</c> to connect the Bridge.</param>
    public sealed record Html(string Document) : DesktopContent
    {
        /// <summary>Gets the HTML document.</summary>
        public string Document { get; init => field = RequireNotNull(value, nameof(Document)); } = RequireNotNull(Document, nameof(Document));
    }

    /// <summary>Opens an external <c>http</c> or <c>https</c> URL. No local files are served.</summary>
    /// <param name="Url">The absolute URL the presentation opens.</param>
    public sealed record ExternalUrl(Uri Url) : DesktopContent
    {
        /// <summary>Gets the absolute URL the presentation opens.</summary>
        public Uri Url { get; init => field = RequireHttpUrl(value, nameof(Url)); } = RequireHttpUrl(Url, nameof(Url));
    }

    /// <summary>Resolves every request with a handler. No local files are served.</summary>
    /// <param name="Resolve">The request-scoped resolver; a <see langword="null"/> response is 404 Not Found.</param>
    public sealed record Handler(ContentHandler Resolve) : DesktopContent
    {
        /// <summary>Gets the request-scoped resolver.</summary>
        public ContentHandler Resolve { get; init => field = RequireNotNull(value, nameof(Resolve)); } = RequireNotNull(Resolve, nameof(Resolve));
    }

    // Each property validates in its initializer (constructor) and in its init accessor (object initializers
    // and `with` expressions), so no instance holds an unchecked value.
    private static T RequireNotNull<T>(T value, string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        return value;
    }

    private static string RequireRoot(string root, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root, parameterName);
        return root;
    }

    private static string? RequireRelativeEntry(string? entry, string parameterName)
    {
        if (entry is null)
        {
            return null;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(entry, parameterName);
        if (Path.IsPathRooted(entry) || entry.Replace('\\', '/').Split('/').Contains(".."))
        {
            throw new ArgumentException(
                $"The entry must be a path inside the root directory, such as \"index.html\": {entry}. " +
                "To open a file elsewhere, use its folder as the root.",
                parameterName);
        }
        return entry;
    }

    private static Uri RequireHttpUrl(Uri url, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(url, parameterName);
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"The external URL must be an absolute http or https URL: {url}.", parameterName);
        }
        return url;
    }
}
