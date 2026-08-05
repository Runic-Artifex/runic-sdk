using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace RunicAssets.AspNetCore;

/// <summary>Maps a Runic Assets source to ASP.NET Core endpoints.</summary>
public static class RunicAssetEndpointExtensions
{
    /// <summary>Maps all exact manifest paths below an optional route prefix.</summary>
    public static IEndpointConventionBuilder MapRunicAssets(
        this IEndpointRouteBuilder endpoints,
        IAssetSource source,
        string routePrefix = "")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(source);
        routePrefix = NormalizePrefix(routePrefix);

        string route = "/" + (routePrefix.Length == 0 ? "" : routePrefix + "/") + "{**runicAssetPath}";
        return endpoints.MapGet(
            route,
            async context =>
            {
                string? requested = context.Request.RouteValues["runicAssetPath"] as string;
                if (requested is null
                    || !TryFind(source.Manifest, requested, out AssetDescriptor? descriptor))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await WriteAssetAsync(context, source, descriptor!).ConfigureAwait(false);
            });
    }

    /// <summary>Writes one declared asset with its manifest-owned HTTP metadata.</summary>
    public static async Task WriteAssetAsync(
        HttpContext context,
        IAssetSource source,
        AssetDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(descriptor);

        context.Response.Headers.ETag = descriptor.EntityTag;
        context.Response.Headers.CacheControl = descriptor.CacheControl;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (StringComparer.Ordinal.Equals(context.Request.Headers.IfNoneMatch, descriptor.EntityTag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = descriptor.MediaType;
        context.Response.ContentLength = descriptor.Length;
        await using Stream content = await source
            .OpenReadAsync(descriptor.RelativePath, context.RequestAborted)
            .ConfigureAwait(false);
        await content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    private static string NormalizePrefix(string routePrefix)
    {
        ArgumentNullException.ThrowIfNull(routePrefix);
        string normalized = routePrefix.Trim('/');
        return normalized.Length == 0 ? "" : AssetPath.Normalize(normalized);
    }

    private static bool TryFind(
        AssetManifest manifest,
        string requested,
        out AssetDescriptor? descriptor)
    {
        try
        {
            return manifest.TryGetAsset(requested, out descriptor);
        }
        catch (ArgumentException)
        {
            descriptor = null;
            return false;
        }
    }
}
