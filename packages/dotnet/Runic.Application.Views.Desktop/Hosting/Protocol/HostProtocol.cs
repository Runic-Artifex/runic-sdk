using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Runic.Application.Views.Desktop.Hosting;

// The Runic host protocol runic-host/1 (specs/desktop/host-protocol.md): constants, secrets, proofs and the launch
// lines. Shared by the guest (RunicDesktopHost), the host (SH3) and RunicTestHost, which links this file.
internal static class HostProtocol
{
    internal const int Version = 1;
    internal const string OfferPrefix = "RUNIC_HOST_OFFER=";
    internal const string ChannelPrefix = "RUNIC_HOST_CHANNEL=";
    internal const string RefusedPrefix = "RUNIC_HOST_REFUSED=";
    internal const string ChannelPath = "/_runic_host";
    internal const int MaximumLineBytes = 4_096;
    internal const int MaximumMessageBytes = 1_048_576;
    // The first message of a connection that has not completed the handshake; a valid hello is far smaller.
    internal const int MaximumHelloBytes = 16_384;
    internal const int MaximumWaitingRequests = 64;
    internal const int MaximumUnauthenticatedConnections = 8;
    internal static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(10);
    // From a connection's accept to its upgrade.
    internal static readonly TimeSpan UnauthenticatedTimeout = TimeSpan.FromSeconds(10);
    // From the upgrade to the first message.
    internal static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(2);
    // A requester gives up a request that has had no reply by then (requests that wait for the user are exempt).
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    // A side that cannot send one message in this time closes the connection.
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan HostGoneExitTimeout = TimeSpan.FromSeconds(10);
    internal const string HostProofLabel = "runic-host/1 host";
    internal const string GuestProofLabel = "runic-host/1 guest";

    internal static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 64 };

    // 32 bytes from the operating system's secure generator, as base64url without padding (43 characters).
    internal static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    internal static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // A 43-character base64url secret's 32 bytes, or null.
    internal static byte[]? DecodeSecret(string? text)
    {
        if (text is not { Length: 43 }) return null;
        foreach (var c in text)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        try
        {
            var bytes = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + "=");
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException) { return null; }
    }

    // HMAC-SHA256 keyed with the nonce over "<label> <key>", base64url (host-protocol.md, Proofs).
    internal static string Proof(byte[] nonce, string label, string key) =>
        Base64Url(HMACSHA256.HashData(nonce, Encoding.ASCII.GetBytes(label + " " + key)));

    internal static bool FixedTimeEquals(string? actual, string expected) =>
        actual is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));

    internal static JsonObject? ParseObject(string json)
    {
        try { return JsonNode.Parse(json, documentOptions: DocumentOptions) as JsonObject; }
        catch (JsonException) { return null; }
    }

    // A line with the prefix whose rest is one JSON object, or null.
    internal static JsonObject? ParseLine(string line, string prefix) =>
        line.StartsWith(prefix, StringComparison.Ordinal) ? ParseObject(line[prefix.Length..]) : null;

    internal static string? String(JsonObject message, string name) =>
        message[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static bool? Boolean(JsonObject message, string name) =>
        message[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    internal static long? Integer(JsonObject message, string name)
    {
        if (message[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return null;
        // A parsed value converts; one built in memory only reads as the type it was built from.
        if (value.TryGetValue<long>(out var number)) return number;
        if (value.TryGetValue<int>(out var small)) return small;
        return value.TryGetValue<double>(out var real) && real == Math.Floor(real) && Math.Abs(real) <= 9_007_199_254_740_991
            ? (long)real
            : null;
    }

    // set-placement's members (host-protocol.md, Presentation): x and y both absent (or null) or both integers from
    // -32,768 to 32,767, width and height from 0 (keep) to 32,767, maximized and fullScreen; null when one is out of form.
    internal static (int? X, int? Y, int Width, int Height, bool Maximized, bool FullScreen)? Placement(JsonObject message)
    {
        if (!Position(message, "x", out var x) || !Position(message, "y", out var y) || x is null != y is null) return null;
        return Integer(message, "width") is { } width and >= 0 and <= 32_767 && Integer(message, "height") is { } height and >= 0 and <= 32_767 &&
            Boolean(message, "maximized") is { } maximized && Boolean(message, "fullScreen") is { } fullScreen
            ? (x, y, (int)width, (int)height, maximized, fullScreen)
            : null;

        static bool Position(JsonObject message, string name, out int? value)
        {
            value = null;
            if (message[name] is null) return true;
            if (Integer(message, name) is not { } number || number is < -32_768 or > 32_767) return false;
            value = (int)number;
            return true;
        }
    }

    // A string of 1 to maximum characters, or null.
    internal static string? Text(JsonObject message, string name, int maximum) =>
        String(message, name) is { Length: > 0 } text && text.Length <= maximum ? text : null;

    // An identifier: 1 to 128 characters from U+0021 to U+007E.
    internal static bool IsIdentifier(string? text) =>
        text is { Length: > 0 and <= 128 } && text.All(static c => c is >= '!' and <= '~');

    // A {name, version} member, or null.
    internal static (string Name, string Version)? Product(JsonObject message, string name) =>
        message[name] is JsonObject product && Text(product, "name", 128) is { } productName &&
        Text(product, "version", 64) is { } version
            ? (productName, version)
            : null;

    internal static JsonObject ProductNode(string name, string version) => new() { ["name"] = name, ["version"] = version };

    internal static string Line(string prefix, JsonObject content) => prefix + content.ToJsonString() + "\n";

    // The offer the host writes on the guest's standard input.
    // elevatedGuest is present, and true, only when the host allows an elevated guest (RunicGuestStartInfo.AllowElevatedGuest).
    internal static JsonObject Offer(IEnumerable<int> protocols, string nonce, string hostName, string hostVersion, bool elevatedGuest = false)
    {
        var offer = new JsonObject
        {
            ["protocols"] = new JsonArray([.. protocols.Select(static version => (JsonNode)version)]),
            ["nonce"] = nonce,
            ["host"] = ProductNode(hostName, hostVersion),
        };
        if (elevatedGuest) offer["elevatedGuest"] = true;
        return offer;
    }

    // ElevatedGuest: the host's opt-in to an elevated guest, never evidence of the host's elevation (host-protocol.md, Offer).
    internal sealed record OfferContent(IReadOnlyList<int> Protocols, byte[] Nonce, string NonceText, string HostName, string HostVersion,
        bool ElevatedGuest);

    // The offer's members in range (host-protocol.md, Offer), or null.
    internal static OfferContent? ReadOffer(JsonObject offer)
    {
        if (offer["protocols"] is not JsonArray { Count: > 0 and <= 16 } list) return null;
        var protocols = new List<int>();
        foreach (var item in list)
        {
            if (item is not JsonValue value || !value.TryGetValue<int>(out var version) || version < 1 || protocols.Contains(version))
                return null;
            protocols.Add(version);
        }
        if (String(offer, "nonce") is not { } nonceText || DecodeSecret(nonceText) is not { } nonce) return null;
        if (Product(offer, "host") is not { } host) return null;
        // Anything but true, such as a string, reads as no opt-in.
        var elevatedGuest = offer["elevatedGuest"] is JsonValue optIn && optIn.GetValueKind() == JsonValueKind.True;
        return new(protocols, nonce, nonceText, host.Name, host.Version, elevatedGuest);
    }

    internal sealed record ChannelContent(Uri Url, string Key, int Protocol);

    // The channel line's members (host-protocol.md, Channel line), or null when they are not of the required form.
    internal static ChannelContent? ReadChannel(JsonObject channel)
    {
        if (String(channel, "url") is not { } text || !Uri.TryCreate(text, UriKind.Absolute, out var url)) return null;
        if (url.Scheme != "ws" || url.AbsolutePath != ChannelPath || !string.IsNullOrEmpty(url.Query) ||
            !string.IsNullOrEmpty(url.Fragment) || !string.IsNullOrEmpty(url.UserInfo) || url.IsDefaultPort ||
            url.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6) || !url.IsLoopback ||
            url.Host is not ("127.0.0.1" or "[::1]"))
            return null;
        if (String(channel, "key") is not { } key || DecodeSecret(key) is null) return null;
        if (Integer(channel, "protocol") is not { } protocol || protocol < 1 || protocol > int.MaxValue) return null;
        return new(url, key, (int)protocol);
    }

    // { "ok": false, "error": { kind, reason?, message } } for the request id.
    internal static JsonObject Error(long id, string kind, string? reason, string message)
    {
        var error = new JsonObject { ["kind"] = kind };
        if (reason is not null) error["reason"] = reason;
        error["message"] = message.Length <= 1_024 ? message : message[..1_024];
        return new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = error };
    }

    internal static string Kebab(string pascal)
    {
        var text = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c))
            {
                // IOError → io-error: a capital that follows a capital starts a word only before a lowercase letter.
                var startsWord = i > 0 && (!char.IsUpper(pascal[i - 1]) || (i + 1 < pascal.Length && char.IsLower(pascal[i + 1])));
                if (startsWord) text.Append('-');
                text.Append(char.ToLowerInvariant(c));
            }
            else text.Append(c);
        }
        return text.ToString();
    }
}
