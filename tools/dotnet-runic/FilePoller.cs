using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

internal static class FilePoller
{
    internal static async Task WatchAsync(
        string path,
        Func<CancellationToken, Task> changed,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(changed);
        byte[]? fingerprint = Fingerprint(path);
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken)
                .ConfigureAwait(false);
            byte[]? current = Fingerprint(path);
            if (current is null
                || (fingerprint is not null && current.AsSpan().SequenceEqual(fingerprint)))
            {
                continue;
            }

            fingerprint = current;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
            await changed(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task WatchTreeAsync(
        string root,
        string searchPattern,
        Func<CancellationToken, Task> changed,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchPattern);
        ArgumentNullException.ThrowIfNull(changed);
        byte[] fingerprint = TreeFingerprint(root, searchPattern);
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken)
                .ConfigureAwait(false);
            byte[] current = TreeFingerprint(root, searchPattern);
            if (current.AsSpan().SequenceEqual(fingerprint))
            {
                continue;
            }

            fingerprint = current;
            await Task.Delay(TimeSpan.FromMilliseconds(75), cancellationToken)
                .ConfigureAwait(false);
            await changed(cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[]? Fingerprint(string path)
    {
        try
        {
            return File.Exists(path) ? SHA256.HashData(File.ReadAllBytes(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static byte[] TreeFingerprint(string root, string searchPattern)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in Directory
            .EnumerateFiles(root, searchPattern, SearchOption.AllDirectories)
            .Where(static path =>
                !path.Split(Path.DirectorySeparatorChar).Any(static segment =>
                    segment is "bin" or "obj" or "node_modules" or ".git"))
            .Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path)));
            try
            {
                hash.AppendData(File.ReadAllBytes(path));
            }
            catch (IOException)
            {
            }
        }

        return hash.GetHashAndReset();
    }
}
