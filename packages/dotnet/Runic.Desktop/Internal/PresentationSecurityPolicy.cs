namespace Runic.Desktop.Internal;

internal sealed record PresentationSecurityPolicy(
    bool RequireSessionCredential,
    bool AllowMissingOrigin,
    bool AllowMultipleClients,
    bool UseClientCookies,
    IReadOnlySet<string> AdditionalOrigins)
{
    internal static PresentationSecurityPolicy SafeDefault { get; } = new(
        RequireSessionCredential: true,
        AllowMissingOrigin: false,
        AllowMultipleClients: false,
        UseClientCookies: true,
        AdditionalOrigins: new HashSet<string>(StringComparer.Ordinal));

    internal bool AllowsOrigin(Uri surfaceUrl, string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return AllowMissingOrigin;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var candidate) ||
            candidate.UserInfo.Length != 0 || candidate.PathAndQuery != "/" || candidate.Fragment.Length != 0)
        {
            return false;
        }

        var canonical = CanonicalOrigin(candidate);
        return canonical == CanonicalOrigin(surfaceUrl) || AdditionalOrigins.Contains(canonical);
    }

    internal static string CanonicalOrigin(Uri origin)
    {
        if (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Only HTTP and HTTPS origins are supported.", nameof(origin));
        }

        var effectivePort = origin.IsDefaultPort
            ? origin.Scheme == Uri.UriSchemeHttps ? 443 : 80
            : origin.Port;
        return $"{origin.Scheme.ToLowerInvariant()}://{origin.IdnHost.ToLowerInvariant()}:{effectivePort}";
    }
}

internal sealed record PresentationSurfaceRuntimeOptions(
    string RootFolder,
    string? BrowserFolder,
    IWebUiEmbeddedHostFactory EmbeddedHostFactory,
    bool WaitForConnection,
    TimeSpan ConnectionTimeout,
    Action<DesktopDiagnostic>? DiagnosticSink);
