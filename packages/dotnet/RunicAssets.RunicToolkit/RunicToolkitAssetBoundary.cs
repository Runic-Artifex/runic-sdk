using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RunicToolkit.Hosting;

namespace RunicAssets.RunicToolkit;

/// <summary>
/// Adapts a Runic Assets source to Runic Toolkit's stable frontend hosting contracts.
/// </summary>
public sealed class RunicToolkitAssetBoundary : IFrontendAssetProvider
{
    /// <summary>Creates a validated Toolkit asset handoff.</summary>
    public RunicToolkitAssetBoundary(IAssetSource source, Uri applicationBaseUri)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentNullException.ThrowIfNull(applicationBaseUri);
        if (!applicationBaseUri.IsAbsoluteUri
            || applicationBaseUri.Query.Length != 0
            || applicationBaseUri.Fragment.Length != 0)
        {
            throw new ArgumentException(
                "The application base URI must be absolute and cannot contain a query or fragment.",
                nameof(applicationBaseUri));
        }

        string baseUri = applicationBaseUri.AbsoluteUri.EndsWith('/')
            ? applicationBaseUri.AbsoluteUri
            : applicationBaseUri.AbsoluteUri + "/";
        EntryPoint = new Uri(new Uri(baseUri), Escape(Source.Manifest.EntryPoint.RelativePath));
        var assets = new FrontendAsset[Source.Manifest.Assets.Count];
        for (int index = 0; index < assets.Length; index++)
        {
            AssetDescriptor asset = Source.Manifest.Assets[index];
            assets[index] = new FrontendAsset(
                asset.RelativePath,
                asset.MediaType,
                asset.Length,
                asset.Sha256,
                asset.IsEntryPoint);
        }

        Manifest = new ToolkitManifest(Array.AsReadOnly(assets));
    }

    /// <summary>Gets the transport-neutral source retained by Toolkit hosting.</summary>
    public IAssetSource Source { get; }

    /// <summary>Gets the escaped absolute URI for the manifest entry point.</summary>
    public Uri EntryPoint { get; }

    /// <inheritdoc />
    public IFrontendAssetManifest Manifest { get; }

    /// <summary>Validates the source before Toolkit starts its browser host.</summary>
    public ValueTask ValidateAsync(CancellationToken cancellationToken = default) =>
        Source.ValidateAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(
        string relativePath,
        CancellationToken cancellationToken) =>
        Source.OpenReadAsync(relativePath, cancellationToken);

    private static string Escape(string relativePath)
    {
        string[] segments = relativePath.Split('/');
        for (int index = 0; index < segments.Length; index++)
        {
            segments[index] = Uri.EscapeDataString(segments[index]);
        }

        return string.Join('/', segments);
    }

    private sealed class ToolkitManifest : IFrontendAssetManifest
    {
        internal ToolkitManifest(IReadOnlyList<FrontendAsset> assets)
        {
            Assets = assets;
        }

        public string ManifestVersion => "runic-toolkit.frontend-assets/1";

        public IReadOnlyList<FrontendAsset> Assets { get; }
    }
}
