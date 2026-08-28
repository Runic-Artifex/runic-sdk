using System.Text;

namespace Runic.Desktop;

/// <summary>Handles one URL-decoded path before Runic Desktop falls back to its local root.</summary>
/// <param name="path">The requested absolute path, beginning with <c>/</c>.</param>
/// <param name="cancellationToken">Cancelled when the HTTP request is aborted or the managed window closes.</param>
/// <returns>Virtual content, or <see langword="null"/> to use the configured local content.</returns>
public delegate ValueTask<WebUiContent?> WebUiFileHandler(
    string path,
    CancellationToken cancellationToken);

/// <summary>Opens one streaming response body for the current request.</summary>
/// <param name="cancellationToken">Cancelled when the request or managed window closes.</param>
/// <returns>A readable stream that Runic Desktop owns and disposes after the response ends.</returns>
public delegate ValueTask<Stream> WebUiStreamFactory(CancellationToken cancellationToken);

/// <summary>Represents a fixed or streaming virtual response returned by a Runic Desktop file handler.</summary>
public sealed class WebUiContent
{
    private readonly WebUiStreamFactory? _streamFactory;

    /// <summary>Creates virtual response content.</summary>
    public WebUiContent(
        ReadOnlyMemory<byte> body,
        string? contentType = null,
        int statusCode = 200,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(statusCode, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(statusCode, 599);
        Body = body;
        ContentLength = body.Length;
        ContentType = contentType;
        StatusCode = statusCode;
        Headers = headers;
    }

    private WebUiContent(
        WebUiStreamFactory streamFactory,
        string? contentType,
        int statusCode,
        long? contentLength,
        IReadOnlyDictionary<string, string>? headers)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(statusCode, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(statusCode, 599);
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

    /// <summary>Gets the complete fixed response body, or empty memory for a streaming response.</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>Gets whether this response opens a request-scoped body stream.</summary>
    public bool IsStreaming => _streamFactory is not null;

    /// <summary>Gets the response length when known before streaming begins.</summary>
    public long? ContentLength { get; }

    /// <summary>Gets the response MIME type, or <see langword="null"/> to infer it from the path.</summary>
    public string? ContentType { get; }

    /// <summary>Gets the HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>Gets optional response headers applied after Runic Desktop's compatibility headers.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; }

    /// <summary>Creates UTF-8 text content.</summary>
    public static WebUiContent FromText(string text, string contentType = "text/html; charset=utf-8", int statusCode = 200)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new WebUiContent(Encoding.UTF8.GetBytes(text), contentType, statusCode);
    }

    /// <summary>Creates a response whose readable stream is opened once for the current request.</summary>
    /// <remarks>
    /// Runic Desktop disposes the returned stream after success, failure, or cancellation.
    /// The factory is not invoked for a <c>HEAD</c> request.
    /// </remarks>
    public static WebUiContent FromStream(
        WebUiStreamFactory streamFactory,
        string? contentType = null,
        int statusCode = 200,
        long? contentLength = null,
        IReadOnlyDictionary<string, string>? headers = null)
        => new(streamFactory, contentType, statusCode, contentLength, headers);

    internal async ValueTask WriteBodyAsync(Stream destination, CancellationToken cancellationToken)
    {
        if (_streamFactory is null)
        {
            await destination.WriteAsync(Body, cancellationToken).ConfigureAwait(false);
            return;
        }

        var source = await _streamFactory(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Runic Desktop stream factory returned null.");
        await using (source.ConfigureAwait(false))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }
}
