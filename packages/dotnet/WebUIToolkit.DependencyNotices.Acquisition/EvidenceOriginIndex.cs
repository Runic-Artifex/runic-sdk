using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.DependencyNotices.Evidence;

namespace WebUIToolkit.DependencyNotices.Acquisition;

public sealed record EvidenceOriginIndexEntry(Uri Origin, string Sha256);

public static class EvidenceOriginIndex
{
    public const int SchemaVersion = 1;
    private const int MaximumReplaceAttempts = 8;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public static byte[] Serialize(IEnumerable<EvidenceOriginIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        EvidenceOriginIndexEntry[] canonical = entries.Select(ValidateAndCanonicalize).ToArray();
        foreach (IGrouping<string, EvidenceOriginIndexEntry> group in canonical.GroupBy(
            static entry => entry.Origin.AbsoluteUri,
            StringComparer.Ordinal))
        {
            if (group.Select(static entry => entry.Sha256).Distinct(StringComparer.Ordinal).Skip(1).Any())
            {
                throw new ArgumentException(
                    $"Origin '{OriginPolicy.Sanitize(group.First().Origin)}' maps to multiple digests.",
                    nameof(entries));
            }
        }

        EvidenceOriginIndexEntry[] ordered = canonical
            .GroupBy(static entry => entry.Origin.AbsoluteUri, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static entry => entry.Origin.AbsoluteUri, StringComparer.Ordinal)
            .ToArray();

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.Default,
            Indented = true,
            NewLine = "\n",
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteStartArray("origins");
            foreach (EvidenceOriginIndexEntry entry in ordered)
            {
                writer.WriteStartObject();
                writer.WriteString("origin", entry.Origin.AbsoluteUri);
                writer.WriteString("sha256", entry.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        // Utf8JsonWriter has no newline policy switch. Its indentation is deterministic;
        // append the repository canonical LF terminator explicitly.
        byte[] result = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(result);
        result[^1] = (byte)'\n';
        return result;
    }

    public static async ValueTask WriteAsync(
        string path,
        IEnumerable<EvidenceOriginIndexEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes = Serialize(entries);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is null)
        {
            throw new ArgumentException("The origin index path must have a parent directory.", nameof(path));
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, ".origin-index-" + Guid.NewGuid().ToString("N"));
        SemaphoreSlim pathLock = PathLocks.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (FileStream output = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (await FileMatchesAsync(fullPath, bytes, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    File.Move(temporaryPath, fullPath, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < MaximumReplaceAttempts - 1)
                {
                    await DelayForContentionAsync(attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException) when (attempt < MaximumReplaceAttempts - 1)
                {
                    await DelayForContentionAsync(attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            pathLock.Release();
        }
    }

    private static async ValueTask<bool> FileMatchesAsync(
        string path,
        ReadOnlyMemory<byte> expected,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        FileInfo file = new(path);
        if (file.Length != expected.Length)
        {
            return false;
        }

        await using FileStream input = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[4096];
        int offset = 0;
        while (offset < expected.Length)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, expected.Length - offset)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0 || !buffer.AsSpan(0, read).SequenceEqual(expected.Span.Slice(offset, read)))
            {
                return false;
            }

            offset += read;
        }

        return input.ReadByte() == -1;
    }

    private static ValueTask DelayForContentionAsync(int attempt, CancellationToken cancellationToken) =>
        new(Task.Delay(TimeSpan.FromMilliseconds(5 << attempt), cancellationToken));

    private static EvidenceOriginIndexEntry ValidateAndCanonicalize(EvidenceOriginIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.Origin);
        if (!entry.Origin.IsAbsoluteUri || !string.IsNullOrEmpty(entry.Origin.UserInfo))
        {
            throw new ArgumentException("Origin index entries require absolute credential-free URIs.", nameof(entry));
        }

        if (!EvidenceDigest.IsCanonicalSha256(entry.Sha256))
        {
            throw new ArgumentException("Origin index entries require canonical SHA-256 digests.", nameof(entry));
        }

        return new EvidenceOriginIndexEntry(OriginPolicy.SanitizeUri(entry.Origin), entry.Sha256);
    }
}
