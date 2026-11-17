using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TUnit.Core;

namespace Runic.Assets.Tests;

// runic-sdk#1109 (8): an archive stores each distinct content once. Schema runic.assets.archive/2 adds "contentPath",
// naming the asset whose ZIP entry holds the bytes; an archive without duplicates stays schema 1, byte for byte, and
// schema-1 archives with duplicate entries, as older SDKs wrote them, still load. Cross-platform: the trusted directory
// writer runs on every operating system.
internal sealed class ArchiveSharedContentTests
{
    private const string SharedContentVersion = "runic.assets.archive/2";

    [Test, DisplayName("identical files are stored once and every path reads back its own bytes and metadata")]
    public async Task IdenticalFilesStoredOnce()
    {
        using var directory = new TemporaryDirectory();
        Dictionary<string, byte[]> files = WriteTwoFrontends(directory);
        byte[] archive = await WriteAsync(directory.Path).ConfigureAwait(false);

        // One ZIP entry per distinct content: the first path in ordinal order holds it.
        SequenceEqual(
            new[]
            {
                "assets/main/app.css", "assets/main/fonts/inter.woff2", "assets/main/index.html",
                "assets/settings/index.html", "runic-assets.json",
            },
            ZipEntryNames(archive));
        using (JsonDocument manifest = ReadManifest(archive))
        {
            Equal(SharedContentVersion, manifest.RootElement.GetProperty("version").GetString());
            Dictionary<string, string?> contentPaths = manifest.RootElement.GetProperty("assets").EnumerateArray().ToDictionary(
                static asset => asset.GetProperty("path").GetString()!,
                static asset => asset.TryGetProperty("contentPath", out JsonElement value) ? value.GetString() : null);
            Equal<string?>(null, contentPaths["main/app.css"]);
            Equal<string?>("main/app.css", contentPaths["notes/app.txt"]);
            Equal<string?>("main/app.css", contentPaths["settings/app.css"]);
            Equal<string?>("main/fonts/inter.woff2", contentPaths["settings/fonts/inter.woff2"]);
            Equal<string?>(null, contentPaths["settings/index.html"]);
        }

        AssetArchiveSource[] readers =
        [
            AssetArchive.Read(new MemoryStream(archive, writable: false)),
            await AssetArchive.ReadAsync(new MemoryStream(archive, writable: false)).ConfigureAwait(false),
            AssetArchive.ReadInPlace(new MemoryStream(archive, writable: false), null),
        ];
        foreach (AssetArchiveSource source in readers)
        {
            Equal(files.Count, source.Manifest.Assets.Count);
            // Aliases first, so a lazily read archive inflates shared content through an alias.
            foreach ((string path, byte[] content) in files.OrderByDescending(static file => file.Key, StringComparer.Ordinal))
            {
                SequenceEqual(content, await ReadAllAsync(source, path).ConfigureAwait(false));
                True(source.Manifest.TryGetAsset(path, out AssetDescriptor? descriptor), path);
                Equal(content.LongLength, descriptor!.Length);
                Equal($"\"sha256-{Convert.ToHexStringLower(SHA256.HashData(content))}\"", descriptor.EntityTag);
                Equal(AssetArchive.InferCacheMode(path), descriptor.CacheMode);
                Equal(path == "main/index.html", descriptor.IsEntryPoint);
            }

            // The same bytes keep the media type of each path.
            Equal("text/css", Descriptor(source, "settings/app.css").MediaType);
            Equal("text/plain", Descriptor(source, "notes/app.txt").MediaType);
            Equal("font/woff2", Descriptor(source, "settings/fonts/inter.woff2").MediaType);
            await source.ValidateAsync().ConfigureAwait(false);
            IAssetSource settings = source.Subdirectory("settings");
            SequenceEqual(files["settings/fonts/inter.woff2"], await ReadAllAsync(settings, "fonts/inter.woff2").ConfigureAwait(false));
        }

        // Writing again gives the same bytes, and inspection reports the shared-content schema.
        SequenceEqual(archive, await WriteAsync(directory.Path).ConfigureAwait(false));
        AssetArchiveInspection inspection = AssetArchive.Inspect(new MemoryStream(archive, writable: false));
        Equal(SharedContentVersion, inspection.ArchiveVersion);
        Equal(Convert.ToHexStringLower(SHA256.HashData(ReadManifestBytes(archive))), inspection.ManifestSha256);

        // The same assets stored once per path, as schema 1 does, take more room.
        byte[] schema1 = Schema1Archive(files, entryPoint: "main/index.html");
        True(archive.Length + files["main/fonts/inter.woff2"].Length < schema1.Length, $"{archive.Length} vs {schema1.Length}");
    }

    [Test, DisplayName("an archive without duplicate content is written as schema 1")]
    public async Task DistinctContentStaysSchema1()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("index.html", "<p>entry</p>");
        directory.Write("app.css", "body { margin: 0; }");
        byte[] archive = await WriteAsync(directory.Path, "index.html").ConfigureAwait(false);
        using JsonDocument manifest = ReadManifest(archive);
        Equal(AssetArchive.CurrentVersion, manifest.RootElement.GetProperty("version").GetString());
        True(manifest.RootElement.GetProperty("assets").EnumerateArray().All(static asset => !asset.TryGetProperty("contentPath", out _)), "contentPath");
        Equal(AssetArchive.CurrentVersion, AssetArchive.Inspect(new MemoryStream(archive, writable: false)).ArchiveVersion);
    }

    [Test, DisplayName("schema-1 archives that store duplicate content twice still load")]
    public async Task Schema1DuplicatesStillLoad()
    {
        using var directory = new TemporaryDirectory();
        Dictionary<string, byte[]> files = WriteTwoFrontends(directory);
        byte[] schema1 = Schema1Archive(files, entryPoint: "main/index.html");
        AssetArchiveSource[] readers =
        [
            AssetArchive.Read(new MemoryStream(schema1, writable: false)),
            await AssetArchive.ReadAsync(new MemoryStream(schema1, writable: false)).ConfigureAwait(false),
            AssetArchive.ReadInPlace(new MemoryStream(schema1, writable: false), null),
        ];
        foreach (AssetArchiveSource source in readers)
        {
            foreach ((string path, byte[] content) in files)
            {
                SequenceEqual(content, await ReadAllAsync(source, path).ConfigureAwait(false));
            }

            await source.ValidateAsync().ConfigureAwait(false);
        }

        // Re-packing a schema-1 archive's assets stores the duplicates once, with the same descriptors.
        using var repacked = new MemoryStream();
        await AssetArchive.WriteAsync(readers[0], repacked).ConfigureAwait(false);
        AssetArchiveSource restored = AssetArchive.Read(new MemoryStream(repacked.ToArray(), writable: false));
        SequenceEqual(readers[0].Manifest.Assets, restored.Manifest.Assets);
        Equal(5, ZipEntryNames(repacked.ToArray()).Length);
    }

    [Test, DisplayName("readers reject invalid shared content")]
    public Task InvalidSharedContent()
    {
        byte[] entry = "<p>entry</p>"u8.ToArray();
        byte[] css = "body { margin: 0; }"u8.ToArray();
        byte[] other = "body { margin: 1; }"u8.ToArray();
        (string, byte[])[] stored = [("assets/index.html", entry), ("assets/a.css", css)];
        string index = Asset("index.html", "text/html", entry, entryPoint: true);
        string a = Asset("a.css", "text/css", css);

        // Valid, to show that the cases below fail for the stated reason only.
        _ = AssetArchive.Read(Archive(SharedContentVersion, [index, a, Asset("b.css", "text/css", css, "a.css")], stored));

        string[][] invalid =
        [
            [index, a, Asset("b.css", "text/css", css, "missing.css")],
            [index, a, Asset("b.css", "text/css", css, "b.css")],
            [index, a, Asset("b.css", "text/css", css[..^1], "a.css")],
            [index, a, Asset("b.css", "text/css", css, "a.css"), Asset("c.css", "text/css", css, "b.css")],
            [index, a, Asset("b.css", "text/css", css, "../a.css")],
        ];
        foreach (string[] assets in invalid)
        {
            Throws<InvalidDataException>(() => AssetArchive.Read(Archive(SharedContentVersion, assets, stored)));
            Throws<InvalidDataException>(() => AssetArchive.ReadInPlace(Archive(SharedContentVersion, assets, stored), null));
        }

        // Another digest is checked with the content, as every digest: Read rejects the archive, and an archive read in
        // place, whose digests the build checks, fails ValidateAsync.
        string[] otherDigest = [index, a, Asset("b.css", "text/css", other, "a.css")];
        Throws<InvalidDataException>(() => AssetArchive.Read(Archive(SharedContentVersion, otherDigest, stored)));
        AssetArchiveSource inPlace = AssetArchive.ReadInPlace(Archive(SharedContentVersion, otherDigest, stored), null);
        Throws<InvalidDataException>(() => inPlace.ValidateAsync().AsTask().GetAwaiter().GetResult());

        // Schema 1 has no shared content; an asset with contentPath has no entry of its own; unknown schemas fail.
        string[] shared = [index, a, Asset("b.css", "text/css", css, "a.css")];
        Throws<InvalidDataException>(() => AssetArchive.Read(Archive(AssetArchive.CurrentVersion, shared, stored)));
        Throws<InvalidDataException>(() => AssetArchive.Read(Archive(SharedContentVersion, shared, [.. stored, ("assets/b.css", css)])));
        Throws<InvalidDataException>(() => AssetArchive.Read(Archive("runic.assets.archive/3", [index, a], stored)));
        return Task.CompletedTask;
    }

    // Two frontends that share a font and a stylesheet, as in runic-sdk#1109, plus the stylesheet under another
    // media type.
    private static Dictionary<string, byte[]> WriteTwoFrontends(TemporaryDirectory directory)
    {
        byte[] font = new byte[48 * 1024];
        new Random(1109).NextBytes(font);
        string css = string.Concat(Enumerable.Range(0, 200).Select(static index => $".c{index} {{ margin: {index}px; }}\n"));
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["main/index.html"] = "<!doctype html><title>Main</title>"u8.ToArray(),
            ["settings/index.html"] = "<!doctype html><title>Settings</title>"u8.ToArray(),
            ["main/app.css"] = Encoding.UTF8.GetBytes(css),
            ["settings/app.css"] = Encoding.UTF8.GetBytes(css),
            ["notes/app.txt"] = Encoding.UTF8.GetBytes(css),
            ["main/fonts/inter.woff2"] = font,
            ["settings/fonts/inter.woff2"] = font,
        };
        foreach ((string path, byte[] content) in files)
        {
            string full = Path.Combine(directory.Path, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
        }

        return files;
    }

    private static async Task<byte[]> WriteAsync(string directory, string entryPoint = "main/index.html")
    {
        using var output = new MemoryStream();
        await AssetArchive.WriteTrustedDirectoryAsync(
            directory,
            output,
            new AssetArchiveDirectoryOptions { EntryPoint = entryPoint }).ConfigureAwait(false);
        return output.ToArray();
    }

    // What the writer of 0.21 and earlier produced: one entry per path.
    private static byte[] Schema1Archive(Dictionary<string, byte[]> files, string entryPoint)
    {
        string[] assets = files.OrderBy(static file => file.Key, StringComparer.Ordinal)
            .Select(file => Asset(
                file.Key,
                file.Key.EndsWith(".woff2", StringComparison.Ordinal) ? "font/woff2"
                    : file.Key.EndsWith(".css", StringComparison.Ordinal) ? "text/css"
                    : file.Key.EndsWith(".txt", StringComparison.Ordinal) ? "text/plain" : "text/html",
                file.Value,
                entryPoint: file.Key == entryPoint))
            .ToArray();
        return Archive(
            AssetArchive.CurrentVersion,
            assets,
            files.OrderBy(static file => file.Key, StringComparer.Ordinal).Select(static file => ("assets/" + file.Key, file.Value)).ToArray())
            .ToArray();
    }

    private static string Asset(string path, string mediaType, byte[] content, string? contentPath = null, bool entryPoint = false)
    {
        var asset = new Dictionary<string, object>
        {
            ["path"] = path,
            ["mediaType"] = mediaType,
            ["length"] = content.LongLength,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(content)),
            ["entryPoint"] = entryPoint,
            ["cacheMode"] = "Revalidate",
        };
        if (contentPath is not null)
        {
            asset["contentPath"] = contentPath;
        }

        return JsonSerializer.Serialize(asset);
    }

    private static MemoryStream Archive(string version, string[] assets, (string Name, byte[] Content)[] entries)
    {
        var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "runic-assets.json", Encoding.UTF8.GetBytes($"{{\"version\":\"{version}\",\"assets\":[{string.Join(',', assets)}]}}"));
            foreach ((string name, byte[] content) in entries)
            {
                Add(zip, name, content);
            }
        }

        archive.Position = 0;
        return archive;

        static void Add(ZipArchive zip, string name, byte[] content)
        {
            using Stream stream = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
            stream.Write(content);
        }
    }

    private static string[] ZipEntryNames(byte[] archive)
    {
        using var zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
        return zip.Entries.Select(static entry => entry.FullName).Order(StringComparer.Ordinal).ToArray();
    }

    private static byte[] ReadManifestBytes(byte[] archive)
    {
        using var zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
        using Stream stream = zip.GetEntry("runic-assets.json")!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static JsonDocument ReadManifest(byte[] archive) => JsonDocument.Parse(ReadManifestBytes(archive));

    private static AssetDescriptor Descriptor(AssetArchiveSource source, string path)
    {
        True(source.Manifest.TryGetAsset(path, out AssetDescriptor? descriptor), path);
        return descriptor!;
    }

    private static async Task<byte[]> ReadAllAsync(IAssetSource source, string path)
    {
        await using AssetReadSnapshot snapshot = await source.OpenAsync(path).ConfigureAwait(false);
        using var copy = new MemoryStream();
        await snapshot.Content.CopyToAsync(copy).ConfigureAwait(false);
        return copy.ToArray();
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}' but found '{actual}'.");
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        T[] expectedItems = [.. expected];
        T[] actualItems = [.. actual];
        if (!expectedItems.SequenceEqual(actualItems))
            throw new InvalidOperationException($"Expected [{string.Join(", ", expectedItems.Take(8))}] but found [{string.Join(", ", actualItems.Take(8))}].");
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() =>
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "runic-shared-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public void Write(string relativePath, string content)
        {
            string fullPath = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
