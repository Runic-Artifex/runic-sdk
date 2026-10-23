using System.Reflection;
using Runic.Assets;
using Runic.Assets.Desktop;
using Runic.Desktop;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// The frontend that the build embedded with <c>RunicApplicationFrontendDelivery=Embedded</c> (I-027, D039.10): the
/// <c>Runic.Application.FrontendArchive</c> runtime option names the manifest resource of the entry assembly. The
/// Desktop and WPF hosts serve it in place of <c>www/</c>.
/// </summary>
internal static class FrontendArchive
{
    internal const string RuntimeOption = "Runic.Application.FrontendArchive";

    // Read once per process, when a window first needs it. A missing or invalid resource fails each open.
    private static readonly Lazy<AssetArchiveSource?> Embedded = new(LoadEmbedded);

    /// <summary>The build's archive, or <see langword="null"/> when the build embedded none.</summary>
    internal static IAssetSource? FromBuild => AppContext.GetData(RuntimeOption) is string { Length: > 0 } ? Embedded.Value : null;

    private static AssetArchiveSource? LoadEmbedded()
    {
        if (AppContext.GetData(RuntimeOption) is not string { Length: > 0 } resource) return null;
        var assembly = Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException($"The {RuntimeOption} runtime option names '{resource}', but the process has no entry assembly to read it from.");
        return AssetArchive.ReadEmbedded(assembly, resource);
    }

    /// <summary>
    /// The content of a window that serves <paramref name="source"/> and opens <paramref name="startPage"/>: the root
    /// serves the start page, every declared path is served as itself, and extensionless paths fall back to the start
    /// page, as the asset routing defaults do.
    /// </summary>
    internal static DesktopContent CreateContent(IAssetSource source, string startPage)
    {
        ArgumentNullException.ThrowIfNull(source);
        var page = AssetPath.Normalize(string.IsNullOrEmpty(startPage) ? "index.html" : startPage);
        if (!source.Manifest.TryGetAsset(page, out _))
            throw new FileNotFoundException($"The frontend archive has no start page '{page}'.", page);
        var served = StringComparer.Ordinal.Equals(source.Manifest.EntryPoint.RelativePath, page) ? source : new StartPageSource(source, page);
        return new DesktopContent.Handler(served.ToDesktopContentHandler());
    }

    // The source with another declared asset as its entry point. Snapshots are the source's own: the Desktop adapter
    // compares their entity tag and length with the manifest's, which do not change.
    private sealed class StartPageSource(IAssetSource source, string startPage) : IAssetSource
    {
        public AssetManifest Manifest { get; } = new(source.Manifest.Assets.Select(asset => new AssetDescriptor(
            asset.RelativePath, asset.MediaType, asset.Length, asset.Sha256,
            isEntryPoint: StringComparer.Ordinal.Equals(asset.RelativePath, startPage), asset.CacheMode)).ToArray());

        public ValueTask ValidateAsync(CancellationToken cancellationToken = default) => source.ValidateAsync(cancellationToken);

        public ValueTask<AssetReadSnapshot> OpenAsync(string relativePath, CancellationToken cancellationToken = default) =>
            source.OpenAsync(relativePath, cancellationToken);
    }
}
