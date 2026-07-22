using System;

namespace WebUIToolkit.Hosting.Build;

/// <summary>Contains the immutable content and metadata used to build one frontend asset entry.</summary>
public sealed class FrontendAssetBuildItem
{
    private readonly byte[] _content;

    /// <summary>Initializes an asset build item and snapshots its content.</summary>
    /// <param name="relativePath">The application-relative asset path.</param>
    /// <param name="content">The complete uncompressed content.</param>
    /// <param name="isEntryPoint">Whether the item is the sole application entry point.</param>
    /// <param name="mediaType">An optional media type override.</param>
    /// <param name="brotliPath">An optional application-relative Brotli variant path.</param>
    /// <param name="gzipPath">An optional application-relative gzip variant path.</param>
    public FrontendAssetBuildItem(
        string relativePath,
        ReadOnlyMemory<byte> content,
        bool isEntryPoint = false,
        string? mediaType = null,
        string? brotliPath = null,
        string? gzipPath = null)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        RelativePath = relativePath;
        _content = content.ToArray();
        IsEntryPoint = isEntryPoint;
        MediaType = mediaType;
        BrotliPath = brotliPath;
        GzipPath = gzipPath;
    }

    /// <summary>Gets the input application-relative asset path.</summary>
    public string RelativePath { get; }

    /// <summary>Gets a defensive copy of the snapshotted uncompressed content.</summary>
    public ReadOnlyMemory<byte> Content => _content.AsSpan().ToArray();

    /// <summary>Gets whether the item is the application entry point.</summary>
    public bool IsEntryPoint { get; }

    /// <summary>Gets the optional media type override.</summary>
    public string? MediaType { get; }

    /// <summary>Gets the optional application-relative Brotli variant path.</summary>
    public string? BrotliPath { get; }

    /// <summary>Gets the optional application-relative gzip variant path.</summary>
    public string? GzipPath { get; }

    internal ReadOnlySpan<byte> ContentSpan => _content;

    internal int ContentLength => _content.Length;
}
