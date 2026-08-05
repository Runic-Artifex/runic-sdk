using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RunicAssets;

/// <summary>Describes the cache semantics of immutable or live asset content.</summary>
public enum AssetCacheMode
{
    /// <summary>Do not retain this asset; intended for live development directories.</summary>
    NoStore,
    /// <summary>Retain the asset but revalidate it with its strong entity tag.</summary>
    Revalidate,
    /// <summary>Retain the content indefinitely because its URL identifies immutable content.</summary>
    Immutable,
}

/// <summary>Contains deterministic metadata for one application asset.</summary>
public sealed record AssetDescriptor
{
    /// <summary>Initializes asset metadata.</summary>
    public AssetDescriptor(
        string relativePath,
        string mediaType,
        long length,
        string sha256,
        bool isEntryPoint = false,
        AssetCacheMode cacheMode = AssetCacheMode.Revalidate)
    {
        RelativePath = AssetPath.Normalize(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        if (mediaType != mediaType.Trim() || mediaType.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("An asset media type must be a single normalized value.", nameof(mediaType));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(length);

        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sha256.Length != 64)
        {
            throw new ArgumentException("An asset SHA-256 digest must contain 64 hexadecimal characters.", nameof(sha256));
        }

        foreach (char character in sha256)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                throw new ArgumentException("An asset SHA-256 digest must be hexadecimal.", nameof(sha256));
            }
        }

        if (!Enum.IsDefined(cacheMode))
        {
            throw new ArgumentOutOfRangeException(nameof(cacheMode));
        }

        MediaType = mediaType;
        Length = length;
        Sha256 = sha256.ToLowerInvariant();
        IsEntryPoint = isEntryPoint;
        CacheMode = cacheMode;
    }

    /// <summary>Gets the normalized application-relative path.</summary>
    public string RelativePath { get; }
    /// <summary>Gets the response media type.</summary>
    public string MediaType { get; }
    /// <summary>Gets the byte length.</summary>
    public long Length { get; }
    /// <summary>Gets the lowercase SHA-256 digest.</summary>
    public string Sha256 { get; }
    /// <summary>Gets whether the asset is the application's entry document.</summary>
    public bool IsEntryPoint { get; }
    /// <summary>Gets the cache behavior.</summary>
    public AssetCacheMode CacheMode { get; }
    /// <summary>Gets a stable strong entity tag derived from the content digest.</summary>
    public string EntityTag => $"\"sha256-{Sha256}\"";
    /// <summary>Gets a transport-neutral HTTP-compatible cache-control value.</summary>
    public string CacheControl => CacheMode switch
    {
        AssetCacheMode.NoStore => "no-store",
        AssetCacheMode.Immutable => "public, max-age=31536000, immutable",
        _ => "no-cache",
    };
}

/// <summary>Provides an immutable, deterministically ordered asset catalog.</summary>
public sealed class AssetManifest
{
    /// <summary>The current package-neutral manifest schema identifier.</summary>
    public const string CurrentVersion = "runic.assets/1";
    private readonly IReadOnlyList<AssetDescriptor> _assets;
    private readonly Dictionary<string, AssetDescriptor> _byPath;
    private readonly string _version = CurrentVersion;

    /// <summary>Creates a validated immutable manifest.</summary>
    public AssetManifest(IEnumerable<AssetDescriptor> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var sorted = new List<AssetDescriptor>();
        foreach (AssetDescriptor asset in assets)
        {
            sorted.Add(asset ?? throw new ArgumentException(
                "An asset manifest cannot contain null entries.",
                nameof(assets)));
        }

        sorted.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        if (sorted.Count == 0)
        {
            throw new ArgumentException("An asset manifest cannot be empty.", nameof(assets));
        }

        var byPath = new Dictionary<string, AssetDescriptor>(StringComparer.Ordinal);
        int entryPoints = 0;
        foreach (AssetDescriptor asset in sorted)
        {
            if (!byPath.TryAdd(asset.RelativePath, asset))
            {
                throw new ArgumentException("Asset paths must be unique using ordinal comparison.", nameof(assets));
            }

            entryPoints += asset.IsEntryPoint ? 1 : 0;
        }

        if (entryPoints != 1)
        {
            throw new ArgumentException("An asset manifest must contain exactly one entry point.", nameof(assets));
        }

        _assets = Array.AsReadOnly(sorted.ToArray());
        _byPath = byPath;
    }

    /// <summary>Gets the manifest schema identifier.</summary>
    public string Version => _version;
    /// <summary>Gets assets in ordinal path order.</summary>
    public IReadOnlyList<AssetDescriptor> Assets => _assets;
    /// <summary>Gets the sole entry point.</summary>
    public AssetDescriptor EntryPoint
    {
        get
        {
            foreach (AssetDescriptor asset in _assets)
            {
                if (asset.IsEntryPoint)
                {
                    return asset;
                }
            }

            throw new InvalidOperationException("The validated manifest has no entry point.");
        }
    }

    /// <summary>Finds one exact, case-sensitive path.</summary>
    public bool TryGetAsset(string relativePath, out AssetDescriptor? asset) =>
        _byPath.TryGetValue(AssetPath.Normalize(relativePath), out asset);
}

/// <summary>Opens assets from one manifest-owned storage boundary.</summary>
public interface IAssetSource
{
    /// <summary>Gets the current immutable manifest snapshot.</summary>
    AssetManifest Manifest { get; }
    /// <summary>Validates metadata and backing content.</summary>
    ValueTask ValidateAsync(CancellationToken cancellationToken = default);
    /// <summary>Opens one exact manifest path for reading.</summary>
    ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default);
}
