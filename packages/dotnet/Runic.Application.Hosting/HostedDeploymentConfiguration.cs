using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace Runic.Application.Hosting;

/// <summary>Loads the explicit, ejectable topology for the initial hosted Runic profile.</summary>
public sealed class HostedDeploymentConfiguration
{
    /// <summary>The configuration section owned by the C# hosted application.</summary>
    public const string SectionName = "Runic:HostedDeployment";
    /// <summary>The service-only health endpoint used by the trusted proxy.</summary>
    public const string HealthPath = "/runic/health";
    /// <summary>The service-only readiness endpoint used by the trusted proxy.</summary>
    public const string ReadinessPath = "/runic/ready";
    /// <summary>The only frontend owner of the deployed static asset output.</summary>
    public const string StaticAssetsOwner = "sveltekit";

    private HostedDeploymentConfiguration(
        Uri publicOrigin,
        FrozenSet<IPAddress> trustedProxyAddresses,
        Uri serviceUpstream,
        Uri frontendUpstream,
        string staticAssetsPath,
        Uri oidcAuthority,
        string oidcClientId,
        string oidcClientSecret)
    {
        PublicOrigin = publicOrigin;
        TrustedProxyAddresses = trustedProxyAddresses;
        ServiceUpstream = serviceUpstream;
        FrontendUpstream = frontendUpstream;
        StaticAssetsPath = staticAssetsPath;
        OidcAuthority = oidcAuthority;
        OidcClientId = oidcClientId;
        OidcClientSecret = oidcClientSecret;
    }

    /// <summary>Gets the single HTTPS origin owned by the trusted reverse proxy.</summary>
    public Uri PublicOrigin { get; }
    /// <summary>Gets the proxy IP snapshot trusted to forward public host and scheme.</summary>
    public IReadOnlySet<IPAddress> TrustedProxyAddresses { get; }
    /// <summary>Gets the C# service's private HTTP upstream URL.</summary>
    public Uri ServiceUpstream { get; }
    /// <summary>Gets the separate SvelteKit SSR process's private HTTP upstream URL.</summary>
    public Uri FrontendUpstream { get; }
    /// <summary>Gets the relative SvelteKit static-output path in the ejected deployment.</summary>
    public string StaticAssetsPath { get; }
    /// <summary>Gets the HTTPS OIDC authority configured for the C# service.</summary>
    public Uri OidcAuthority { get; }
    /// <summary>Gets the C# OIDC client identifier.</summary>
    public string OidcClientId { get; }
    /// <summary>Gets the injected C# OIDC client secret. Never place this value in deployment artifacts.</summary>
    public string OidcClientSecret { get; }

    /// <summary>
    /// Loads the initial hosted topology. Every value is required so a deployment
    /// cannot silently fall back to a developer-machine endpoint or credential.
    /// </summary>
    public static HostedDeploymentConfiguration Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Uri publicOrigin = RequireHttpsOrigin(configuration, "PublicOrigin");
        FrozenSet<IPAddress> proxies = RequireProxyAddresses(configuration);
        Uri serviceUpstream = RequirePrivateHttpOrigin(configuration, "ServiceUpstream");
        Uri frontendUpstream = RequirePrivateHttpOrigin(configuration, "FrontendUpstream");
        if (serviceUpstream == frontendUpstream)
            throw new InvalidOperationException("The C# service and SvelteKit frontend upstreams must be distinct.");
        string staticAssetsPath = RequireRelativePath(configuration, "StaticAssetsPath");
        Uri oidcAuthority = RequireHttpsOrigin(configuration, "OidcAuthority");
        string oidcClientId = RequireValue(configuration, "OidcClientId");
        string oidcClientSecret = RequireValue(configuration, "OidcClientSecret");
        return new HostedDeploymentConfiguration(
            publicOrigin,
            proxies,
            serviceUpstream,
            frontendUpstream,
            staticAssetsPath,
            oidcAuthority,
            oidcClientId,
            oidcClientSecret);
    }

    /// <summary>Builds the C# admission policy from the deployment-owned public topology.</summary>
    public HostedServiceAdmissionPolicy CreateAdmissionPolicy() =>
        HostedServiceAdmissionPolicy.CreateInitial(PublicOrigin, TrustedProxyAddresses);

    private static FrozenSet<IPAddress> RequireProxyAddresses(IConfiguration configuration)
    {
        string[] values = RequireValue(configuration, "TrustedProxyAddresses")
            .Split(',', StringSplitOptions.None)
            .Select(value => value.Trim())
            .ToArray();
        if (values.Length == 0 || values.Any(string.IsNullOrEmpty) || values.Any(value => !IPAddress.TryParse(value, out _)))
            throw new InvalidOperationException($"{SectionName}:TrustedProxyAddresses must be a comma-separated list of explicit IP addresses.");
        FrozenSet<IPAddress> addresses = values.Select(IPAddress.Parse).ToFrozenSet();
        if (addresses.Count != values.Length || addresses.Contains(IPAddress.Any) || addresses.Contains(IPAddress.IPv6Any))
            throw new InvalidOperationException($"{SectionName}:TrustedProxyAddresses must contain distinct non-wildcard addresses.");
        return addresses;
    }

    private static Uri RequireHttpsOrigin(IConfiguration configuration, string key)
    {
        Uri value = RequireAbsoluteUri(configuration, key);
        if (!string.Equals(value.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{SectionName}:{key} must be an HTTPS origin.");
        return value;
    }

    private static Uri RequirePrivateHttpOrigin(IConfiguration configuration, string key)
    {
        Uri value = RequireAbsoluteUri(configuration, key);
        if (!string.Equals(value.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{SectionName}:{key} must be a private HTTP origin behind the TLS proxy.");
        return value;
    }

    private static Uri RequireAbsoluteUri(IConfiguration configuration, string key)
    {
        string raw = RequireValue(configuration, key);
        if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? value) ||
            !string.IsNullOrEmpty(value.UserInfo) ||
            !string.Equals(value.AbsoluteUri, value.GetLeftPart(UriPartial.Authority) + "/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{SectionName}:{key} must be one scheme-and-authority origin.");
        }
        return value;
    }

    private static string RequireRelativePath(IConfiguration configuration, string key)
    {
        string value = RequireValue(configuration, key);
        if (IsPlatformAbsolutePath(value) || value.Split('/', '\\').Any(segment => segment is "" or "." or ".."))
            throw new InvalidOperationException($"{SectionName}:{key} must be a non-empty relative path within the ejected deployment.");
        return value;
    }

    private static bool IsPlatformAbsolutePath(string value) =>
        value.StartsWith('/') ||
        value.StartsWith('\\') ||
        value.Length >= 2 && IsAsciiLetter(value[0]) && value[1] == ':';

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static string RequireValue(IConfiguration configuration, string key)
    {
        string? value = configuration[$"{SectionName}:{key}"];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Missing required hosted deployment configuration '{SectionName}:{key}'.");
        return value;
    }
}

/// <summary>Maps stable health and readiness responses for the configured hosted deployment.</summary>
public static class HostedDeploymentEndpoints
{
    /// <summary>Maps the service-only health and readiness routes after validated configuration is loaded.</summary>
    public static IEndpointRouteBuilder MapRunicHostedDeploymentHealth(
        this IEndpointRouteBuilder endpoints,
        HostedDeploymentConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configuration);
        endpoints.MapGet(HostedDeploymentConfiguration.HealthPath, static () => Results.Ok(new HostedDeploymentStatus("healthy")));
        endpoints.MapGet(HostedDeploymentConfiguration.ReadinessPath, static () => Results.Ok(new HostedDeploymentStatus("ready")));
        return endpoints;
    }
}

/// <summary>The bounded response emitted only by the C# deployment health routes.</summary>
public sealed record HostedDeploymentStatus(string Status)
{
    /// <summary>The stable hosted-deployment response schema.</summary>
    public const string SchemaName = "runic.hosted-deployment-health/1";
    /// <summary>Gets the stable hosted-deployment response schema.</summary>
    public string Schema { get; } = SchemaName;
}
