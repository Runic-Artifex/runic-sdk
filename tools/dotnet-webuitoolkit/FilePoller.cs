using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.DotNet.WebUIToolkit;

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
}
