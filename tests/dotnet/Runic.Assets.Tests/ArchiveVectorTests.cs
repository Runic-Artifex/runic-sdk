using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using TUnit.Core;

namespace Runic.Assets.Tests;

// The archive vectors of specs/assets/vectors (runic.assets.vector/1) against the .NET reader, in both of the reader
// rules' modes: eager (AssetArchive.Read) and lazy (the ReadEmbedded reader, then every asset opened). A valid case
// must report the expected assets; an invalid one must fail with InvalidDataException. The .NET reader reports no
// error codes, so a case's code is not compared (specs/assets/vectors/README.md).
internal sealed class ArchiveVectorTests
{
    // The project file copies specs/assets/vectors here.
    private static readonly string s_vectors = Path.Combine(AppContext.BaseDirectory, "vectors");

    [Test, DisplayName("archive vectors: eager reads (AssetArchive.Read)")]
    public async Task EagerVectors() => await RunAsync(lazy: false).ConfigureAwait(false);

    [Test, DisplayName("archive vectors: lazy reads (ReadEmbedded's reader, every asset opened)")]
    public async Task LazyVectors() => await RunAsync(lazy: true).ConfigureAwait(false);

    // The packer vectors' files, written to a directory and packed again by the current writer in each of the packer's
    // modes, read in both reading modes and report what those vectors expect.
    [Test, DisplayName("the current packer's archives of the packer vectors' files read as expected")]
    public async Task CurrentPackerArchives()
    {
        List<Vector> vectors = Cases()
            .Where(static vector => vector.Id.StartsWith("assets.packer.", StringComparison.Ordinal))
            .ToList();
        Equal(2, vectors.Count);
        foreach (Vector vector in vectors)
        {
            string name = vector.Id;
            JsonElement expect = vector.Expect;
            using var temporary = new TemporaryDirectory();
            string directory = temporary.Path;
            AssetArchiveSource original = AssetArchive.Read(new MemoryStream(vector.Input, writable: false));
            foreach (AssetDescriptor asset in original.Manifest.Assets)
            {
                string file = Path.Combine(directory, asset.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, await ReadAllAsync(original, asset.RelativePath).ConfigureAwait(false)).ConfigureAwait(false);
            }

            var archives = new List<byte[]>();
            using (var written = new MemoryStream())
            {
                await AssetArchive.WriteTrustedDirectoryAsync(directory, written).ConfigureAwait(false);
                archives.Add(written.ToArray());
            }

            if (OperatingSystem.IsLinux())
            {
                using var written = new MemoryStream();
                await AssetArchive.WriteDirectoryAsync(directory, written).ConfigureAwait(false);
                archives.Add(written.ToArray());
            }

            foreach (byte[] bytes in archives)
            {
                foreach (bool lazy in new[] { false, true })
                {
                    AssetArchiveSource source = lazy
                        ? AssetArchive.ReadInPlace(new MemoryStream(bytes, writable: false), null)
                        : AssetArchive.Read(new MemoryStream(bytes, writable: false));
                    string? difference = Compare(expect, source.Manifest);
                    True(difference is null, $"{name} packed again ({(lazy ? "lazy" : "eager")}): {difference}");
                    foreach (AssetDescriptor asset in source.Manifest.Assets)
                    {
                        SequenceEqual(
                            File.ReadAllBytes(Path.Combine(directory, asset.RelativePath)),
                            await ReadAllAsync(source, asset.RelativePath).ConfigureAwait(false));
                    }
                }
            }
        }
    }

    private static async Task RunAsync(bool lazy)
    {
        List<Vector> cases = Cases().ToList();
        True(cases.Count > 0, $"No archive vectors in {s_vectors}.");
        var failures = new List<string>();
        foreach (Vector vector in cases)
        {
            JsonElement expect = lazy && vector.ExpectLazy is JsonElement lazyExpect ? lazyExpect : vector.Expect;
            bool valid = expect.GetProperty("result").GetString() switch
            {
                "valid" => true,
                "invalid" => false,
                string other => throw new InvalidOperationException($"{vector.Id}: unknown result '{other}'."),
                null => throw new InvalidOperationException($"{vector.Id}: no result."),
            };
            try
            {
                AssetArchiveSource source = lazy
                    ? AssetArchive.ReadInPlace(new MemoryStream(vector.Input, writable: false), null)
                    : AssetArchive.Read(new MemoryStream(vector.Input, writable: false));
                foreach (AssetDescriptor asset in source.Manifest.Assets)
                {
                    byte[] content = await ReadAllAsync(source, asset.RelativePath).ConfigureAwait(false);
                    if (content.Length != asset.Length)
                    {
                        throw new InvalidOperationException($"'{asset.RelativePath}' served {content.Length} bytes, not {asset.Length}.");
                    }

                    if (!lazy && Convert.ToHexStringLower(SHA256.HashData(content)) != asset.Sha256)
                    {
                        throw new InvalidOperationException($"'{asset.RelativePath}' was served with another digest.");
                    }
                }

                if (!valid)
                {
                    failures.Add($"{vector.Id}: accepted instead of {expect.GetProperty("error").GetString()}");
                }
                else if (Compare(expect, source.Manifest) is string difference)
                {
                    failures.Add($"{vector.Id}: {difference}");
                }
            }
            catch (InvalidDataException exception)
            {
                if (valid)
                {
                    failures.Add($"{vector.Id}: rejected: {exception.Message}");
                }
            }
            catch (Exception exception)
            {
                failures.Add($"{vector.Id}: {exception.GetType().Name} instead of InvalidDataException: {exception.Message}");
            }
        }

        True(failures.Count == 0, $"{failures.Count} of {cases.Count} archive vectors failed ({(lazy ? "lazy" : "eager")}):\n" + string.Join('\n', failures));
    }

    // The assets a valid case expects, member by member, in order.
    private static string? Compare(JsonElement expect, AssetManifest manifest)
    {
        string[] expected = expect.GetProperty("assets").EnumerateArray().Select(static asset =>
            $"{asset.GetProperty("path").GetString()}|{asset.GetProperty("mediaType").GetString()}|{asset.GetProperty("length").GetInt64()}|"
            + $"{asset.GetProperty("sha256").GetString()}|{asset.GetProperty("entryPoint").GetBoolean()}|{asset.GetProperty("cacheMode").GetString()}").ToArray();
        string[] actual = manifest.Assets.Select(static asset =>
            $"{asset.RelativePath}|{asset.MediaType}|{asset.Length}|{asset.Sha256}|{asset.IsEntryPoint}|{asset.CacheMode}").ToArray();
        return expected.SequenceEqual(actual, StringComparer.Ordinal)
            ? null
            : $"reported [{string.Join(", ", actual)}] instead of [{string.Join(", ", expected)}]";
    }

    private static IEnumerable<Vector> Cases()
    {
        string[] files = Directory.GetFiles(s_vectors, "*.json");
        True(files.Length > 0, $"No archive vector files in {s_vectors}.");
        foreach (string file in files.Order(StringComparer.Ordinal))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));
            Equal("runic.assets.vector/1", document.RootElement.GetProperty("format").GetString());
            foreach (JsonElement vector in document.RootElement.GetProperty("cases").EnumerateArray())
            {
                Equal("read", vector.GetProperty("operation").GetString());
                JsonElement input = vector.GetProperty("input");
                Equal("base64", input.GetProperty("encoding").GetString());
                yield return new Vector(
                    vector.GetProperty("id").GetString()!,
                    Convert.FromBase64String(input.GetProperty("value").GetString()!),
                    vector.GetProperty("expect").Clone(),
                    vector.TryGetProperty("expectLazy", out JsonElement lazy) ? lazy.Clone() : null);
            }
        }
    }

    private static async Task<byte[]> ReadAllAsync(AssetArchiveSource source, string path)
    {
        await using AssetReadSnapshot snapshot = await source.OpenAsync(path).ConfigureAwait(false);
        using var copy = new MemoryStream();
        await snapshot.Content.CopyToAsync(copy).ConfigureAwait(false);
        return copy.ToArray();
    }

    private sealed record Vector(string Id, byte[] Input, JsonElement Expect, JsonElement? ExpectLazy);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("runic-vectors-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
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
            throw new InvalidOperationException($"Expected {expectedItems.Length} items but found {actualItems.Length}, or they differ.");
    }
}
