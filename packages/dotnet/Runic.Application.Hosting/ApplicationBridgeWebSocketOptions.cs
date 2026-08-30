using System;
using System.Collections.Generic;
using Runic.Application.Bridge;

namespace Runic.Application.Hosting;

/// <summary>Configures a bounded ASP.NET Core Application Bridge WebSocket endpoint.</summary>
public sealed record ApplicationBridgeWebSocketOptions
{
    /// <summary>Untrusted input, session, and outbound-frame limits.</summary>
    public BridgeLimits Limits { get; init; } = BridgeLimits.Default;

    /// <summary>
    /// Exact browser Origin values permitted to connect. An empty collection
    /// rejects every request that supplies an Origin header.
    /// </summary>
    public IReadOnlySet<string> AllowedOrigins { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Returns whether a request Origin is allowed by this endpoint configuration.</summary>
    public bool IsOriginAllowed(string? origin)
    {
        Validate();
        return string.IsNullOrEmpty(origin) || AllowedOrigins.Contains(origin);
    }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Limits);
        ArgumentNullException.ThrowIfNull(AllowedOrigins);
        foreach (string origin in AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) ||
                !string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.Ordinal))
            {
                throw new ArgumentException("Application Bridge allowed origins must be absolute scheme-and-authority values.", nameof(AllowedOrigins));
            }
        }
    }
}
