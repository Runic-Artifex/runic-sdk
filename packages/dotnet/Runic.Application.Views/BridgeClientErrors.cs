using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

// client-errors/1 (specs/application/README.md, "Page errors"): a page reports its uncaught errors with client-error
// requests, and the producer logs them in the category RunicViewsTelemetry.PageLogCategory. The report is untrusted
// input from the page: every text is capped, control characters are removed, a URL loses its query and fragment, and
// each connection logs at most MaximumPerMinute reports a minute.
internal static class BridgeClientErrors
{
    internal const string Feature = "client-errors/1";
    internal const int MaximumPerMinute = 10;
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

    // Logs the report, unless the connection already logged MaximumPerMinute this minute; those are counted and the
    // count goes with the next logged report.
    internal static void Report(BridgeConnection connection, JsonElement root, ILogger logger, TimeProvider time)
    {
        var now = time.GetUtcNow();
        long suppressed;
        lock (connection)
        {
            if (connection.ClientErrorWindow is not { } start || now - start >= Window)
            {
                connection.ClientErrorWindow = now;
                connection.ClientErrorsLogged = 0;
            }
            if (connection.ClientErrorsLogged >= MaximumPerMinute)
            {
                if (connection.ClientErrorsSuppressed++ == 0) ViewsLog.PageErrorsSuppressed(logger, MaximumPerMinute);
                return;
            }
            connection.ClientErrorsLogged++;
            suppressed = connection.ClientErrorsSuppressed;
            connection.ClientErrorsSuppressed = 0;
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

    // A capped text without control characters, which could forge log lines; a stack keeps its line breaks.
    internal static string? Text(JsonElement root, string name, int maximumLength, bool singleLine)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return null;
        var value = element.GetString()!;
        var text = new StringBuilder(Math.Min(value.Length, maximumLength + 1));
        foreach (var character in value)
        {
            if (text.Length >= maximumLength)
            {
                text.Append('…');
                break;
            }
            if (character == '\n' && !singleLine) text.Append('\n');
            else if (char.IsControl(character) || character is (char)0x2028 or (char)0x2029) text.Append(' ');
            else text.Append(character);
        }
        return text.ToString();
    }

    // The page's URL without its query and fragment, which can carry tokens.
    internal static string? Url(string? url)
    {
        if (url is null) return null;
        var end = url.AsSpan().IndexOfAny('?', '#');
        return end < 0 ? url : url[..end];
    }
}
