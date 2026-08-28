using System.Text;

namespace Runic.Desktop;

/// <summary>Handles one URL-decoded path before Runic Desktop falls back to its local root.</summary>
/// <param name="path">The requested absolute path, beginning with <c>/</c>.</param>
/// <param name="cancellationToken">Cancelled when the HTTP request is aborted.</param>
/// <returns>Virtual content, or <see langword="null"/> to use the configured local content.</returns>
public delegate ValueTask<WebUiContent?> WebUiFileHandler(
    string path,
    CancellationToken cancellationToken);

/// <summary>Represents a complete virtual response returned by a Runic Desktop file handler.</summary>
public sealed class WebUiContent
{
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
        ContentType = contentType;
        StatusCode = statusCode;
        Headers = headers;
    }

    /// <summary>Gets the complete response body.</summary>
    public ReadOnlyMemory<byte> Body { get; }

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
}
