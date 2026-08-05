using System;
using System.Collections.Generic;

namespace RunicToolkit.Hosting.Build;

/// <summary>Provides an immutable, deterministically ordered frontend asset manifest.</summary>
public sealed class FrontendAssetManifest : IFrontendAssetManifest
{
    /// <summary>The manifest contract version emitted by this package.</summary>
    public const string CurrentVersion = "runic-toolkit.frontend-assets/1";

    private readonly IReadOnlyList<FrontendAsset> _assets;

    /// <summary>Initializes a manifest from an already validated deterministic asset sequence.</summary>
    /// <param name="assets">The assets in ordinal path order.</param>
    public FrontendAssetManifest(IReadOnlyList<FrontendAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        var snapshot = new FrontendAsset[assets.Count];
        for (var index = 0; index < assets.Count; index++)
        {
            snapshot[index] = assets[index]
                ?? throw new ArgumentException("Manifest assets cannot contain null entries.", nameof(assets));
        }

        _assets = Array.AsReadOnly(snapshot);
        var issues = FrontendAssetManifestValidator.Validate(this);
        if (issues.Count != 0)
        {
            throw new ArgumentException(issues[0].Message, nameof(assets));
        }
    }

    /// <inheritdoc />
    public string ManifestVersion => CurrentVersion;

    /// <inheritdoc />
    public IReadOnlyList<FrontendAsset> Assets => _assets;
}
