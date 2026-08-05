using System;
using System.Threading;
using System.Threading.Tasks;

namespace RunicAssets.RunicToolkit;

/// <summary>
/// Defines the Runic Assets-owned handoff that Runic Toolkit hosting will consume once
/// Toolkit contracts are independently packageable.
/// </summary>
public sealed class RunicToolkitAssetBoundary
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

        string baseUri = applicationBaseUri.AbsoluteUri.EndsWith('/', StringComparison.Ordinal)
            ? applicationBaseUri.AbsoluteUri
            : applicationBaseUri.AbsoluteUri + "/";
        EntryPoint = new Uri(new Uri(baseUri), Escape(Source.Manifest.EntryPoint.RelativePath));
    }

    /// <summary>Gets the transport-neutral source retained by Toolkit hosting.</summary>
    public IAssetSource Source { get; }

    /// <summary>Gets the escaped absolute URI for the manifest entry point.</summary>
    public Uri EntryPoint { get; }

    /// <summary>Validates the source before Toolkit starts its browser host.</summary>
    public ValueTask ValidateAsync(CancellationToken cancellationToken = default) =>
        Source.ValidateAsync(cancellationToken);

    private static string Escape(string relativePath)
    {
        string[] segments = relativePath.Split('/');
        for (int index = 0; index < segments.Length; index++)
        {
            segments[index] = Uri.EscapeDataString(segments[index]);
        }

        return string.Join('/', segments);
    }
}
