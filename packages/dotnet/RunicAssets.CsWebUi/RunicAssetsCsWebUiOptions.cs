using System;

namespace RunicAssets.CsWebUi;

/// <summary>Configures Runic Assets routing through a CS-WebUI custom file handler.</summary>
public sealed class RunicAssetsCsWebUiOptions
{
    /// <summary>
    /// Gets or sets whether extensionless unknown paths resolve to the current manifest entry point.
    /// </summary>
    public bool EnableSinglePageApplicationFallback { get; set; }

    /// <summary>Gets or sets the maximum complete raw HTTP response size.</summary>
    public int MaxResponseBytes { get; set; } = int.MaxValue;
}
