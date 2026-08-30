namespace Runic.Assets.Desktop;

/// <summary>Configures Runic Assets resolution for one Desktop surface.</summary>
public sealed record DesktopAssetOptions
{
    /// <summary>Gets whether extensionless missing paths resolve to the manifest entry point.</summary>
    public bool EnableSinglePageApplicationFallback { get; init; } = true;
}
