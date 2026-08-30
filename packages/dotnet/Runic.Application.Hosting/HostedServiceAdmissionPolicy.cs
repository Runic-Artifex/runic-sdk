using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Net;

namespace Runic.Application.Hosting;

/// <summary>
/// Declares the initial C#-owned admission boundary for a hosted Runic service.
/// </summary>
public sealed class HostedServiceAdmissionPolicy
{
    /// <summary>The only initial browser authentication flow.</summary>
    public const string AuthenticationFlow = "oidc-authorization-code";
    /// <summary>The encrypted, host-only session cookie issued by the C# service.</summary>
    public const string SessionCookieName = "__Host-runic-session";
    /// <summary>The only initial browser session carrier.</summary>
    public const string SessionCarrier = "encrypted-host-only-cookie";
    /// <summary>The required request header for unsafe same-origin service requests.</summary>
    public const string AntiforgeryHeaderName = "X-Runic-CSRF";
    /// <summary>The C#-owned public service route prefix.</summary>
    public const string ServiceRoutePrefix = "/runic/service";
    /// <summary>The C#-owned OpenID Connect callback route.</summary>
    public const string OidcCallbackRoute = "/signin-oidc";
    /// <summary>The fixed public TLS termination role.</summary>
    public const string TlsTerminator = "trusted-reverse-proxy";
    /// <summary>The separate frontend process role.</summary>
    public const string FrontendProcess = "sveltekit-ssr";
    /// <summary>The unsafe-request origin rule.</summary>
    public const string UnsafeRequestOriginPolicy = "exact-public-origin";
    /// <summary>The sole service identity, session, endpoint, and policy owner.</summary>
    public const string ServicePolicyOwner = "csharp";
    /// <summary>Gets whether a frontend may only forward the opaque session cookie.</summary>
    public const bool FrontendMayForwardOpaqueCookieOnly = true;
    /// <summary>Gets whether the W20 WebSocket boundary remains local-only.</summary>
    public const bool W20WebSocketRemainsLocalOnly = true;

    private HostedServiceAdmissionPolicy(Uri publicOrigin, IReadOnlySet<IPAddress> trustedProxyAddresses)
    {
        PublicOrigin = publicOrigin;
        TrustedProxyAddresses = trustedProxyAddresses;
    }

    /// <summary>Gets the single public HTTPS origin served by the trusted reverse proxy.</summary>
    public Uri PublicOrigin { get; }
    /// <summary>Gets the only proxy addresses permitted to supply forwarded scheme and host headers.</summary>
    public IReadOnlySet<IPAddress> TrustedProxyAddresses { get; }

    /// <summary>
    /// Creates the initial OIDC-code-flow, encrypted-cookie admission policy.
    /// Browser bearer tokens are intentionally not an alternative carrier.
    /// </summary>
    public static HostedServiceAdmissionPolicy CreateInitial(
        Uri publicOrigin,
        IReadOnlySet<IPAddress> trustedProxyAddresses)
    {
        ArgumentNullException.ThrowIfNull(publicOrigin);
        ArgumentNullException.ThrowIfNull(trustedProxyAddresses);
        ValidatePublicOrigin(publicOrigin);
        FrozenSet<IPAddress> trustedProxySnapshot = trustedProxyAddresses.ToFrozenSet();
        ValidateTrustedProxies(trustedProxySnapshot);
        return new HostedServiceAdmissionPolicy(publicOrigin, trustedProxySnapshot);
    }

    private static void ValidatePublicOrigin(Uri publicOrigin)
    {
        if (!publicOrigin.IsAbsoluteUri ||
            !string.Equals(publicOrigin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(publicOrigin.UserInfo) ||
            !string.Equals(publicOrigin.AbsoluteUri, publicOrigin.GetLeftPart(UriPartial.Authority) + "/", StringComparison.Ordinal))
        {
            throw new ArgumentException("The hosted service origin must be one HTTPS scheme-and-authority value.", nameof(publicOrigin));
        }
    }

    private static void ValidateTrustedProxies(FrozenSet<IPAddress> trustedProxyAddresses)
    {
        if (trustedProxyAddresses.Count == 0 ||
            trustedProxyAddresses.Contains(IPAddress.Any) ||
            trustedProxyAddresses.Contains(IPAddress.IPv6Any))
        {
            throw new ArgumentException("Hosted service forwarded headers require explicit trusted proxy addresses.", nameof(trustedProxyAddresses));
        }
    }
}
