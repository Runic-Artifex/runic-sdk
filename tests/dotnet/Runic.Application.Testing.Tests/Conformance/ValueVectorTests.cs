using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Application.Views.Generated;
using TUnit.Core;

namespace Runic.Application.Testing.Tests.Conformance;

// The value vectors of the Views protocol 4 conformance corpus (specs/application/conformance/vectors/values, README
// "Values") against BridgeWire, the readers and writers that generated codecs call. Each case decodes the input; a
// valid input's encoding must be the normal form in `expect`, and an invalid one must fail as invalid input.
public sealed class ValueVectorTests
{
    // The enum of the vectors' `names` parameter.
    private enum VectorLevel { Low, Normal, High }

    public static IEnumerable<Func<string>> Cases() => Vectors().Select(vector => vector["id"]!.GetValue<string>())
        .Select(id => (Func<string>)(() => id));

    [Test]
    [MethodDataSource(nameof(Cases))]
    [DisplayName("$id")]
    public void BridgeWireReadsAndWritesTheNormalForm(string id)
    {
        var vector = Vectors().Single(candidate => candidate["id"]!.GetValue<string>() == id);
        var type = vector["type"]!.GetValue<string>();
        var parameters = vector["parameters"]?.AsObject();
        var nullable = parameters?["nullable"]?.GetValue<bool>() == true;
        var expect = vector["expect"]!.AsObject();
        var input = vector["input"]!.AsObject();
        var bytes = input["encoding"]!.GetValue<string>() switch
        {
            "json" => Encoding.UTF8.GetBytes(input["value"]?.ToJsonString() ?? "null"),
            "utf8" => Encoding.UTF8.GetBytes(input["value"]!.GetValue<string>()),
            var encoding => throw new InvalidOperationException($"Unknown input encoding {encoding}."),
        };

        object? decoded;
        string encoded;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            var element = document.RootElement;
            if (element.ValueKind == JsonValueKind.Null && (nullable || type == "json" && parameters?["nullable"]?.GetValue<bool>() != false))
                (decoded, encoded) = (null, "null");
            else
            {
                var codec = Codec(type);
                decoded = codec.Read(element);
                encoded = Write(writer => codec.Write(writer, decoded));
            }
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException
            or ArgumentException or OverflowException)
        {
            Require(expect["error"]?["code"]?.GetValue<string>() == "invalid-value",
                $"{id}: the valid input was rejected: {error.Message}");
            return;
        }
        Require(expect["error"] is null, $"{id}: the invalid input was read as {encoded}.");
        var expected = expect["text"] is { } text ? JsonNode.Parse(text.GetValue<string>(), documentOptions: new() { MaxDepth = 128 }) : expect["value"];

        // A date-time with an offset is read as local time (language-mappings.md, Values), so .NET writes the machine's
        // offset: the instant must be the same.
        if (type == "date-time" && decoded is DateTime { Kind: DateTimeKind.Local } local)
        {
            var instant = DateTimeOffset.Parse(expected!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
            Require(local.ToUniversalTime() == instant.UtcDateTime, $"{id}: read {local:O}, expected {instant:O}.");
            return;
        }
        Require(JsonValueEquals(JsonNode.Parse(encoded, documentOptions: new() { MaxDepth = 128 }), expected),
            $"{id}: wrote {encoded}, expected {expected?.ToJsonString() ?? "null"}.");
    }

    private sealed record WireCodec(Func<JsonElement, object?> Read, Action<Utf8JsonWriter, object?> Write);

    private static WireCodec Codec(string type) => type switch
    {
        "boolean" => new(element => BridgeWire.ReadBoolean(element), (writer, value) => writer.WriteBooleanValue((bool)value!)),
        "int8" => new(element => BridgeWire.ReadInt8(element), (writer, value) => writer.WriteNumberValue((sbyte)value!)),
        "uint8" => new(element => BridgeWire.ReadUInt8(element), (writer, value) => writer.WriteNumberValue((byte)value!)),
        "int16" => new(element => BridgeWire.ReadInt16(element), (writer, value) => writer.WriteNumberValue((short)value!)),
        "uint16" => new(element => BridgeWire.ReadUInt16(element), (writer, value) => writer.WriteNumberValue((ushort)value!)),
        "int32" => new(element => BridgeWire.ReadInt32(element), (writer, value) => writer.WriteNumberValue((int)value!)),
        "uint32" => new(element => BridgeWire.ReadUInt32(element), (writer, value) => writer.WriteNumberValue((uint)value!)),
        "float32" => new(element => BridgeWire.ReadSingle(element), (writer, value) => BridgeWire.WriteSingle(writer, (float)value!)),
        "float64" => new(element => BridgeWire.ReadDouble(element), (writer, value) => BridgeWire.WriteDouble(writer, (double)value!)),
        "int64" => new(element => BridgeWire.ReadInt64(element), (writer, value) => BridgeWire.WriteInt64(writer, (long)value!)),
        "uint64" => new(element => BridgeWire.ReadUInt64(element), (writer, value) => BridgeWire.WriteUInt64(writer, (ulong)value!)),
        "big-integer" => new(element => BridgeWire.ReadBigInteger(element),
            (writer, value) => BridgeWire.WriteBigInteger(writer, (System.Numerics.BigInteger)value!)),
        "decimal" => new(element => BridgeWire.ReadDecimal(element), (writer, value) => BridgeWire.WriteDecimal(writer, (decimal)value!)),
        "string" => new(element => BridgeWire.ReadString(element), (writer, value) => writer.WriteStringValue((string)value!)),
        "uuid" => new(element => BridgeWire.ReadGuid(element), (writer, value) => BridgeWire.WriteGuid(writer, (Guid)value!)),
        "local-date" => new(element => BridgeWire.ReadDateOnly(element), (writer, value) => BridgeWire.WriteDateOnly(writer, (DateOnly)value!)),
        "local-time" => new(element => BridgeWire.ReadTimeOnly(element), (writer, value) => BridgeWire.WriteTimeOnly(writer, (TimeOnly)value!)),
        "date-time" => new(element => BridgeWire.ReadDateTime(element), (writer, value) => BridgeWire.WriteDateTime(writer, (DateTime)value!)),
        "offset-date-time" => new(element => BridgeWire.ReadDateTimeOffset(element),
            (writer, value) => BridgeWire.WriteDateTimeOffset(writer, (DateTimeOffset)value!)),
        "duration" => new(element => BridgeWire.ReadTimeSpan(element), (writer, value) => BridgeWire.WriteTimeSpan(writer, (TimeSpan)value!)),
        "enum" => new(element => BridgeWire.ReadEnum<VectorLevel>(element),
            (writer, value) => BridgeWire.WriteEnum(writer, (VectorLevel)value!)),
        "json" => new(element => element.ValueKind == JsonValueKind.Null ? throw new FormatException("Expected a value.") : element.Clone(),
            (writer, value) => ((JsonElement)value!).WriteTo(writer)),
        _ => throw new InvalidOperationException($"No codec for the wire type {type}."),
    };

    private static string Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = 128 })) write(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // JSON values compared as the corpus compares them: numbers as doubles, object members in any order.
    private static bool JsonValueEquals(JsonNode? left, JsonNode? right) => (left, right) switch
    {
        (null, null) => true,
        (JsonObject first, JsonObject second) => first.Count == second.Count
            && first.All(member => second.TryGetPropertyValue(member.Key, out var other) && JsonValueEquals(member.Value, other)),
        (JsonArray first, JsonArray second) => first.Count == second.Count
            && first.Zip(second).All(pair => JsonValueEquals(pair.First, pair.Second)),
        (JsonValue first, JsonValue second) when first.GetValueKind() == JsonValueKind.Number && second.GetValueKind() == JsonValueKind.Number =>
            first.GetValue<double>() == second.GetValue<double>(),
        (JsonValue first, JsonValue second) => first.ToJsonString() == second.ToJsonString(),
        _ => false,
    };

    private static IEnumerable<JsonObject> Vectors()
    {
        var files = Directory.GetFiles(Path.Combine(ConformanceCorpus.Root, "vectors", "values"), "*.json")
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Require(files.Length > 0, $"No value vectors under {ConformanceCorpus.Root}.");
        foreach (var file in files)
            foreach (var testCase in JsonNode.Parse(File.ReadAllText(file))!["cases"]!.AsArray())
                yield return testCase!.AsObject();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
