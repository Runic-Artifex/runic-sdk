using Runic.Application.Bridge;
using Runic.Assets;
using CsWebUi;

namespace Runic.Application.CsWebUi;

/// <summary>Internal options for the experimental direct-route Window Bridge host.</summary>
internal sealed record CsWebUiWindowBridgeHostOptions
{
    internal required IAssetSource Assets { get; init; }
    internal required Func<IWindowBridgeTransport, WindowBridgeSession> CreateWindowSession { get; init; }
    internal WebUiBrowser Browser { get; init; } = WebUiBrowser.Any;
    internal bool OpenWindow { get; init; } = true;
    internal string Title { get; init; } = "Runic Application";
    internal BridgeLimits Limits { get; init; } = BridgeLimits.Default;
    internal int MaxAssetBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>Maximum time native shutdown waits while cancelled window work drains.</summary>
    internal TimeSpan NativeCloseDrainTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
