using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RunicToolkit.Hosting.WebUi;

/// <summary>Identifies a supported pre-compressed representation.</summary>
public enum FrontendContentEncoding
{
    /// <summary>Uncompressed manifest content.</summary>
    Identity,
    /// <summary>Brotli-compressed content.</summary>
    Brotli,
    /// <summary>Gzip-compressed content.</summary>
    Gzip,
}

/// <summary>Contains one bounded, normalized static-asset request.</summary>
public sealed record FrontendAssetRequest(
    string RelativePath,
    IReadOnlyList<FrontendContentEncoding>? AcceptedEncodings = null);

/// <summary>Owns one selected static-asset response stream.</summary>
public sealed class FrontendAssetResponse : IAsyncDisposable
{
    internal FrontendAssetResponse(
        Stream content,
        string mediaType,
        long length,
        string sha256,
        FrontendContentEncoding contentEncoding)
    {
        Content = content;
        MediaType = mediaType;
        Length = length;
        Sha256 = sha256;
        ContentEncoding = contentEncoding;
    }

    /// <summary>Gets the readable response body.</summary>
    public Stream Content { get; }

    /// <summary>Gets the declared response media type.</summary>
    public string MediaType { get; }

    /// <summary>Gets the selected representation length.</summary>
    public long Length { get; }

    /// <summary>Gets the selected representation SHA-256 digest.</summary>
    public string Sha256 { get; }

    /// <summary>Gets the selected content encoding.</summary>
    public FrontendContentEncoding ContentEncoding { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// Maps exact normalized paths to a manifest provider without filesystem or framework discovery.
/// </summary>
public sealed class FrontendAssetEndpoint
{
    private readonly IFrontendAssetProvider _provider;
    private readonly Dictionary<string, FrontendAsset> _assets;

    /// <summary>Initializes an endpoint and selects its sole entry point.</summary>
    public FrontendAssetEndpoint(IFrontendAssetProvider provider, Uri baseUri)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new ArgumentException(
                "The frontend base URI must be absolute and cannot contain a query or fragment.",
                nameof(baseUri));
        }

        _assets = new Dictionary<string, FrontendAsset>(StringComparer.Ordinal);
        FrontendAsset? entryPoint = null;
        foreach (FrontendAsset asset in provider.Manifest.Assets)
        {
            if (!_assets.TryAdd(asset.RelativePath, asset))
            {
                throw new ArgumentException("The frontend manifest contains duplicate paths.", nameof(provider));
            }

            if (asset.IsEntryPoint)
            {
                if (entryPoint is not null)
                {
                    throw new ArgumentException(
                        "The frontend manifest contains more than one entry point.",
                        nameof(provider));
                }

                entryPoint = asset;
            }
        }

        if (entryPoint is null)
        {
            throw new ArgumentException(
                "The frontend manifest does not contain an entry point.",
                nameof(provider));
        }

        EntryPoint = new Uri(
            EnsureTrailingSlash(baseUri),
            EscapeRelativePath(entryPoint.RelativePath));
    }

    /// <summary>Gets the deterministic absolute browser entry point.</summary>
    public Uri EntryPoint { get; }

    /// <summary>Opens one exact asset and deterministically selects Brotli, gzip, or identity.</summary>
    public async ValueTask<FrontendAssetResponse> OpenAsync(
        FrontendAssetRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_assets.TryGetValue(request.RelativePath, out FrontendAsset? asset))
        {
            throw new FileNotFoundException(
                "The requested frontend asset is not declared by the manifest.");
        }

        FrontendContentEncoding encoding = SelectEncoding(asset, request.AcceptedEncodings);
        string selectedPath = encoding switch
        {
            FrontendContentEncoding.Brotli => asset.BrotliPath!,
            FrontendContentEncoding.Gzip => asset.GzipPath!,
            _ => asset.RelativePath,
        };
        FrontendAsset selectedAsset = _assets[selectedPath];
        Stream stream = await _provider
            .OpenReadAsync(selectedPath, cancellationToken)
            .ConfigureAwait(false);
        return new FrontendAssetResponse(
            stream,
            asset.MediaType,
            selectedAsset.Length,
            selectedAsset.Sha256,
            encoding);
    }

    private static FrontendContentEncoding SelectEncoding(
        FrontendAsset asset,
        IReadOnlyList<FrontendContentEncoding>? accepted)
    {
        if (accepted is null)
        {
            return FrontendContentEncoding.Identity;
        }

        bool acceptsBrotli = false;
        bool acceptsGzip = false;
        for (int index = 0; index < accepted.Count; index++)
        {
            acceptsBrotli |= accepted[index] == FrontendContentEncoding.Brotli;
            acceptsGzip |= accepted[index] == FrontendContentEncoding.Gzip;
        }

        if (acceptsBrotli && asset.BrotliPath is not null)
        {
            return FrontendContentEncoding.Brotli;
        }

        return acceptsGzip && asset.GzipPath is not null
            ? FrontendContentEncoding.Gzip
            : FrontendContentEncoding.Identity;
    }

    private static Uri EnsureTrailingSlash(Uri baseUri) =>
        baseUri.AbsoluteUri.EndsWith('/')
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + "/", UriKind.Absolute);

    private static string EscapeRelativePath(string relativePath)
    {
        string[] segments = relativePath.Split('/');
        for (int index = 0; index < segments.Length; index++)
        {
            segments[index] = Uri.EscapeDataString(segments[index]);
        }

        return string.Join('/', segments);
    }
}
