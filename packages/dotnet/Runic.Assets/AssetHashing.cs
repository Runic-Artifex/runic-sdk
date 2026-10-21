using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Assets;

internal static class AssetHashing
{
    internal static AssetDescriptor Describe(
        string path,
        Stream stream,
        string? mediaType,
        bool isEntryPoint,
        AssetCacheMode cacheMode)
    {
        if (!stream.CanSeek)
        {
            throw new InvalidOperationException("Asset metadata requires a seekable stream.");
        }

        long length = stream.Length;
        byte[] digest = SHA256.HashData(stream);
        return new AssetDescriptor(
            path,
            mediaType ?? AssetMediaTypes.Resolve(path),
            length,
            Convert.ToHexStringLower(digest),
            isEntryPoint,
            cacheMode);
    }

    internal static async ValueTask<AssetDescriptor> DescribeAsync(
        string path,
        Stream stream,
        string? mediaType,
        bool isEntryPoint,
        AssetCacheMode cacheMode,
        CancellationToken cancellationToken)
    {
        if (!stream.CanSeek)
        {
            throw new InvalidOperationException("Asset metadata requires a seekable stream.");
        }

        long length = stream.Length;
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new AssetDescriptor(
            path,
            mediaType ?? AssetMediaTypes.Resolve(path),
            length,
            Convert.ToHexStringLower(digest),
            isEntryPoint,
            cacheMode);
    }

    internal static async ValueTask VerifyAsync(AssetDescriptor descriptor, Stream stream, CancellationToken cancellationToken)
    {
        if (!stream.CanSeek || stream.Length != descriptor.Length)
        {
            throw new InvalidDataException($"Asset '{descriptor.RelativePath}' has an unexpected length.");
        }

        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(Convert.ToHexStringLower(digest), descriptor.Sha256))
        {
            throw new InvalidDataException($"Asset '{descriptor.RelativePath}' does not match its manifest digest.");
        }
    }

    internal static void Verify(AssetDescriptor descriptor, Stream stream)
    {
        if (!stream.CanSeek || stream.Length != descriptor.Length)
        {
            throw new InvalidDataException($"Asset '{descriptor.RelativePath}' has an unexpected length.");
        }

        byte[] digest = SHA256.HashData(stream);
        if (!StringComparer.Ordinal.Equals(Convert.ToHexStringLower(digest), descriptor.Sha256))
        {
            throw new InvalidDataException($"Asset '{descriptor.RelativePath}' does not match its manifest digest.");
        }
    }
}
