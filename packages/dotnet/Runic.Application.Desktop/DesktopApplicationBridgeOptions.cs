using System;
using Runic.Application.Bridge;

namespace Runic.Application.Desktop;

/// <summary>Configures the Application Bridge capability carried by Runic Desktop.</summary>
public sealed record DesktopApplicationBridgeOptions
{
    /// <summary>Client-to-host capability negotiated by the Desktop browser transport.</summary>
    public const string Capability = "runic.desktop.application-bridge/1";

    /// <summary>Host-to-client raw receiver owned by the Desktop browser transport.</summary>
    public const string Receiver = "__runicDesktopReceiveApplicationBridgeFrame";

    /// <summary>Gets untrusted Application Bridge frame and session limits.</summary>
    public BridgeLimits Limits { get; init; } = BridgeLimits.Default;

    internal void Validate() => ArgumentNullException.ThrowIfNull(Limits);
}
