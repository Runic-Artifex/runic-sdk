using System.Collections.ObjectModel;
using Runic.Application.Views;

namespace Runic.Application.Testing;

/// <summary>
/// The response a window's host gives for a blob URL (blobs/1), as <see cref="RunicWindowTestHost{TViewModel}.ReadBlobAsync"/>
/// serves it.
/// </summary>
/// <param name="StatusCode">The HTTP status: 200, 206 for a range, 304, 404 for a released or unknown URL, 416.</param>
/// <param name="Headers">The response headers, such as <c>Content-Disposition</c> and <c>Cache-Control</c>, by name.</param>
/// <param name="Content">The body; empty for <c>HEAD</c> and for a status without one.</param>
public sealed record RunicServedBlob(int StatusCode, IReadOnlyDictionary<string, string> Headers, byte[] Content)
{
    /// <summary>The <c>Content-Type</c> header, or <see langword="null"/>.</summary>
    public string? ContentType => Headers.TryGetValue("Content-Type", out var value) ? value : null;

    /// <summary>The <c>Content-Disposition</c> header, or <see langword="null"/>.</summary>
    public string? ContentDisposition => Headers.TryGetValue("Content-Disposition", out var value) ? value : null;
}

// The test window's blob endpoint: blob URLs below TestOrigin, served in memory.
internal static class RunicBlobs
{
    internal static readonly Uri TestOrigin = new("http://runic.test/");

    internal static async Task<RunicServedBlob> ServeAsync(BridgeBlobs blobs, string url, string? range, bool head,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        var prefix = new Uri(TestOrigin, BridgeBlobEndpoint.BlobsPath).AbsoluteUri;
        if (!url.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException($"'{url}' is not a blob URL of this window; it starts with {prefix}.", nameof(url));
        var exchange = new Exchange(head ? "HEAD" : "GET", range, cancellationToken);
        await blobs.ServeBlobAsync(url[prefix.Length..], exchange).ConfigureAwait(false);
        return new RunicServedBlob(exchange.StatusCode, new ReadOnlyDictionary<string, string>(exchange.Headers),
            exchange.Body.ToArray());
    }

    private sealed class Exchange(string method, string? range, CancellationToken aborted) : IBridgeBlobExchange
    {
        internal Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal MemoryStream Body { get; } = new();

        public string Method => method;
        public string GetRequestHeader(string name) =>
            range is not null && string.Equals(name, "Range", StringComparison.OrdinalIgnoreCase) ? range : string.Empty;
        public Stream RequestBody => Stream.Null;
        public int StatusCode { get; set; } = 200;
        public string? ContentType { set { if (value is not null) Headers["Content-Type"] = value; } }
        public long? ContentLength { set { if (value is { } length) Headers["Content-Length"] = length.ToString(System.Globalization.CultureInfo.InvariantCulture); } }
        public void SetResponseHeader(string name, string value) => Headers[name] = value;
        public Stream ResponseBody => Body;
        public CancellationToken RequestAborted => aborted;
    }
}
