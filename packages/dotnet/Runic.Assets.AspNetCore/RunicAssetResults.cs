using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Runic.Assets.AspNetCore;

/// <summary>Creates minimal-API results that serve Runic Assets.</summary>
public static class RunicAssetResults
{
    /// <summary>
    /// Creates a result that serves one asset of <paramref name="source"/> with its manifest-owned HTTP metadata,
    /// as <see cref="RunicAssetEndpointExtensions.WriteAssetAsync"/> does: ETag, Cache-Control, conditional and
    /// range requests, and a <c>text/*</c> media type with <c>; charset=utf-8</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="relativePath"/> resolves with <see cref="AssetManifest.TryResolveRequestPath"/> when the result
    /// runs, against the source's current manifest and <paramref name="routing"/>: an empty path serves the entry
    /// point and an extensionless path falls back to it unless <paramref name="routing"/> disables that. A path that
    /// does not resolve, or a request other than GET or HEAD, is answered with 404 Not Found. HTML assets carry
    /// <see cref="AssetRoutingOptions.ContentSecurityPolicy"/> when it is set.
    /// <code>
    /// app.MapGet("/app/{**path}", (string? path) => RunicAssetResults.Asset(assets, path ?? ""));
    /// </code>
    /// </remarks>
    /// <param name="source">The source that holds the asset.</param>
    /// <param name="relativePath">The request path below the route, such as <c>assets/app.js</c>.</param>
    /// <param name="routing">The routing rules and the HTML policy; <see langword="null"/> uses the defaults.</param>
    /// <returns>The result to return from a minimal-API handler.</returns>
    public static IResult Asset(IAssetSource source, string relativePath, AssetRoutingOptions? routing = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(relativePath);
        return new AssetResult(source, relativePath, routing);
    }

    private sealed class AssetResult(IAssetSource source, string relativePath, AssetRoutingOptions? routing) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            if ((!HttpMethods.IsGet(httpContext.Request.Method) && !HttpMethods.IsHead(httpContext.Request.Method))
                || !source.Manifest.TryResolveRequestPath(relativePath, routing, out AssetDescriptor? descriptor))
            {
                httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            return RunicAssetEndpointExtensions.WriteAssetCoreAsync(
                httpContext, source, descriptor, routing?.ContentSecurityPolicy);
        }
    }
}
