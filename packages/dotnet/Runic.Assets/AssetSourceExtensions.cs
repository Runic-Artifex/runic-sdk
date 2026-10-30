using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Assets;

/// <summary>Views of an <see cref="IAssetSource"/>.</summary>
public static class AssetSourceExtensions
{
    /// <summary>
    /// Presents one folder of <paramref name="source"/> as a frontend of its own: its assets keep their content and
    /// metadata under paths relative to the folder, and <paramref name="entryPoint"/> in the folder is the entry point.
    /// </summary>
    /// <param name="source">The source, such as an <see cref="AssetArchiveSource"/> that holds several frontends.</param>
    /// <param name="path">The folder, relative to the source, such as <c>settings</c> or <c>windows/settings/</c>.</param>
    /// <param name="entryPoint">The entry document, relative to the folder; <c>index.html</c> by default.</param>
    /// <returns>A source that serves the folder's assets at its root.</returns>
    /// <remarks>
    /// The view takes the folder's assets from the manifest <paramref name="source"/> has when it is created, and opens
    /// them through <paramref name="source"/>. Create a new view after a live source, such as a
    /// <see cref="DevelopmentDirectoryAssetSource"/>, changes: an asset whose content changed since then fails to open
    /// with <see cref="InvalidDataException"/>. <see cref="IAssetSource.ValidateAsync"/> verifies the folder's assets
    /// only. Every host adapter serves the view as it serves any source, for example
    /// <c>RunicDesktopHostOptions.Assets = archive.Subdirectory("settings")</c>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> or <paramref name="entryPoint"/> is not a safe
    /// relative asset path (<see cref="AssetPath.Normalize"/>).</exception>
    /// <exception cref="DirectoryNotFoundException">No asset of <paramref name="source"/> is in the folder.</exception>
    /// <exception cref="FileNotFoundException">The folder has no <paramref name="entryPoint"/>.</exception>
    public static IAssetSource Subdirectory(this IAssetSource source, string path, string entryPoint = "index.html")
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(entryPoint);
        // A trailing separator names the same folder.
        string folder = path.Length > 1 && path[^1] is '/' or '\\' ? path[..^1] : path;
        return new SubdirectoryAssetSource(source, AssetPath.Normalize(folder), AssetPath.Normalize(entryPoint));
    }

    private sealed class SubdirectoryAssetSource : IAssetSource
    {
        private readonly IAssetSource _source;
        private readonly string _prefix;

        internal SubdirectoryAssetSource(IAssetSource source, string folder, string entryPoint)
        {
            _source = source;
            _prefix = folder + "/";
            var assets = new List<AssetDescriptor>();
            bool hasEntryPoint = false;
            foreach (AssetDescriptor asset in source.Manifest.Assets)
            {
                if (!asset.RelativePath.StartsWith(_prefix, StringComparison.Ordinal)) continue;
                string relative = asset.RelativePath[_prefix.Length..];
                bool isEntryPoint = StringComparer.Ordinal.Equals(relative, entryPoint);
                hasEntryPoint |= isEntryPoint;
                assets.Add(new AssetDescriptor(relative, asset.MediaType, asset.Length, asset.Sha256, isEntryPoint, asset.CacheMode));
            }

            if (assets.Count == 0)
            {
                throw new DirectoryNotFoundException($"The asset source has no assets in '{folder}'.");
            }

            if (!hasEntryPoint)
            {
                throw new FileNotFoundException(
                    $"The asset folder '{folder}' has no entry point '{entryPoint}'.",
                    _prefix + entryPoint);
            }

            Manifest = new AssetManifest(assets);
        }

        public AssetManifest Manifest { get; }

        public async ValueTask ValidateAsync(CancellationToken cancellationToken = default)
        {
            foreach (AssetDescriptor asset in Manifest.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using AssetReadSnapshot snapshot = await OpenAsync(asset.RelativePath, cancellationToken).ConfigureAwait(false);
                await AssetHashing.VerifyAsync(asset, snapshot.Content, cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask<AssetReadSnapshot> OpenAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Manifest.TryGetAsset(relativePath, out AssetDescriptor? descriptor))
            {
                throw new FileNotFoundException("The requested asset is not declared by the folder.", relativePath);
            }

            AssetReadSnapshot snapshot = await _source.OpenAsync(_prefix + descriptor.RelativePath, cancellationToken).ConfigureAwait(false);
            // A snapshot describes its bytes exactly: never present other content under this view's descriptor.
            if (!StringComparer.Ordinal.Equals(snapshot.Descriptor.Sha256, descriptor.Sha256) ||
                snapshot.Descriptor.Length != descriptor.Length)
            {
                await snapshot.DisposeAsync().ConfigureAwait(false);
                throw new InvalidDataException($"The asset '{_prefix}{descriptor.RelativePath}' changed after the folder view was created.");
            }

            return new AssetReadSnapshot(descriptor, snapshot.Content);
        }
    }
}
