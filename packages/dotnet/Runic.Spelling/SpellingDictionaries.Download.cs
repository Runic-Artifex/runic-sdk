using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Runic.Spelling;

/// <summary>How far a download is.</summary>
/// <param name="BytesReceived">The bytes received so far, of every file.</param>
/// <param name="TotalBytes">The bytes of every file together (<see cref="SpellingCatalogEntry.Size"/>).</param>
public readonly record struct SpellingDownloadProgress(long BytesReceived, long TotalBytes);

public sealed partial class SpellingDictionaries
{
    // The package's own client: a generic User-Agent, no cookies, and only the catalogue's addresses (no redirects).
    private static readonly Lazy<HttpClient> s_client = new(() =>
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Runic.Spelling", "1"));
        return client;
    });

    /// <summary>
    /// Downloads <paramref name="entry"/> into <see cref="DownloadDirectory"/>: its files from the catalog's addresses,
    /// each checked against its size and SHA-256, then moved into place at once, with its license texts and readme beside
    /// it.
    /// </summary>
    /// <param name="entry">An entry of <see cref="Catalog"/>, such as <see cref="SpellingAvailability.Download"/>.</param>
    /// <param name="progress">Told the bytes received, after each read.</param>
    /// <param name="cancellationToken">Stops the download; nothing of it stays installed.</param>
    /// <returns>The installed dictionary. A dictionary of the same name installed before is replaced.</returns>
    /// <exception cref="ArgumentException"><paramref name="entry"/> is not of <see cref="Catalog"/>.</exception>
    /// <exception cref="InvalidDataException">A file's size or SHA-256 is not the catalog's; nothing is installed.</exception>
    /// <exception cref="HttpRequestException">A file could not be downloaded; nothing is installed.</exception>
    /// <exception cref="IOException">The dictionary could not be written; nothing is installed.</exception>
    public async Task<SpellingDictionary> DownloadAsync(SpellingCatalogEntry entry, IProgress<SpellingDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!Catalog.Contains(entry)) throw new ArgumentException($"{entry.Name} is not an entry of this catalogue.", nameof(entry));
        foreach (var file in entry.Files)
        {
            if (!SpellingCatalog.IsAllowed(file.Url, _allowLoopback)) throw new ArgumentException($"{file.Url} is not an HTTPS address.", nameof(entry));
        }
        var target = Path.GetFullPath(Path.Combine(DownloadDirectory, entry.Name));
        var gate = s_installing.GetOrAdd(target, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PrivateFiles.CreateDirectory(DownloadDirectory);
            RemoveAbandoned();
            var staging = Path.Combine(DownloadDirectory, $".download-{entry.Name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                long received = 0;
                progress?.Report(new(0, entry.Size));
                foreach (var file in entry.Files)
                {
                    await DownloadFileAsync(file, Path.Combine(staging, file.InstalledName(entry.Name)), read =>
                    {
                        received += read;
                        progress?.Report(new(received, entry.Size));
                    }, cancellationToken).ConfigureAwait(false);
                }
                WriteSource(entry, Path.Combine(staging, "source.json"));
                cancellationToken.ThrowIfCancellationRequested();
                Install(staging, target);
            }
            catch
            {
                TryDelete(staging);
                throw;
            }
            return new(entry.Name, SpellingDictionarySource.Downloaded, Path.Combine(target, entry.Name + ".dic"), Path.Combine(target, entry.Name + ".aff"))
            {
                InstalledFiles = InstalledDictionaryCheck.Expected(target, entry.Name, Catalog),
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DownloadFileAsync(SpellingCatalogFile file, string path, Action<int> received, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        using var response = await (_httpClient ?? s_client.Value).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long length = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                length += read;
                if (length > file.Size) throw new InvalidDataException($"{file.Url} is larger than the catalogue's {file.Size} bytes.");
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received(read);
            }
            if (length != file.Size) throw new InvalidDataException($"{file.Url} has {length} bytes, not the catalogue's {file.Size}.");
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual != file.Sha256) throw new InvalidDataException($"{file.Url} has the SHA-256 {actual}, not the catalogue's {file.Sha256}.");
            // On the disk before the folder is moved into place, so a crash cannot leave a shorter file under its name.
            target.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Where the files came from, beside them: the catalogue's entry.
    private static void WriteSource(SpellingCatalogEntry entry, string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("tag", entry.LanguageTag);
        writer.WriteString("name", entry.Name);
        writer.WriteString("license", entry.License);
        writer.WriteString("repository", entry.Source.ToString());
        writer.WriteString("commit", entry.Commit);
        writer.WriteStartArray("files");
        foreach (var file in entry.Files)
        {
            writer.WriteStartObject();
            writer.WriteString("file", file.InstalledName(entry.Name));
            writer.WriteString("url", file.Url.ToString());
            writer.WriteString("sha256", file.Sha256);
            writer.WriteNumber("size", file.Size);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    // The verified folder takes the dictionary's place in one move; one installed before steps aside first and comes back
    // if the move fails.
    private void Install(string staging, string target)
    {
        string? previous = null;
        if (Directory.Exists(target))
        {
            previous = Path.Combine(DownloadDirectory, $".replaced-{Path.GetFileName(target)}-{Guid.NewGuid():N}");
            Directory.Move(target, previous);
        }
        try
        {
            Directory.Move(staging, target);
        }
        catch when (previous is not null)
        {
            Directory.Move(previous, target);
            throw;
        }
        if (previous is not null) TryDelete(previous);
    }

    // Folders of downloads that stopped with the process, a day old.
    private void RemoveAbandoned()
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(DownloadDirectory, ".*"))
            {
                var name = Path.GetFileName(folder);
                if ((name.StartsWith(".download-", StringComparison.Ordinal) || name.StartsWith(".replaced-", StringComparison.Ordinal)) &&
                    Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-1))
                    TryDelete(folder);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
