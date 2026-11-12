using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

// client-errors/1 (specs/application/README.md, "Page errors"): a page reports its uncaught errors with client-error
// requests, and the producer logs them in the category RunicViewsTelemetry.PageLogCategory. The report is untrusted
// input from the page: every text is capped, control and bidirectional formatting characters are removed, every stack
// line starts with StackLinePrefix, a URL loses its query and fragment, a region's frame launch code or frame token in
// any text is redacted (host-protocol.md, Frame admission: an engine's message about a blocked frame can carry the
// frame's full address), and each connection logs at most
// MaximumPerMinute reports a minute and each window at most MaximumPerWindowPerMinute, so a page that reloads or opens
// new connections cannot get a fresh budget.
internal static class BridgeClientErrors
{
    internal const string Feature = "client-errors/1";
    internal const int MaximumPerMinute = 10;
    internal const int MaximumPerWindowPerMinute = 30;
    // Starts every line of a logged stack, so in a plain-text sink a stack line cannot pass for a log entry.
    internal const string StackLinePrefix = "    | ";
    internal const int MaximumMessageLength = 2_048;
    internal const int MaximumStackLength = 8_192;
    internal const int MaximumNameLength = 128;
    internal const int MaximumUrlLength = 2_048;
    internal const int MaximumSourceLength = 32;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    // The members of a client-error request; a member of the wrong type is invalid-request, an overlong text is cut.
    internal static string? Check(JsonElement root)
    {
        if (!root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String
            || !BridgeRequestEnvelope.IsIdentifier(source.GetString()!) || source.GetString()!.Length > MaximumSourceLength)
            return "The client-error's source is not an identifier of at most 32 characters.";
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String)
            return "The client-error has no message.";
        foreach (var name in (ReadOnlySpan<string>)["name", "stack", "url"])
        {
            if (root.TryGetProperty(name, out var text) && text.ValueKind != JsonValueKind.String)
                return $"The client-error's {name} is not a string.";
        }
        if (root.TryGetProperty("route", out var route) && (route.ValueKind != JsonValueKind.String || !BridgeRequestEnvelope.IsRoute(route.GetString()!)))
            return "The client-error's route is not a route name.";
        if (root.TryGetProperty("dropped", out var dropped) && !(BridgeRequestEnvelope.TryReadInteger(dropped, out var count) && count >= 0))
            return "The client-error's dropped is not an integer from 0 to 2^53-1.";
        return null;
    }

    // Logs the report, unless the connection already logged MaximumPerMinute this minute or the window
    // MaximumPerWindowPerMinute; those are counted, and the counts go with the next report logged.
    internal static void Report(BridgeConnection connection, Limit window, JsonElement root, ILogger logger, TimeProvider time)
    {
        var now = time.GetUtcNow();
        long suppressed;
        // The window's limit guards the connection's too: a connection belongs to one window.
        lock (window)
        {
            var limit = connection.ClientErrors;
            limit.Start(now);
            window.Start(now);
            if (limit.Logged >= MaximumPerMinute)
            {
                if (limit.Suppressed++ == 0) ViewsLog.PageErrorsSuppressed(logger, MaximumPerMinute);
                return;
            }
            if (window.Logged >= MaximumPerWindowPerMinute)
            {
                if (window.Suppressed++ == 0) ViewsLog.WindowPageErrorsSuppressed(logger, MaximumPerWindowPerMinute);
                return;
            }
            limit.Logged++;
            window.Logged++;
            suppressed = limit.Suppressed + window.Suppressed;
            limit.Suppressed = window.Suppressed = 0;
        }
        BridgeRequestEnvelope.TryReadInteger(root.TryGetProperty("dropped", out var dropped) ? dropped : default, out var pageDropped);
        ViewsLog.PageError(logger,
            Text(root, "source", MaximumSourceLength, singleLine: true) ?? "error",
            Text(root, "name", MaximumNameLength, singleLine: true) ?? "Error",
            Text(root, "message", MaximumMessageLength, singleLine: true) ?? "",
            Url(Text(root, "url", MaximumUrlLength, singleLine: true)) ?? "an unknown page",
            Text(root, "route", MaximumNameLength, singleLine: true) ?? "",
            pageDropped + suppressed,
            $"{connection.ClientName} {connection.ClientVersion}",
            Text(root, "stack", MaximumStackLength, singleLine: false) ?? "");
    }

    // A capped text without control characters, which could forge log lines, and without bidirectional formatting
    // characters, which could make a log line read differently than it is. A stack keeps its line breaks, and each of
    // its lines, the first included (the entry's message puts it on a line of its own), starts with StackLinePrefix.
    // The cap counts the page's characters, not the prefixes, and never splits a surrogate pair.
    internal static string? Text(JsonElement root, string name, int maximumLength, bool singleLine) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? Text(element.GetString()!, maximumLength, singleLine)
            : null;

    internal static string Text(string value, int maximumLength, bool singleLine)
    {
        value = RedactFrameSecrets(value);
        var text = new StringBuilder(Math.Min(value.Length, maximumLength + 1) + (singleLine ? 0 : StackLinePrefix.Length));
        if (!singleLine && value.Length > 0) text.Append(StackLinePrefix);
        var length = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (length >= maximumLength)
            {
                if (char.IsHighSurrogate(text[^1])) text.Length--;
                text.Append('…');
                break;
            }
            length++;
            if (!singleLine && (character == '\n' || character == '\r'))
            {
                if (character == '\r' && index + 1 < value.Length && value[index + 1] == '\n') length--;
                else text.Append('\n').Append(StackLinePrefix);
            }
            else if (IsBidiControl(character)) length--;
            else if (char.IsControl(character) || character is (char)0x2028 or (char)0x2029) text.Append(' ');
            else text.Append(character);
        }
        return text.ToString();
    }

    // The embeddings, overrides and isolates (U+202A-U+202E, U+2066-U+2069) and the marks U+200E, U+200F and U+061C.
    private static bool IsBidiControl(char character) =>
        character is >= (char)0x202A and <= (char)0x202E or >= (char)0x2066 and <= (char)0x2069 or (char)0x200E or (char)0x200F or (char)0x061C;

    // One limit's minute: when it started, the reports logged in it and those suppressed since the last one logged.
    internal sealed class Limit
    {
        internal DateTimeOffset? Started { get; private set; }
        internal int Logged { get; set; }
        internal long Suppressed { get; set; }

        internal void Start(DateTimeOffset now)
        {
            if (Started is { } started && now - started < Window) return;
            Started = now;
            Logged = 0;
        }
    }

    // The code of a frame launch address (/_runic_frame/<code>) and the token of a frame address's fragment
    // (#runic-frame=<token>), both base64url, become "…".
    internal static string RedactFrameSecrets(string text)
    {
        foreach (var marker in (ReadOnlySpan<string>)["/_runic_frame/", "#runic-frame="])
        {
            var start = 0;
            while ((start = text.IndexOf(marker, start, StringComparison.Ordinal)) >= 0)
            {
                var secret = start + marker.Length;
                var end = secret;
                while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] is '-' or '_')) end++;
                if (end > secret) text = string.Concat(text.AsSpan(0, secret), "…", text.AsSpan(end));
                start = secret;
            }
        }
        return text;
    }

    // The page's URL without its query and fragment, which can carry tokens.
    internal static string? Url(string? url)
    {
        if (url is null) return null;
        var end = url.AsSpan().IndexOfAny('?', '#');
        return end < 0 ? url : url[..end];
    }
}
