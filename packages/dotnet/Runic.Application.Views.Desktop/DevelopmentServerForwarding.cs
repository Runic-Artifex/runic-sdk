using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// Forwards what a Window's own origin cannot serve to the frontend's development server, the internal contract between
/// <c>dotnet runic dev</c> and the Runic Desktop and CS-WebUI hosts (#1085, #1086). With a Vite or Angular development
/// server, <c>dev</c> writes a development document into <c>www/</c> that loads the page's modules from the server, and
/// the Window's origin serves only <c>www/</c>. A URL that the page builds at run time against its document, such as
/// <c>&lt;img src="brand/logo.svg"&gt;</c> or the Vite DevTools dock's <c>/__devtools-assets/</c> icons, would 404 there.
/// </summary>
/// <remarks>
/// <para>
/// Forwarding is on only in the <c>Development</c> environment, when <c>dev</c> set <see cref="ViteVariable"/> or
/// <see cref="FrontendVariable"/> to <c>http://127.0.0.1:&lt;port&gt;/</c>, the address it binds the server to. It runs
/// after the Window's content and the application's resources had no answer, so it never replaces a file of
/// <c>www/</c>.
/// </para>
/// <para>
/// A path relative to a Window's document is forwarded (proxied), so the page keeps its own origin for it. A
/// root-relative path outside every Window's path, such as the dock's <c>/__devtools/</c>, is redirected instead
/// (<see cref="RootTarget"/>), so the files loaded there resolve their own URLs, and the dock its WebSocket, against
/// the server.
/// </para>
/// <para>
/// Requests: only <c>GET</c> and <c>HEAD</c>, only from a loopback peer that names a loopback <c>Host</c>, and only
/// same-origin or user-initiated fetches (<c>Sec-Fetch-Site</c>). Targets: built from the server's origin and a plain
/// path (no empty, <c>.</c> or <c>..</c> segment, no encoded or literal slash or backslash), then checked to keep the
/// server's scheme, host, port and base path. A first segment that starts with <c>__</c> or <c>@</c> names one of the
/// server's own endpoints (Vite's <c>/__open-in-editor</c>, vite-plugin-runic's <c>/__runic/</c>, <c>/@fs/</c>), which
/// would answer the Window's page without the provenance headers they judge, so only the dock's <c>__devtools</c> and
/// <c>__devtools-assets</c> pass, at the root. Requests carry no cookies, credentials, <c>Origin</c> or
/// <c>Sec-Fetch-*</c>; redirects are not followed; only successful answers come back, with their type, cache headers and
/// <c>nosniff</c>, within a size and time limit (DevelopmentServerProxy, Runic Desktop only). WebSockets are not forwarded. Another local process that learns a
/// Window's random path reads through it what it could read from the server directly.
/// </para>
/// </remarks>
internal sealed class DevelopmentServerForwarding
{
    internal const string ViteVariable = "RUNIC_APPLICATION_VITE_DEV_SERVER";
    internal const string FrontendVariable = "RUNIC_APPLICATION_FRONTEND_DEV_SERVER";

    private const int MaximumQueryLength = 4096;

    // The root-level server endpoints the Vite DevTools dock needs; every other "__" or "@" first segment stays unforwarded.
    private static readonly string[] RootEndpoints = ["__devtools", "__devtools-assets"];

    internal DevelopmentServerForwarding(Uri origin) => Origin = origin;

    /// <summary>
    /// Whether the fallback exists in this build. A trimmed or NativeAOT publish sets the switch to false
    /// (Runic.Application.Views.targets), so the trimmer removes the fallback and its HTTP client from the application;
    /// <c>dotnet runic dev</c> runs an untrimmed build, where it is on.
    /// </summary>
    [FeatureSwitchDefinition(FeatureSwitch)]
    internal static bool IsSupported => !AppContext.TryGetSwitch(FeatureSwitch, out var enabled) || enabled;

    internal const string FeatureSwitch = "Runic.Application.DevelopmentServerForwarding";

    /// <summary>The development server's origin, with its base path, such as <c>http://127.0.0.1:5173/</c>.</summary>
    internal Uri Origin { get; }

    /// <summary>
    /// The forwarding of this process: the environment is <c>Development</c> and <c>dev</c> named its development
    /// server on 127.0.0.1. Otherwise <see langword="null"/>.
    /// </summary>
    internal static DevelopmentServerForwarding? Resolve(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (!IsSupported || !IsDevelopment(read)) return null;
        var configured = read(ViteVariable);
        if (string.IsNullOrWhiteSpace(configured)) configured = read(FrontendVariable);
        return ParseOrigin(configured) is { } origin ? new DevelopmentServerForwarding(origin) : null;
    }

    /// <summary>
    /// An <c>http:</c> origin on 127.0.0.1, the address <c>dev</c> binds the server to, with a path ending in
    /// <c>/</c>; otherwise null. Not <c>localhost</c>, which may resolve to <c>::1</c>, where another local user could
    /// listen on the same port.
    /// </summary>
    internal static Uri? ParseOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.HostNameType != UriHostNameType.IPv4 || !IPAddress.Loopback.Equals(IPAddress.Parse(uri.Host)))
            return null;
        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        return new UriBuilder(Uri.UriSchemeHttp, uri.Host, uri.Port, path).Uri;
    }

    // The rule of BridgeFailureDetails.IsDevelopment in Runic.Application.Views.
    private static bool IsDevelopment(Func<string, string?> read)
    {
        var name = read("DOTNET_ENVIRONMENT");
        if (string.IsNullOrEmpty(name)) name = read("ASPNETCORE_ENVIRONMENT");
        return string.Equals(name, "Development", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a request may be forwarded: <c>GET</c> or <c>HEAD</c> from a loopback peer, with a loopback (or absent)
    /// <c>Host</c> and a same-origin or user-initiated fetch. The path is judged by <see cref="DocumentTarget"/> and
    /// <see cref="RootTarget"/>.
    /// </summary>
    /// <param name="method">The request method.</param>
    /// <param name="peer">The TCP peer, or null where the server does not report it.</param>
    /// <param name="host">The <c>Host</c> header, or null where the server does not report it.</param>
    /// <param name="fetchSite">The <c>Sec-Fetch-Site</c> header, or null when absent or not reported.</param>
    internal static bool Admits(string method, IPAddress? peer, string? host, string? fetchSite)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
            return false;
        if (peer is not null && !IPAddress.IsLoopback(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer)) return false;
        if (host is not null && !IsLoopbackHost(HostName(host))) return false;
        // Browsers send Sec-Fetch-Site with every request; a page of another site gets nothing forwarded.
        return string.IsNullOrEmpty(fetchSite) ||
            string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fetchSite, "none", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The dock's script, which its loader requests from the document's origin.</summary>
    internal const string DockScriptPath = "/__devtools/embedded.js";

    /// <summary>
    /// The Window's answer to <see cref="DockScriptPath"/>: a module that imports the dock's script from the server.
    /// Vite serves the dock's inline loader as a module of its own (<c>index.html?html-proxy</c>), whose root-relative
    /// <c>/__devtools/embedded.js</c> names the Window's origin, and the Runic development client loads the same script
    /// from the server. A redirect would evaluate the script under two URLs, defining its element twice; this module
    /// imports it under the server's URL, so the page runs it once, whichever loader comes first.
    /// </summary>
    internal string DockScriptModule() =>
        // AbsoluteUri escapes quotes, backslashes and line breaks, so the URL is a plain string literal.
        $"import \"{new Uri(Origin, DockScriptPath).AbsoluteUri}\";\n";

    /// <summary>Whether a root-relative path names one of the dock's root endpoints (<see cref="RootEndpoints"/>).</summary>
    internal static bool IsDockPath(string path) =>
        RelativePath(path, rawPath: null) is { } relative && IsRootEndpoint(relative.Split('/')[0]);

    /// <summary>
    /// The server URL of a path relative to a Window's document folder: the development document in <c>www/</c> stands
    /// for the server's document at <see cref="Origin"/>, so <c>/brand/logo.svg</c> of the Window is
    /// <c>brand/logo.svg</c> below <see cref="Origin"/>. Null for a path that is not plain or names a server endpoint.
    /// </summary>
    /// <param name="path">The decoded path, starting with <c>/</c>.</param>
    /// <param name="query">The raw query, such as <c>?v=1</c>, or null.</param>
    /// <param name="rawPath">The path as received, before the last decoding, when the server reports it.</param>
    internal Uri? DocumentTarget(string path, string? query, string? rawPath = null) =>
        Build(path, query, rawPath, root: false);

    /// <summary>
    /// The server URL of a root-relative path of the Window's listener, outside every Window's own path, such as the
    /// Vite DevTools dock's <c>/__devtools/</c> and <c>/__devtools-assets/</c>: the same path on the server's host.
    /// </summary>
    internal Uri? RootTarget(string path, string? query, string? rawPath = null) =>
        Build(path, query, rawPath, root: true);

    private Uri? Build(string path, string? query, string? rawPath, bool root)
    {
        if (RelativePath(path, rawPath) is not { } relative || Query(query) is not { } suffix) return null;
        var first = relative.Split('/')[0];
        if (first.StartsWith("__", StringComparison.Ordinal) || first.StartsWith('@'))
        {
            if (!root || !IsRootEndpoint(first)) return null;
        }
        var basePath = root ? "/" : Origin.AbsolutePath;
        Uri target;
        try
        {
            target = new UriBuilder(Origin.Scheme, Origin.Host, Origin.Port, basePath + Escape(relative))
            {
                Query = suffix.Length == 0 ? string.Empty : suffix[1..],
            }.Uri;
        }
        catch (UriFormatException)
        {
            return null;
        }
        // The target keeps the server's authority and base path, whatever the path held.
        return target.Scheme == Origin.Scheme &&
            string.Equals(target.IdnHost, Origin.IdnHost, StringComparison.OrdinalIgnoreCase) &&
            target.Port == Origin.Port && target.UserInfo.Length == 0 &&
            target.AbsolutePath.StartsWith(basePath, StringComparison.Ordinal) &&
            !target.AbsolutePath.Contains("//", StringComparison.Ordinal)
                ? target
                : null;
    }

    private static bool IsRootEndpoint(string segment) =>
        Array.Exists(RootEndpoints, endpoint => string.Equals(endpoint, segment, StringComparison.Ordinal));

    // The path without its leading slash, or null for a path that is not plain: empty, with an empty segment (a trailing
    // slash aside), a dot segment, a backslash or a control character, or, as received, an encoded slash or backslash.
    private static string? RelativePath(string path, string? rawPath)
    {
        if (rawPath is not null &&
            (rawPath.Contains("%2F", StringComparison.OrdinalIgnoreCase) ||
             rawPath.Contains("%5C", StringComparison.OrdinalIgnoreCase) ||
             rawPath.Contains('\\') || rawPath.Contains("//", StringComparison.Ordinal)))
            return null;
        if (string.IsNullOrEmpty(path) || path[0] != '/') return null;
        var relative = path[1..];
        if (relative.Length == 0) return null;
        foreach (var character in relative)
        {
            if (character is '\\' || char.IsControl(character)) return null;
        }
        var segments = relative.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment is "." or ".." || segment.Length == 0 && index != segments.Length - 1) return null;
        }
        return relative;
    }

    private static string? Query(string? query)
    {
        if (string.IsNullOrEmpty(query) || query == "?") return string.Empty;
        if (query.Length > MaximumQueryLength || query[0] != '?' || query.Contains('#')) return null;
        foreach (var character in query)
        {
            if (char.IsControl(character) || character == ' ') return null;
        }
        return query;
    }

    private static string Escape(string relative) =>
        string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));

    private static string HostName(string host)
    {
        var value = host.Trim();
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']', StringComparison.Ordinal);
            return end > 0 ? value[1..end] : value;
        }
        var colon = value.LastIndexOf(':');
        return colon >= 0 && value.IndexOf(':') == colon ? value[..colon] : value;
    }

    private static bool IsLoopbackHost(string host)
    {
        var name = host.Trim('[', ']');
        // The Window's own Host: browsers name it as localhost or a loopback address.
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(name, out var address) && IPAddress.IsLoopback(address);
    }
}
