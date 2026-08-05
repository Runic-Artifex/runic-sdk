using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace RunicToolkit.Hosting.Build;

/// <summary>Builds deterministic, content-addressed frontend asset manifests.</summary>
public sealed class FrontendAssetManifestBuilder
{
    private readonly IFrontendMediaTypeResolver _mediaTypeResolver;

    /// <summary>Initializes a builder with the platform-independent default media type resolver.</summary>
    public FrontendAssetManifestBuilder()
        : this(new DefaultFrontendMediaTypeResolver())
    {
    }

    /// <summary>Initializes a builder with an explicit deterministic media type resolver.</summary>
    /// <param name="mediaTypeResolver">The resolver used when an item has no media type override.</param>
    public FrontendAssetManifestBuilder(IFrontendMediaTypeResolver mediaTypeResolver)
    {
        ArgumentNullException.ThrowIfNull(mediaTypeResolver);
        _mediaTypeResolver = mediaTypeResolver;
    }

    /// <summary>Builds a manifest from immutable content items.</summary>
    /// <param name="items">The complete frontend asset set in any enumeration order.</param>
    /// <returns>A validated manifest sorted by normalized path using ordinal comparison.</returns>
    public FrontendAssetManifest Build(IEnumerable<FrontendAssetBuildItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var assets = new List<FrontendAsset>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entryPointCount = 0;
        foreach (var item in items)
        {
            if (item is null)
            {
                throw new ArgumentException("Asset build items cannot contain null entries.", nameof(items));
            }

            var hash = Convert.ToHexStringLower(SHA256.HashData(item.ContentSpan));
            var mediaType = item.MediaType ?? _mediaTypeResolver.Resolve(item.RelativePath);
            var asset = new FrontendAsset(
                item.RelativePath,
                mediaType,
                item.ContentLength,
                hash,
                item.IsEntryPoint,
                item.BrotliPath,
                item.GzipPath);

            if (!paths.Add(asset.RelativePath))
            {
                throw new InvalidOperationException(
                    "Asset paths must be unique using case-insensitive comparison.");
            }

            assets.Add(asset);
            if (asset.IsEntryPoint)
            {
                entryPointCount++;
            }
        }

        if (assets.Count == 0)
        {
            throw new InvalidOperationException("The frontend asset manifest must contain at least one asset.");
        }

        if (entryPointCount != 1)
        {
            throw new InvalidOperationException(
                "The frontend asset manifest must contain exactly one entry point.");
        }

        foreach (var asset in assets)
        {
            if ((asset.BrotliPath is not null && !paths.Contains(asset.BrotliPath))
                || (asset.GzipPath is not null && !paths.Contains(asset.GzipPath)))
            {
                throw new InvalidOperationException(
                    "A declared compressed variant must refer to a present manifest asset.");
            }
        }

        assets.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        return new FrontendAssetManifest(assets);
    }

    /// <summary>Builds a manifest from every regular file below an output directory.</summary>
    /// <param name="outputRoot">The frontend output root.</param>
    /// <param name="entryPointRelativePath">The sole application-relative entry point.</param>
    /// <param name="cancellationToken">Stops enumeration and content reading.</param>
    /// <returns>A deterministic manifest containing an immutable snapshot of the directory.</returns>
    /// <remarks>
    /// Reparse points are rejected. Wave D may add a cross-platform link resolution policy that
    /// accepts links proven to remain below the output root.
    /// </remarks>
    public FrontendAssetManifest BuildFromDirectory(
        string outputRoot,
        string entryPointRelativePath,
        CancellationToken cancellationToken = default) =>
        BuildFromDirectory(
            outputRoot,
            entryPointRelativePath,
            Array.Empty<string>(),
            cancellationToken);

    /// <summary>
    /// Builds a manifest while excluding explicit application-relative build artifacts
    /// such as a manifest written below the frontend output root.
    /// </summary>
    /// <param name="outputRoot">The frontend output root.</param>
    /// <param name="entryPointRelativePath">The sole application-relative entry point.</param>
    /// <param name="excludedRelativePaths">Exact application-relative paths to exclude.</param>
    /// <param name="cancellationToken">Stops enumeration and content reading.</param>
    /// <returns>A deterministic manifest containing an immutable snapshot of the directory.</returns>
    public FrontendAssetManifest BuildFromDirectory(
        string outputRoot,
        string entryPointRelativePath,
        IEnumerable<string> excludedRelativePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPointRelativePath);
        ArgumentNullException.ThrowIfNull(excludedRelativePaths);

        var root = Path.GetFullPath(outputRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The frontend output directory does not exist.");
        }

        RejectReparsePoint(root);
        var normalizedEntryPoint = NormalizePathWithoutContent(entryPointRelativePath);
        var exclusions = new HashSet<string>(StringComparer.Ordinal);
        foreach (string excludedRelativePath in excludedRelativePaths)
        {
            exclusions.Add(NormalizePathWithoutContent(excludedRelativePath));
        }

        if (exclusions.Contains(normalizedEntryPoint))
        {
            throw new ArgumentException(
                "The frontend entry point cannot be excluded.",
                nameof(excludedRelativePaths));
        }

        var files = new List<(string FullPath, string RelativePath)>();
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = directories.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Frontend output cannot contain reparse points.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                }
                else
                {
                    var filesystemRelativePath = Path.GetRelativePath(root, entry);
                    RejectNormalizationIdentityChange(filesystemRelativePath);
                    var relativePath = NormalizePathWithoutContent(filesystemRelativePath);
                    if (!exclusions.Contains(relativePath))
                    {
                        files.Add((entry, relativePath));
                    }
                }
            }
        }

        files.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        var items = new List<FrontendAssetBuildItem>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = ReadFile(file.FullPath, cancellationToken);
            items.Add(new(
                file.RelativePath,
                content,
                StringComparer.Ordinal.Equals(file.RelativePath, normalizedEntryPoint)));
        }

        return Build(items);
    }

    private static byte[] ReadFile(string fullPath, CancellationToken cancellationToken)
    {
        RejectReparsePoint(fullPath);
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81_920,
            FileOptions.SequentialScan);
        if (stream.Length > int.MaxValue)
        {
            throw new IOException("A frontend asset is too large to snapshot in memory.");
        }

        var content = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < content.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(content, offset, content.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("A frontend asset changed while it was being read.");
            }

            offset += read;
        }

        if (stream.ReadByte() != -1)
        {
            throw new IOException("A frontend asset changed while it was being read.");
        }

        return content;
    }

    private static string NormalizePathWithoutContent(string relativePath)
    {
        var placeholderHash = new string('0', 64);
        return new FrontendAsset(
            relativePath,
            "application/octet-stream",
            0,
            placeholderHash).RelativePath;
    }

    private static void RejectNormalizationIdentityChange(string filesystemRelativePath)
    {
        if (!StringComparer.Ordinal.Equals(filesystemRelativePath, filesystemRelativePath.Trim()))
        {
            throw new IOException(
                "Frontend output names cannot have leading or trailing whitespace.");
        }

        if (Path.DirectorySeparatorChar != '\\' && filesystemRelativePath.Contains('\\', StringComparison.Ordinal))
        {
            throw new IOException(
                "Frontend output names cannot contain a literal backslash on this platform.");
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Frontend output cannot contain reparse points.");
        }
    }
}
