using System.Net;
using System.Net.WebSockets;

namespace Runic.Desktop.Internal;

// One request to a presentation server and its response, as the host core and the surfaces see it. The native server
// implements it directly (NativeExchange); Runic.Desktop.Kestrel adapts an ASP.NET Core HttpContext. Runic Desktop serves
// GET and HEAD, so there is no request body. Header names are case-insensitive; a header sent more than once reads as
// its values joined with commas, as ASP.NET Core's StringValues does.
internal abstract class PresentationExchange
{
    protected PresentationExchange(string method, string path, string queryString)
    {
        Method = method;
        Path = path;
        QueryString = queryString;
    }

    internal string Method { get; }

    // The request target's path as Kestrel presents it: percent-decoded except %2F, dot segments removed. The host
    // core moves a surface's path base from Path to PathBase before the surface sees the request.
    internal string Path { get; set; }

    internal string PathBase { get; set; } = string.Empty;

    // The raw query including its '?', or empty.
    internal string QueryString { get; }

    internal bool IsGet => string.Equals(Method, "GET", StringComparison.OrdinalIgnoreCase);

    internal bool IsHead => string.Equals(Method, "HEAD", StringComparison.OrdinalIgnoreCase);

    internal abstract IPAddress? RemoteIpAddress { get; }

    internal abstract int RemotePort { get; }

    internal abstract IPAddress? LocalIpAddress { get; }

    internal abstract int LocalPort { get; }

    // The requester left, or the host core linked it with the surface's and the host's closing.
    internal abstract CancellationToken RequestAborted { get; set; }

    internal IServiceProvider? RequestServices { get; set; }

    internal PresentationRequestCancellation? Cancellation { get; set; }

    // The client a surface identified by its cookie (BrowserLaunchBinding), 0 for none.
    internal nuint ClientId { get; set; }

    internal abstract bool IsWebSocketRequest { get; }

    // The value of a request header, empty when it is absent.
    internal abstract string GetRequestHeader(string name);

    // Every request header once, with repeated headers joined.
    internal abstract IEnumerable<KeyValuePair<string, string>> RequestHeaders { get; }

    internal string? GetRequestCookie(string name) => PresentationCookies.Find(GetRequestHeader("Cookie"), name);

    internal IReadOnlyList<string> WebSocketRequestedProtocols => PresentationCookies.SplitList(GetRequestHeader("Sec-WebSocket-Protocol"));

    internal abstract int StatusCode { get; set; }

    internal abstract string? ContentType { get; set; }

    internal abstract long? ContentLength { get; set; }

    // Replaces a response header.
    internal abstract void SetResponseHeader(string name, string value);

    // Adds a value to a response header, after the values it has.
    internal abstract void AppendResponseHeader(string name, string value);

    internal abstract IReadOnlyList<string> GetResponseHeader(string name);

    // The response body. The first write sends the status and headers.
    internal abstract Stream ResponseBody { get; }

    // Sends the whole file as the body; the server may answer a byte range.
    internal abstract Task SendFileAsync(string path, CancellationToken cancellationToken);

    internal abstract Task<WebSocket> AcceptWebSocketAsync(string? subProtocol);

    // Splits the Host header the way ASP.NET Core's HostString does: an IPv6 literal keeps its brackets.
    internal (string Host, int? Port) GetHost() => ParseHost(GetRequestHeader("Host"));

    internal static (string Host, int? Port) ParseHost(string value)
    {
        var portSeparator = value.StartsWith('[')
            ? value.IndexOf("]:", StringComparison.Ordinal) is var bracket and >= 0 ? bracket + 1 : -1
            : value.LastIndexOf(':');
        if (portSeparator < 0 || (!value.StartsWith('[') && value.IndexOf(':') != portSeparator))
        {
            return (value, null);
        }
        var host = value[..portSeparator];
        return int.TryParse(value.AsSpan(portSeparator + 1), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var port)
            ? (host, port)
            : (host, null);
    }

    // Whether Path is base or starts with base followed by '/', case-insensitively as PathString.StartsWithSegments.
    internal static bool StartsWithSegments(string path, string segments, out string remaining)
    {
        remaining = string.Empty;
        if (segments.Length == 0)
        {
            remaining = path;
            return true;
        }
        if (!path.StartsWith(segments, StringComparison.OrdinalIgnoreCase) ||
            (path.Length > segments.Length && path[segments.Length] != '/'))
        {
            return false;
        }
        remaining = path[segments.Length..];
        return true;
    }
}

internal static class PresentationStatus
{
    internal const int Ok = 200;
    internal const int Found = 302;
    internal const int SeeOther = 303;
    internal const int BadRequest = 400;
    internal const int Unauthorized = 401;
    internal const int Forbidden = 403;
    internal const int NotFound = 404;
    internal const int MethodNotAllowed = 405;
    internal const int MisdirectedRequest = 421;
    internal const int UpgradeRequired = 426;
    internal const int InternalServerError = 500;
}

internal static class PresentationCookies
{
    // The value of the first cookie with the name in a Cookie header, as ASP.NET Core's request cookies read it.
    internal static string? Find(string header, string name)
    {
        foreach (var range in header.AsSpan().Split(';'))
        {
            var pair = header.AsSpan(range).Trim();
            var equals = pair.IndexOf('=');
            if (equals <= 0 || !pair[..equals].Trim().SequenceEqual(name))
            {
                continue;
            }
            var value = pair[(equals + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }
            try
            {
                return Uri.UnescapeDataString(value.ToString());
            }
            catch (UriFormatException)
            {
                return value.ToString();
            }
        }
        return null;
    }

    // A Set-Cookie value in ASP.NET Core's form for an HttpOnly, SameSite=Strict cookie.
    internal static string StrictHttpOnly(string name, string value, string path) =>
        $"{name}={Uri.EscapeDataString(value)}; path={path}; samesite=strict; httponly";

    // A comma-separated header list, its items trimmed and empty ones skipped.
    internal static IReadOnlyList<string> SplitList(string header)
    {
        if (header.Length == 0)
        {
            return [];
        }
        List<string> items = [];
        foreach (var range in header.AsSpan().Split(','))
        {
            var item = header.AsSpan(range).Trim();
            if (item.Length != 0)
            {
                items.Add(item.ToString());
            }
        }
        return items;
    }
}
