using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Evidence;

namespace WebUIToolkit.DependencyNotices.Acquisition;

public sealed class ContentAddressedEvidenceStore
{
    private readonly string _sha256Directory;

    public ContentAddressedEvidenceStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        _sha256Directory = Path.Combine(RootDirectory, "sha256");
        EnsureDirectorySafe(RootDirectory);
        EnsureDirectorySafe(_sha256Directory);
    }

    public string RootDirectory { get; }

    public string GetPath(string sha256)
    {
        ValidateDigest(sha256);
        return Path.Combine(_sha256Directory, sha256);
    }

    public bool Contains(string sha256) => File.Exists(GetPath(sha256));

    public async ValueTask<CacheCommitResult> CommitAsync(
        Stream content,
        string expectedSha256,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateDigest(expectedSha256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        EnsureDirectorySafe(_sha256Directory);
        string destinationPath = GetPath(expectedSha256);
        if (File.Exists(destinationPath))
        {
            EnsureExistingEntrySafe(destinationPath, maximumBytes);
            await VerifyExistingAsync(destinationPath, expectedSha256, cancellationToken).ConfigureAwait(false);
            return new CacheCommitResult(destinationPath, new FileInfo(destinationPath).Length, true);
        }

        string temporaryPath = Path.Combine(_sha256Directory, ".tmp-" + Guid.NewGuid().ToString("N"));
        long byteCount = 0;
        string actualSha256;
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (FileStream output = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                byte[] buffer = new byte[64 * 1024];
                while (true)
                {
                    int read = await content.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    byteCount = checked(byteCount + read);
                    if (byteCount > maximumBytes)
                    {
                        throw new AcquisitionException(
                            NoticeDiagnosticCodes.AcquisitionSizeLimit,
                            $"Acquired evidence exceeded the configured {maximumBytes} byte limit.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            actualSha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(expectedSha256, actualSha256, StringComparison.Ordinal))
            {
                throw new AcquisitionException(
                    NoticeDiagnosticCodes.AcquisitionDigestMismatch,
                    $"Acquired evidence digest mismatch. Expected '{expectedSha256}', actual '{actualSha256}'.");
            }

            try
            {
                File.Move(temporaryPath, destinationPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                EnsureExistingEntrySafe(destinationPath, maximumBytes);
                await VerifyExistingAsync(destinationPath, expectedSha256, cancellationToken).ConfigureAwait(false);
                File.Delete(temporaryPath);
                return new CacheCommitResult(destinationPath, byteCount, true);
            }

            return new CacheCommitResult(destinationPath, byteCount, false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async ValueTask VerifyExistingAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 8;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                string actualSha256 = Convert.ToHexStringLower(actual);
                if (!string.Equals(expectedSha256, actualSha256, StringComparison.Ordinal))
                {
                    throw new AcquisitionException(
                        NoticeDiagnosticCodes.AcquisitionDigestMismatch,
                        $"The existing cache entry for '{expectedSha256}' contains different bytes.");
                }

                return;
            }
            catch (IOException) when (attempt < maximumAttempts - 1)
            {
                // A concurrent atomic move can make the destination visible before Windows
                // releases its final sharing handle. Preserve verification and retry briefly.
                int delayMilliseconds = Math.Min(10 << attempt, 160);
                await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void EnsureExistingEntrySafe(string path, long maximumBytes)
    {
        FileInfo file = new(path);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new AcquisitionException(
                NoticeDiagnosticCodes.AcquisitionOriginBlocked,
                "An existing evidence cache entry cannot be a symbolic link or reparse point.");
        }

        if (file.Length > maximumBytes)
        {
            throw new AcquisitionException(
                NoticeDiagnosticCodes.AcquisitionSizeLimit,
                $"An existing evidence cache entry exceeded the configured {maximumBytes} byte limit.");
        }
    }

    private static void ValidateDigest(string digest)
    {
        if (!EvidenceDigest.IsCanonicalSha256(digest))
        {
            throw new ArgumentException("A lowercase canonical SHA-256 digest is required.", nameof(digest));
        }
    }

    private static void EnsureDirectorySafe(string directory)
    {
        if (Directory.Exists(directory))
        {
            FileAttributes attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new AcquisitionException(
                    NoticeDiagnosticCodes.AcquisitionOriginBlocked,
                    "The evidence cache directory cannot be a symbolic link or reparse point.");
            }
        }
        else
        {
            Directory.CreateDirectory(directory);
        }
    }
}

public readonly record struct CacheCommitResult(string Path, long ByteCount, bool WasAlreadyCached);
