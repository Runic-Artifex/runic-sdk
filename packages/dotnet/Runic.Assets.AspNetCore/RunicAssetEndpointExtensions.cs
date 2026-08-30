using System;
using System.IO;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace Runic.Assets.AspNetCore;

/// <summary>Maps a Runic Assets source to ASP.NET Core endpoints.</summary>
public static class RunicAssetEndpointExtensions
{
    /// <summary>Maps all exact manifest paths below an optional route prefix.</summary>
    public static IEndpointConventionBuilder MapRunicAssetSource(
        this IEndpointRouteBuilder endpoints,
        IAssetSource source,
        string routePrefix = "")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(source);
        if (source is not IAssetSnapshotSource)
        {
            throw new ArgumentException(
                "ASP.NET Core delivery requires a source that atomically owns descriptor-and-stream snapshots.",
                nameof(source));
        }

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
        if (source is not IAssetSnapshotSource snapshots)
        {
            throw new ArgumentException(
                "ASP.NET Core delivery requires a source that atomically owns descriptor-and-stream snapshots.",
                nameof(source));
        }

        await using AssetReadSnapshot snapshot = await snapshots.OpenSnapshotAsync(
            descriptor.RelativePath,
            context.RequestAborted).ConfigureAwait(false);
        AssetDescriptor current = snapshot.Descriptor;

        context.Response.Headers.ETag = current.EntityTag;
        context.Response.Headers.CacheControl = current.CacheControl;
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (MatchesIfNoneMatch(context.Request.Headers.IfNoneMatch, current.EntityTag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        long start = 0;
        long length = current.Length;
        long parsedStart = 0;
        long parsedLength = 0;
        AssetRangeResult range = context.Request.Headers.Range.Count != 0
            && MatchesIfRange(context.Request.Headers.IfRange, current.EntityTag)
            ? ParseRange(context.Request.Headers.Range.ToString(), current.Length, out parsedStart, out parsedLength)
            : AssetRangeResult.Ignore;
        if (range == AssetRangeResult.Unsatisfiable)
        {
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            context.Response.Headers.ContentRange = "bytes */" + current.Length.ToString(CultureInfo.InvariantCulture);
            return;
        }

        bool hasRange = range == AssetRangeResult.Satisfiable;
        if (hasRange)
        {
            start = parsedStart;
            length = parsedLength;
        }

        context.Response.StatusCode = hasRange ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        context.Response.ContentType = current.MediaType;
        context.Response.ContentLength = length;
        if (hasRange)
        {
            long end = checked(start + length - 1);
            context.Response.Headers.ContentRange = "bytes "
                + start.ToString(CultureInfo.InvariantCulture)
                + "-"
                + end.ToString(CultureInfo.InvariantCulture)
                + "/"
                + current.Length.ToString(CultureInfo.InvariantCulture);
        }

        await CopyRangeAsync(snapshot.Content, context.Response.Body, start, length, context.RequestAborted)
            .ConfigureAwait(false);
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

    private static bool MatchesIfNoneMatch(StringValues values, string entityTag)
    {
        foreach (string? value in values)
        {
            if (value is null)
            {
                continue;
            }

            foreach (string candidate in value.Split(',', StringSplitOptions.TrimEntries))
            {
                if (candidate == "*"
                    || StringComparer.Ordinal.Equals(RemoveWeakPrefix(candidate), entityTag))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool MatchesIfRange(StringValues values, string entityTag)
    {
        if (values.Count == 0)
        {
            return true;
        }

        return values.Count == 1
            && StringComparer.Ordinal.Equals(values[0]?.Trim(), entityTag);
    }

    private static string RemoveWeakPrefix(string value) =>
        value.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? value[2..].TrimStart() : value;

    private static AssetRangeResult ParseRange(string value, long assetLength, out long start, out long length)
    {
        start = 0;
        length = 0;
        if (!value.StartsWith("bytes=", StringComparison.Ordinal)
            || value.Contains(',', StringComparison.Ordinal))
        {
            return AssetRangeResult.Ignore;
        }

        string range = value["bytes=".Length..];
        int separator = range.IndexOf('-');
        if (separator < 0 || separator != range.LastIndexOf('-'))
        {
            return AssetRangeResult.Ignore;
        }

        string left = range[..separator];
        string right = range[(separator + 1)..];
        if (left.Length == 0)
        {
            if (!long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out long suffix)
                || suffix <= 0)
            {
                return AssetRangeResult.Ignore;
            }

            if (assetLength == 0)
            {
                return AssetRangeResult.Unsatisfiable;
            }

            length = Math.Min(suffix, assetLength);
            start = assetLength - length;
            return AssetRangeResult.Satisfiable;
        }

        if (!long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out start)
            || start < 0)
        {
            return AssetRangeResult.Ignore;
        }

        if (start >= assetLength)
        {
            return AssetRangeResult.Unsatisfiable;
        }

        if (right.Length == 0)
        {
            length = assetLength - start;
            return AssetRangeResult.Satisfiable;
        }

        if (!long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out long end)
            || end < start)
        {
            return AssetRangeResult.Ignore;
        }

        end = Math.Min(end, assetLength - 1);
        length = checked(end - start + 1);
        return AssetRangeResult.Satisfiable;
    }

    private static async Task CopyRangeAsync(
        Stream source,
        Stream destination,
        long start,
        long length,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81_920];
        long skipped = 0;
        while (skipped < start)
        {
            int request = (int)Math.Min(buffer.Length, start - skipped);
            int read = await source.ReadAsync(buffer.AsMemory(0, request), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("The asset ended before its declared range offset.");
            }

            skipped += read;
        }

        long remaining = length;
        while (remaining != 0)
        {
            int request = (int)Math.Min(buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, request), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("The asset ended before its declared range length.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private enum AssetRangeResult
    {
        Ignore,
        Satisfiable,
        Unsatisfiable,
    }
}
