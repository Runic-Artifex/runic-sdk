namespace Runic.Assets;

/// <summary>
/// Selects how a host adapter maps request paths to manifest assets. The Runic Assets host
/// adapters share these rules and defaults, so one archive routes identically on every host.
/// Disable both options for exact routing, where only declared manifest paths resolve.
/// </summary>
public sealed record AssetRoutingOptions
{
    internal static AssetRoutingOptions Default { get; } = new();

    /// <summary>
    /// Gets whether an empty path or <c>/</c> resolves to the manifest entry point.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool ServeEntryPointAtRoot { get; init; } = true;

    /// <summary>
    /// Gets whether a missing path whose last segment has no file extension resolves to the
    /// manifest entry point, so client-side routes survive a reload. Defaults to <see langword="true"/>.
    /// </summary>
    public bool EnableSinglePageApplicationFallback { get; init; } = true;
}
