using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Runic.Application.Testing.Tests.Conformance;

// Writes the messages a producer runner sends and compares the messages it receives, under the rules of
// specs/application/conformance/README.md, "Comparing messages" and "Identifiers". One instance serves one scenario:
// it keeps the bindings of provided ids and the mapping of expected to actual revisions.
internal sealed class ScenarioMessages(IReadOnlyDictionary<string, ScenarioId> ids)
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Dictionary<string, JsonNode?> _provided = new(StringComparer.Ordinal);
    // Expected revision -> actual revision, consistent and strictly increasing.
    private readonly SortedDictionary<double, double> _revisions = [];

    // Supplies a provided id that the runner writes before a message carried it (IConformanceWindow.Provide).
    internal Func<string, JsonNode?>? Provider { get; set; }

    // The declared id with the name, which must be of one of the kinds.
    internal ScenarioId Id(string name, params string[] kinds)
    {
        if (!ids.TryGetValue(name, out var id)) throw new ScenarioFailure($"'{name}' is not declared in ids.");
        if (!kinds.Contains(id.Kind)) throw new ScenarioFailure($"'{name}' is a {id.Kind} id, not a {string.Join(" or ", kinds)} id.");
        return id;
    }

    // The message a send step writes: {"$any"} writes its default, a wire {"$id"} its value, a bound provided
    // {"$id"} its value and {"$json"} its text verbatim. A string message is the request's text.
    internal byte[] Write(JsonNode message)
    {
        if (message is JsonValue text && text.GetValueKind() == JsonValueKind.String)
            return System.Text.Encoding.UTF8.GetBytes(text.GetValue<string>());
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) Write(writer, message);
        return buffer.WrittenSpan.ToArray();
    }

    // Requires that the actual message equals the expected one. A string message is the expected message's text.
    internal void Match(JsonNode expected, JsonNode? actual)
    {
        if (expected is JsonValue text && text.GetValueKind() == JsonValueKind.String)
            expected = JsonNode.Parse(text.GetValue<string>())!;
        Match(expected, actual, "$", member: null);
    }

    // Match for one candidate of an unordered group: null when the messages are equal, otherwise the failure, with the
    // bindings of provided ids and revisions as they were before.
    internal string? TryMatch(JsonNode expected, JsonNode? actual)
    {
        var provided = new Dictionary<string, JsonNode?>(_provided, StringComparer.Ordinal);
        var revisions = new SortedDictionary<double, double>(_revisions);
        try
        {
            Match(expected, actual);
            return null;
        }
        catch (ScenarioFailure failure)
        {
            _provided.Clear();
            foreach (var (name, value) in provided) _provided.Add(name, value);
            _revisions.Clear();
            foreach (var (expectedRevision, actualRevision) in revisions) _revisions.Add(expectedRevision, actualRevision);
            return failure.Message;
        }
    }

    // Requires that an actual value equals an expected one, as in a message (expect.model).
    internal void MatchValue(JsonNode? expected, JsonNode? actual, string path) => Match(expected, actual, path, member: null);

    private void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject any when any.ContainsKey("$any"):
                if (!any.TryGetPropertyValue("default", out var fallback))
                    throw new ScenarioFailure($"{any.ToJsonString()} has no default for the runner to write.");
                Write(writer, fallback);
                break;
            case JsonObject { Count: 1 } id when id["$id"] is JsonValue name:
                var declared = Id(name.GetValue<string>(), "wire", "provided");
                if (declared.Kind == "wire") writer.WriteStringValue(declared.Value);
                else if (_provided.TryGetValue(declared.Name, out var bound)) Write(writer, bound);
                else if (Provider?.Invoke(declared.Name) is { } provided)
                {
                    _provided.Add(declared.Name, provided.DeepClone());
                    Write(writer, provided);
                }
                else throw new ScenarioFailure($"The provided id '{declared.Name}' is written before the producer provided it.");
                break;
            case JsonObject { Count: 1 } json when json["$json"] is JsonValue raw:
                writer.WriteRawValue(raw.GetValue<string>());
                break;
            case JsonObject value:
                writer.WriteStartObject();
                foreach (var (key, child) in value)
                {
                    writer.WritePropertyName(key);
                    Write(writer, child);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array) Write(writer, item);
                writer.WriteEndArray();
                break;
            case null:
                writer.WriteNullValue();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    private void Match(JsonNode? expected, JsonNode? actual, string path, string? member)
    {
        switch (expected)
        {
            case JsonObject any when any.ContainsKey("$any"):
                var type = any["$any"]!.GetValue<string>();
                if (TypeOf(actual) != type && !(type == "integer" && TypeOf(actual) == "number" && IsInteger(actual!)))
                    Fail(path, $"any {type}", actual);
                return;
            case JsonObject { Count: 1 } id when id["$id"] is JsonValue name:
                MatchId(Id(name.GetValue<string>(), "wire", "provided"), actual, path);
                return;
            case JsonObject { Count: 1 } json when json.ContainsKey("$json"):
                throw new ScenarioFailure($"{path}: {{\"$json\"}} appears only in messages the runner writes.");
            case JsonObject value:
                if (actual is not JsonObject actualObject) { Fail(path, value, actual); return; }
                var missing = value.Select(pair => pair.Key).Except(actualObject.Select(pair => pair.Key), StringComparer.Ordinal).ToArray();
                var extra = actualObject.Select(pair => pair.Key).Except(value.Select(pair => pair.Key), StringComparer.Ordinal).ToArray();
                if (missing.Length > 0 || extra.Length > 0)
                    throw new ScenarioFailure($"{path}: members differ (missing [{string.Join(", ", missing)}], unexpected [{string.Join(", ", extra)}]).\n" +
                        $"expected: {Abbreviate(value)}\nactual:   {Abbreviate(actual)}");
                var frame = value["kind"] is JsonValue kind && kind.GetValueKind() == JsonValueKind.String && kind.GetValue<string>() == "frame";
                foreach (var (key, child) in value)
                {
                    if (frame && key == "changes" && child is JsonArray changes) MatchChanges(changes, actualObject[key], $"{path}.changes");
                    else Match(child, actualObject[key], $"{path}.{key}", key);
                }
                return;
            case JsonArray array:
                if (actual is not JsonArray actualArray || actualArray.Count != array.Count) { Fail(path, array, actual); return; }
                for (var index = 0; index < array.Count; index++) Match(array[index], actualArray[index], $"{path}[{index}]", null);
                return;
            case null:
                if (actual is not null && TypeOf(actual) != "null") Fail(path, null, actual);
                return;
            default:
                var expectedType = TypeOf(expected);
                if (expectedType == "number" && member is "revision" or "baseRevision")
                {
                    if (TypeOf(actual) != "number") Fail(path, expected, actual);
                    MatchRevision(Number(expected), Number(actual!), path);
                    return;
                }
                if (expectedType != TypeOf(actual)) { Fail(path, expected, actual); return; }
                var equal = expectedType switch
                {
                    "number" => Number(expected) == Number(actual!),
                    "string" => expected.GetValue<string>() == actual!.GetValue<string>(),
                    "boolean" => expected.GetValue<bool>() == actual!.GetValue<bool>(),
                    _ => false,
                };
                if (!equal) Fail(path, expected, actual);
                return;
        }
    }

    // A frame's changes: the changes of each field in order, and the same set of fields (README "Changes").
    private void MatchChanges(JsonArray expected, JsonNode? actual, string path)
    {
        if (actual is not JsonArray actualArray) { Fail(path, expected, actual); return; }
        var expectedFields = ByField(expected, path);
        var actualFields = ByField(actualArray, path);
        if (!expectedFields.Keys.Order(StringComparer.Ordinal).SequenceEqual(actualFields.Keys.Order(StringComparer.Ordinal)))
            throw new ScenarioFailure($"{path}: the changed fields differ: expected [{string.Join(", ", expectedFields.Keys)}], " +
                $"actual [{string.Join(", ", actualFields.Keys)}].\nactual: {Abbreviate(actual)}");
        foreach (var (field, changes) in expectedFields)
        {
            var actualChanges = actualFields[field];
            if (changes.Count != actualChanges.Count)
                throw new ScenarioFailure($"{path}: field '{field}' has {actualChanges.Count} changes, expected {changes.Count}.\n" +
                    $"expected: {Abbreviate(new JsonArray([.. changes.Select(change => change?.DeepClone())]))}\nactual:   {Abbreviate(actual)}");
            for (var index = 0; index < changes.Count; index++)
                Match(changes[index], actualChanges[index], $"{path}[{field}][{index}]", null);
        }
    }

    private static Dictionary<string, List<JsonNode?>> ByField(JsonArray changes, string path)
    {
        var fields = new Dictionary<string, List<JsonNode?>>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            var field = change is JsonObject value && value["field"] is JsonValue name && name.GetValueKind() == JsonValueKind.String
                ? name.GetValue<string>()
                : throw new ScenarioFailure($"{path}: a change without a field: {Abbreviate(change)}.");
            if (!fields.TryGetValue(field, out var list)) fields.Add(field, list = []);
            list.Add(change);
        }
        return fields;
    }

    private void MatchId(ScenarioId id, JsonNode? actual, string path)
    {
        if (id.Kind == "wire")
        {
            if (TypeOf(actual) != "string" || actual!.GetValue<string>() != id.Value) Fail(path, $"the wire id {id.Name} ({id.Value})", actual);
            return;
        }
        // A provided id binds the first time a message carries it and must match after that.
        if (!_provided.TryGetValue(id.Name, out var bound)) _provided.Add(id.Name, actual?.DeepClone());
        else if (!JsonNode.DeepEquals(bound, actual)) Fail(path, $"the provided id {id.Name} ({bound?.ToJsonString() ?? "null"})", actual);
    }

    // Revisions are compared by order (README "Revisions"): each distinct expected revision maps to the actual one
    // it first meets, and the mapping stays consistent and strictly increasing.
    private void MatchRevision(double expected, double actual, string path)
    {
        if (_revisions.TryGetValue(expected, out var mapped))
        {
            if (mapped != actual)
                throw new ScenarioFailure($"{path}: expected revision {expected} was revision {mapped} before, now {actual}.");
            return;
        }
        foreach (var (otherExpected, otherActual) in _revisions)
            if ((otherExpected < expected && otherActual >= actual) || (otherExpected > expected && otherActual <= actual))
                throw new ScenarioFailure($"{path}: expected revision {expected} maps to {actual}, out of order with " +
                    $"expected revision {otherExpected}, which was {otherActual}.");
        _revisions.Add(expected, actual);
    }

    private static string TypeOf(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        _ => node.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            var kind => kind.ToString(),
        },
    };

    // Numbers are compared as doubles, whatever their text.
    private static double Number(JsonNode node) => double.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static bool IsInteger(JsonNode node) => Math.Floor(Number(node)) == Number(node);

    private static void Fail(string path, object? expected, JsonNode? actual) =>
        throw new ScenarioFailure($"{path}: expected {(expected is JsonNode node ? Abbreviate(node) : expected ?? "null")}, " +
            $"actual {Abbreviate(actual)}.");

    internal static string Abbreviate(JsonNode? node)
    {
        var json = node?.ToJsonString() ?? "null";
        return json.Length <= 2048 ? json : $"{json[..2048]}... ({json.Length} chars)";
    }
}

// A scenario does not hold: the producer's messages differ, or the scenario is malformed.
internal sealed class ScenarioFailure(string message) : Exception(message);
