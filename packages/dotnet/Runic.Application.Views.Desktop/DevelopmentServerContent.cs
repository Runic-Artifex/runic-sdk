using Runic.Desktop;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// The Runic Desktop side of <see cref="DevelopmentServerForwarding"/>: a Window's resources after its own (a path
/// relative to its document), and the host listener's paths outside every Window (root-relative paths such as the Vite
/// DevTools dock's).
/// </summary>
internal static class DevelopmentServerContent
{
    /// <summary>The Window's resources: <paramref name="resources"/> first, then the development server.</summary>
    internal static ContentHandler ForWindow(DevelopmentServerForwarding forwarding, ContentHandler? resources) =>
        async (request, cancellationToken) =>
        {
            if (resources is not null &&
                await resources(request, cancellationToken).ConfigureAwait(false) is { } response)
                return response;
            return await ForwardAsync(forwarding, request, forwarding.DocumentTarget(request.Path, request.QueryString),
                cancellationToken).ConfigureAwait(false);
        };

    /// <summary>
    /// The listener's requests outside every Window's path, redirected to the same path on the development server
    /// rather than forwarded: a script or connection file loaded there resolves its own relative URLs against the server.
    /// The Vite DevTools dock, for example, opens its WebSocket relative to the <c>/__devtools/__connection.json</c> it
    /// read, which the Window's origin does not forward.
    /// </summary>
    internal static ContentHandler ForListener(DevelopmentServerForwarding forwarding) =>
        (request, _) =>
        {
            if (forwarding.RootTarget(request.Path, request.QueryString) is not { } target ||
                !DevelopmentServerForwarding.Admits(request.Method, request.RemoteIpAddress, Header(request, "Host"),
                    Header(request, "Sec-Fetch-Site"), request.Path))
                return ValueTask.FromResult<ContentResponse?>(null);
            return ValueTask.FromResult<ContentResponse?>(new ContentResponse(ReadOnlyMemory<byte>.Empty,
                "text/plain; charset=utf-8", 307, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Location"] = target.AbsoluteUri,
                    ["Cache-Control"] = "no-store",
                }));
        };

    private static async ValueTask<ContentResponse?> ForwardAsync(DevelopmentServerForwarding forwarding,
        ContentRequest request, Uri? target, CancellationToken cancellationToken)
    {
        if (target is null ||
            !DevelopmentServerForwarding.Admits(request.Method, request.RemoteIpAddress, Header(request, "Host"),
                Header(request, "Sec-Fetch-Site"), request.Path))
            return null;
        var head = string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
        var forwarded = await forwarding.SendAsync(target, head, Header(request, "Accept"), cancellationToken)
            .ConfigureAwait(false);
        if (forwarded is null) return null;
        if (head)
        {
            // A HEAD answer has no body to open.
            forwarded.Dispose();
            return ContentResponse.Stream(_ => ValueTask.FromResult(Stream.Null), forwarded.ContentType,
                forwarded.StatusCode, forwarded.ContentLength, forwarded.Headers);
        }
        return ContentResponse.Stream(async token => await forwarded.OpenAsync(token).ConfigureAwait(false),
            forwarded.ContentType, forwarded.StatusCode, forwarded.ContentLength, forwarded.Headers);
    }

    private static string? Header(ContentRequest request, string name) =>
        request.Headers.TryGetValue(name, out var value) ? value : null;
}
