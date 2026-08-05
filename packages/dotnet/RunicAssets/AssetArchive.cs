using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RunicAssets;

/// <summary>Reads and writes the portable Runic Assets ZIP archive format.</summary>
public static class AssetArchive
{
    /// <summary>The archive contract written to <c>runic-assets.json</c>.</summary>
    public const string CurrentVersion = "runic.assets.archive/1";

    private const string ManifestEntryName = "runic-assets.json";
    private const string ContentPrefix = "assets/";
    private static readonly DateTimeOffset StableTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Writes a deterministic, portable ZIP archive from one validated source.</summary>
    public static async ValueTask WriteAsync(
        IAssetSource source,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The archive destination must be writable.", nameof(destination));
        }

        await source.ValidateAsync(cancellationToken).ConfigureAwait(false);
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        ZipArchiveEntry manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.SmallestSize);
        manifestEntry.LastWriteTime = StableTimestamp;
        await using (Stream manifestStream = manifestEntry.Open())
        {
            using var writer = new Utf8JsonWriter(manifestStream, new JsonWriterOptions { Indented = false });
            writer.WriteStartObject();
            writer.WriteString("version", CurrentVersion);
            writer.WriteStartArray("assets");
            foreach (AssetDescriptor descriptor in source.Manifest.Assets)
            {
                writer.WriteStartObject();
                writer.WriteString("path", descriptor.RelativePath);
                writer.WriteString("mediaType", descriptor.MediaType);
                writer.WriteNumber("length", descriptor.Length);
                writer.WriteString("sha256", descriptor.Sha256);
                writer.WriteBoolean("entryPoint", descriptor.IsEntryPoint);
                writer.WriteString("cacheMode", descriptor.CacheMode.ToString());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (AssetDescriptor descriptor in source.Manifest.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipArchiveEntry contentEntry = archive.CreateEntry(
                ContentPrefix + descriptor.RelativePath,
                IsAlreadyCompressed(descriptor.RelativePath)
                    ? CompressionLevel.NoCompression
                    : CompressionLevel.SmallestSize);
            contentEntry.LastWriteTime = StableTimestamp;
            await using Stream output = contentEntry.Open();
            await using Stream input = await source
                .OpenReadAsync(descriptor.RelativePath, cancellationToken)
                .ConfigureAwait(false);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Loads and validates a portable Runic Assets archive into memory.</summary>
    public static AssetArchiveSource Read(Stream source, AssetArchiveReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The archive source must be readable.", nameof(source));
        }

        options ??= new AssetArchiveReadOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxFileCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxArchiveBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxTotalUncompressedBytes, 1);

        using var copy = new MemoryStream();
        CopyBounded(source, copy, options.MaxArchiveBytes);
        copy.Position = 0;
        using var archive = new ZipArchive(copy, ZipArchiveMode.Read, leaveOpen: false);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            if (!entries.TryAdd(entry.FullName, entry))
            {
                throw new InvalidDataException($"The asset archive contains duplicate entry '{entry.FullName}'.");
            }
        }

        if (!entries.Remove(ManifestEntryName, out ZipArchiveEntry? manifestEntry))
        {
            throw new InvalidDataException("The asset archive does not contain runic-assets.json.");
        }

        AssetDescriptor[] descriptors;
        using (Stream manifestStream = manifestEntry.Open())
        using (JsonDocument document = JsonDocument.Parse(manifestStream))
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out JsonElement version)
                || !StringComparer.Ordinal.Equals(version.GetString(), CurrentVersion)
                || !root.TryGetProperty("assets", out JsonElement assets)
                || assets.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The asset archive manifest has an unsupported shape or version.");
            }

            var parsed = new List<AssetDescriptor>();
            long totalLength = 0;
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (parsed.Count >= options.MaxFileCount)
                {
                    throw new InvalidDataException(
                        $"The asset archive exceeds the {options.MaxFileCount} file limit.");
                }

                try
                {
                    var descriptor = new AssetDescriptor(
                        asset.GetProperty("path").GetString()!,
                        asset.GetProperty("mediaType").GetString()!,
                        asset.GetProperty("length").GetInt64(),
                        asset.GetProperty("sha256").GetString()!,
                        asset.GetProperty("entryPoint").GetBoolean(),
                        Enum.Parse<AssetCacheMode>(asset.GetProperty("cacheMode").GetString()!, ignoreCase: false));
                    if (descriptor.Length > options.MaxTotalUncompressedBytes - totalLength)
                    {
                        throw new InvalidDataException(
                            $"The asset archive exceeds the {options.MaxTotalUncompressedBytes} byte uncompressed-size limit.");
                    }

                    totalLength += descriptor.Length;
                    parsed.Add(descriptor);
                }
                catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
                {
                    throw new InvalidDataException("The asset archive manifest contains invalid asset metadata.", exception);
                }
            }

            descriptors = parsed.ToArray();
        }

        var content = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (AssetDescriptor descriptor in descriptors)
        {
            string entryName = ContentPrefix + descriptor.RelativePath;
            if (!entries.Remove(entryName, out ZipArchiveEntry? entry))
            {
                throw new InvalidDataException($"The asset archive is missing '{descriptor.RelativePath}'.");
            }

            if (entry.Length != descriptor.Length || entry.Length > int.MaxValue)
            {
                throw new InvalidDataException($"Asset '{descriptor.RelativePath}' has an unexpected length.");
            }

            using Stream input = entry.Open();
            using var output = new MemoryStream(checked((int)entry.Length));
            input.CopyTo(output);
            byte[] bytes = output.ToArray();
            using var validation = new MemoryStream(bytes, writable: false);
            AssetHashing.Verify(descriptor, validation);
            content.Add(descriptor.RelativePath, bytes);
        }

        if (entries.Count != 0)
        {
            throw new InvalidDataException("The asset archive contains undeclared files.");
        }

        return new AssetArchiveSource(new AssetManifest(descriptors), content);
    }

    private static bool IsAlreadyCompressed(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".7z" or ".avif" or ".br" or ".gif" or ".gz" or ".ico" or ".jpeg" or ".jpg"
            or ".mp3" or ".mp4" or ".ogg" or ".opus" or ".pdf" or ".png" or ".rar"
            or ".webm" or ".webp" or ".woff" or ".woff2" or ".zip";

    private static void CopyBounded(Stream source, Stream destination, long maximumBytes)
    {
        byte[] buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (read > maximumBytes - total)
            {
                throw new InvalidDataException(
                    $"The asset archive exceeds the {maximumBytes} byte archive-size limit.");
            }

            destination.Write(buffer, 0, read);
            total += read;
        }
    }
}

/// <summary>Bounds memory and file counts while reading an untrusted asset archive.</summary>
public sealed class AssetArchiveReadOptions
{
    /// <summary>The maximum compressed archive size accepted in bytes.</summary>
    public long MaxArchiveBytes { get; init; } = 1024L * 1024L * 1024L;

    /// <summary>The maximum number of declared files.</summary>
    public int MaxFileCount { get; init; } = 100_000;

    /// <summary>The maximum combined uncompressed content size accepted in bytes.</summary>
    public long MaxTotalUncompressedBytes { get; init; } = 1024L * 1024L * 1024L;
}

/// <summary>An immutable in-memory source loaded from a Runic Assets archive.</summary>
public sealed class AssetArchiveSource : IAssetSource
{
    private readonly IReadOnlyDictionary<string, byte[]> _content;

    internal AssetArchiveSource(AssetManifest manifest, IReadOnlyDictionary<string, byte[]> content)
    {
        Manifest = manifest;
        _content = content;
    }

    /// <inheritdoc />
    public AssetManifest Manifest { get; }

    /// <inheritdoc />
    public ValueTask ValidateAsync(CancellationToken cancellationToken = default)
    {
        foreach (AssetDescriptor descriptor in Manifest.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(_content[descriptor.RelativePath], writable: false);
            AssetHashing.Verify(descriptor, stream);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = AssetPath.Normalize(relativePath);
        if (!_content.TryGetValue(path, out byte[]? content))
        {
            throw new FileNotFoundException("The requested asset is not declared by the archive manifest.", path);
        }

        return ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
    }
}
