using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;

namespace RunicAssets.CsWebUi;

/// <summary>Adapts transport-neutral Runic assets to CsWebUi delivery.</summary>
public static class CsWebUiAssetExtensions
{
    /// <summary>Preloads one source into a CsWebUi virtual file system.</summary>
    public static async ValueTask<WebUiVirtualFileSystem> ToWebUiVirtualFileSystemAsync(
        this IAssetSource source,
        WebUiVirtualFileSystemOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await source.ValidateAsync(cancellationToken).ConfigureAwait(false);

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (AssetDescriptor descriptor in source.Manifest.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ZipArchiveEntry entry = archive.CreateEntry(descriptor.RelativePath, CompressionLevel.NoCompression);
                await using Stream output = entry.Open();
                await using Stream input = await source
                    .OpenReadAsync(descriptor.RelativePath, cancellationToken)
                    .ConfigureAwait(false);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }

        buffer.Position = 0;
        options ??= new WebUiVirtualFileSystemOptions
        {
            IndexFile = source.Manifest.EntryPoint.RelativePath,
        };
        return WebUiVirtualFileSystem.FromArchive(buffer, options);
    }

    /// <summary>Preloads one source and attaches it to a CsWebUi window.</summary>
    public static async ValueTask SetRunicAssetsAsync(
        this WebUiWindow window,
        IAssetSource source,
        WebUiVirtualFileSystemOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        WebUiVirtualFileSystem fileSystem = await source
            .ToWebUiVirtualFileSystemAsync(options, cancellationToken)
            .ConfigureAwait(false);
        window.SetVirtualFileSystem(fileSystem);
    }
}
