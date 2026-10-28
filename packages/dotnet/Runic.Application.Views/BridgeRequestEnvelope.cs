using System.Text.Json;

namespace Runic.Application.Views;

// One Views protocol 4 request whose envelope was read and checked against the message catalogue
// (specs/application/README.md, "Message catalogue"): the members each kind requires, their JSON types and the
// identifier, integer, route and contract grammars. Whether a route or member exists is the route handler's question.
// Handlers read the other members (value, input, handlers, output) from Root; a member the catalogue requires is
// present with its JSON type. The document is disposed when the request completes.
internal sealed class BridgeRequestEnvelope : IDisposable
{
    // The JSON profile: I-JSON with nesting depth at most 64 (specs/application/values.md, "JSON profile").
    internal const int MaximumDepth = 64;
    internal const int MaximumIdentifierLength = 128;
    internal const int MaximumRouteLength = 128;
    internal const long MaximumSafeInteger = 9_007_199_254_740_991;
    internal const string ReservedRoutePrefix = "runic.";

    private static readonly JsonDocumentOptions ReaderOptions = new()
    {
        MaxDepth = MaximumDepth,
        AllowDuplicateProperties = false,
    };

    private readonly JsonDocument _document;

    private BridgeRequestEnvelope(JsonDocument document, string kind)
    {
        _document = document;
        Kind = kind;
    }

    internal JsonElement Root => _document.RootElement;
    internal string Kind { get; }
    // Present for every kind but hello and receive.
    internal string? Route { get; private set; }
    // Present for the kinds that name a member.
    internal string? Member { get; private set; }

    internal bool IsServiceRoute => Route is { } route && route.StartsWith(ReservedRoutePrefix, StringComparison.Ordinal);

    public void Dispose() => _document.Dispose();

    // The request kinds of Views routes. hello and receive belong to the connection, and reserved routes (runic.*)
    // define their own kinds.
    internal static bool IsViewKind(string kind) => kind switch
    {
        "open" or "snapshot" or "close" or "set" or "write" or "command" or "can" or "start" or "status" or "watch"
            or "unwatch" or "cancel" or "stream" or "interactions" or "interaction-reply" => true,
        _ => false,
    };

    // Reads one request. A message that is not one JSON object of the profile, or has no string kind, fails with
    // invalid-request; so does a known kind whose members do not match the catalogue. An unknown kind is returned as
    // read: the dispatcher answers it after the handshake check.
    internal static BridgeRequestEnvelope? TryRead(ReadOnlyMemory<byte> message, out string? error)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(message, ReaderOptions); }
        catch (JsonException exception)
        {
            error = $"The request is not one JSON object: {exception.Message}";
            return null;
        }
        catch (ArgumentException exception)
        {
            error = $"The request is not one JSON object: {exception.Message}";
            return null;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("kind", out var kindElement)
            || kindElement.ValueKind != JsonValueKind.String
            || kindElement.GetString() is not { Length: > 0 } kind)
        {
            document.Dispose();
            error = "The request is not a JSON object with a kind.";
            return null;
        }

        var envelope = new BridgeRequestEnvelope(document, kind);
        error = envelope.Check();
        if (error is null) return envelope;
        envelope.Dispose();
        return null;
    }

    // Checks the members of a kind of the catalogue. Unknown kinds and the kinds of reserved routes pass with only
    // their route read, when they have one.
    private string? Check()
    {
        var root = Root;
        switch (Kind)
        {
            case "hello":
                // The protocol is read first, so that a hello of another protocol version, whatever its shape, is
                // refused with protocol-mismatch rather than invalid-request (BridgeDispatcher).
                return null;
            case "receive":
                return null;
        }

        if (root.TryGetProperty("route", out var routeElement))
        {
            if (routeElement.ValueKind != JsonValueKind.String || !IsRoute(routeElement.GetString()!))
                return "The request's route is not a route name.";
            Route = routeElement.GetString();
        }
        if (!IsViewKind(Kind)) return null;
        if (Route is null) return "The request has no route.";
        if (IsServiceRoute) return null;

        switch (Kind)
        {
            case "open":
                return Identifier(root, "presentation")
                    ?? (root.TryGetProperty("handlers", out var handlers) ? Handlers(handlers) : null);
            case "snapshot":
                return null;
            case "close":
                return Identifier(root, "presentation");
            case "set":
                return ReadMember(root)
                    ?? Present(root, "value")
                    ?? Integer(root, "edit", minimum: 1);
            case "write":
                return ReadMember(root)
                    ?? Identifier(root, "requestId")
                    ?? Integer(root, "expectedVersion", minimum: 0)
                    ?? Present(root, "expectedValue")
                    ?? Present(root, "value");
            case "command":
            case "can":
                return ReadMember(root);
            case "start":
            case "status":
            case "watch":
            case "unwatch":
            case "cancel":
                return ReadMember(root) ?? Identifier(root, "operation") ?? Contract(root);
            case "stream":
                return ReadMember(root) ?? Identifier(root, "operation") ?? Contract(root)
                    ?? Integer(root, "cursor", minimum: 0);
            case "interactions":
                return Identifier(root, "presentation")
                    ?? Integer(root, "generation", minimum: 0)
                    ?? (root.TryGetProperty("handlers", out var registered)
                        ? Handlers(registered) : "The request has no handlers.");
            case "interaction-reply":
                return Identifier(root, "presentation")
                    ?? Identifier(root, "requestId")
                    ?? Integer(root, "ownerEpoch", minimum: 0)
                    ?? String(root, "name")
                    ?? String(root, "contract")
                    ?? Outcome(root);
            default:
                return null;
        }
    }

    private string? ReadMember(JsonElement root)
    {
        if (String(root, "member") is { } error) return error;
        Member = root.GetProperty("member").GetString();
        return null;
    }

    private static string? Outcome(JsonElement root)
    {
        if (String(root, "outcome") is { } error) return error;
        return root.GetProperty("outcome").GetString() switch
        {
            "answered" => Present(root, "output"),
            "cancelled" or "failed" => null,
            _ => "The interaction reply's outcome is not answered, cancelled or failed.",
        };
    }

    private static string? Handlers(JsonElement handlers)
    {
        if (handlers.ValueKind != JsonValueKind.Array) return "The request's handlers are not an array.";
        foreach (var handler in handlers.EnumerateArray())
            if (handler.ValueKind != JsonValueKind.Object || String(handler, "name") is not null || String(handler, "contract") is not null)
                return "A handler is not an object with a name and a contract.";
        return null;
    }

    private static string? Contract(JsonElement root)
    {
        if (String(root, "contract") is { } error) return error;
        return IsContract(root.GetProperty("contract").GetString()!) ? null : "The request's contract is not {view}:{fingerprint}.";
    }

    private static string? Present(JsonElement root, string name) =>
        root.TryGetProperty(name, out _) ? null : $"The request has no {name}.";

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? null : $"The request's {name} is not a string.";

    private static string? Identifier(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && IsIdentifier(value.GetString()!)
            ? null : $"The request's {name} is not an identifier of 1 to {MaximumIdentifierLength} printable ASCII characters.";

    private static string? Integer(JsonElement root, string name, long minimum) =>
        root.TryGetProperty(name, out var value) && TryReadInteger(value, out var integer) && integer >= minimum
            ? null : $"The request's {name} is not an integer from {minimum} to 2^53-1.";

    // Integers are read by value: 1.0 and 1e2 are integers (specs/application/values.md).
    internal static bool TryReadInteger(JsonElement value, out long integer)
    {
        integer = 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)) return false;
        if (double.IsNaN(number) || double.IsInfinity(number) || Math.Floor(number) != number) return false;
        if (number < -MaximumSafeInteger || number > MaximumSafeInteger) return false;
        integer = (long)number;
        return true;
    }

    // Sessions, presentation ids, operation ids and request ids: 1 to 128 characters from U+0021 to U+007E.
    internal static bool IsIdentifier(string value)
    {
        if (value.Length is 0 or > MaximumIdentifierLength) return false;
        foreach (var character in value)
            if (character is < '!' or > '~') return false;
        return true;
    }

    // A route is [A-Za-z0-9_-]{1,128}, or a reserved route of a host service, which starts with "runic.".
    internal static bool IsRoute(string value)
    {
        if (value.Length is 0 or > MaximumRouteLength) return false;
        var start = value.StartsWith(ReservedRoutePrefix, StringComparison.Ordinal) ? ReservedRoutePrefix.Length : 0;
        if (start == value.Length) return false;
        for (var index = start; index < value.Length; index++)
        {
            var character = value[index];
            if (!(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' || (start > 0 && character == '.'))) return false;
        }
        return true;
    }

    // {view}:{fingerprint}: the fingerprint is an identifier without ':' after the last ':'; the view is not empty.
    internal static bool IsContract(string value)
    {
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1) return false;
        return IsIdentifier(value[(separator + 1)..]);
    }
}
