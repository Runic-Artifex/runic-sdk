using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Assets;

// Development entry documents for development tooling only. Internal: called in production, the parameterless form
// would let an environment variable replace the application's entry document (W319-003 assets-03). A host that needs
// it gets an explicit development-only opt-in.
internal static class DevelopmentDocumentAssets
{
    // Replaces only the entry document with the CLI's bounded development bootstrap. Without the environment
    // variable, returns the original source. The local development CLI supplies RUNIC_APPLICATION_DEVELOPMENT_DOCUMENT.
    // This never adds a disk root or an HTTP proxy to the asset boundary.
    internal static IAssetSource WithDevelopmentDocument(this IAssetSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string? path = Environment.GetEnvironmentVariable("RUNIC_APPLICATION_DEVELOPMENT_DOCUMENT");
        return string.IsNullOrEmpty(path) ? source : WithDevelopmentDocument(source, path);
    }

    // Reads one explicit development HTML file as an immutable entry snapshot. Other assets retain their original
    // manifest and storage boundary.
    internal static IAssetSource WithDevelopmentDocument(this IAssetSource source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Development document must use an absolute path.", nameof(path));
        const int maximum = 1024 * 1024;
        using var file = File.OpenRead(path);
        if (file.Length > maximum) throw new InvalidDataException("Development document exceeds 1 MiB.");
        using var content = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int read;
        while ((read = file.Read(buffer)) != 0)
        {
            if (content.Length + read > maximum) throw new InvalidDataException("Development document exceeds 1 MiB.");
            content.Write(buffer, 0, read);
        }
        return new DocumentSource(source, content.ToArray());
    }

    private sealed class DocumentSource : IAssetSource
    {
        private readonly IAssetSource _source;
        private readonly byte[] _content;
        private readonly AssetDescriptor _entry;

        internal DocumentSource(IAssetSource source, byte[] content)
        {
            _source = source;
            _content = content;
            _entry = new(source.Manifest.EntryPoint.RelativePath, "text/html; charset=utf-8",
                content.Length, Convert.ToHexStringLower(SHA256.HashData(content)), true, AssetCacheMode.NoStore);
            Manifest = new(source.Manifest.Assets.Select(asset => asset.IsEntryPoint ? _entry : asset));
        }

        public AssetManifest Manifest { get; }
        public ValueTask ValidateAsync(CancellationToken cancellationToken = default) => _source.ValidateAsync(cancellationToken);
        public ValueTask<AssetReadSnapshot> OpenAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return AssetPath.Normalize(relativePath) == _entry.RelativePath
                ? ValueTask.FromResult(new AssetReadSnapshot(_entry, new MemoryStream(_content, writable: false)))
                : _source.OpenAsync(relativePath, cancellationToken);
        }
    }
}
