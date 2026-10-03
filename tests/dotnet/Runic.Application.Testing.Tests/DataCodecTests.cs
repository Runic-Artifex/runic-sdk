using System.Text.Json;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// Invoked by the test application's top-level Program. Kept independent of
// generated bridges so the wire primitives retain their exact-value contract.
internal static class DataCodecTests
{
    internal static void Run()
    {
        var codec = new BridgeValueCodec<WireSample>(Read, Write);
        var sample = new WireSample(long.MaxValue, 1234567890.123456789m,
            new DateTime(2026, 9, 28, 10, 11, 12, DateTimeKind.Unspecified),
            TimeSpan.FromTicks(123456789));
        var encoded = codec.Encode(sample);
        Require(encoded.Contains("\"9223372036854775807\"", StringComparison.Ordinal),
            "Int64 must remain an exact decimal string on the wire.");
        Require(encoded.Contains("\"1234567890.123456789\"", StringComparison.Ordinal),
            "Decimal must remain an exact decimal string on the wire.");
        var decoded = codec.Decode(encoded);
        Require(decoded == sample && decoded.When.Kind is DateTimeKind.Unspecified,
            "Generated-style direct codecs did not round-trip an exact DTO value.");

        Require(BridgeWire.StructuralEquals("{\"b\":2,\"a\":1}", "{\"a\":1,\"b\":2}"),
            "Structural equality must order object keys canonically.");
        Require(Throws<FormatException>(() => BridgeWire.ReadInt64(Json("9007199254740993"))),
            "An unsafe JSON number must not be accepted as an Int64 string.");
        Require(Throws<FormatException>(() => BridgeWire.ReadDouble(Json("1e999"))),
            "Non-finite floating-point input must be rejected.");
        Require(BridgeWire.ReadTimeSpan(Json("\"-1.02:03:04.5000007\""))
            == -TimeSpan.FromDays(1) - TimeSpan.FromHours(2) - TimeSpan.FromMinutes(3)
                - TimeSpan.FromSeconds(4) - TimeSpan.FromMilliseconds(500) - TimeSpan.FromTicks(7),
            "Invariant TimeSpan decoding did not preserve a signed day and fraction.");
        Require(BridgeWire.ReadTimeSpan(Json("\"10675199.02:48:05.4775807\"")) == TimeSpan.MaxValue
            && BridgeWire.ReadTimeSpan(Json("\"-10675199.02:48:05.4775808\"")) == TimeSpan.MinValue,
            "Invariant TimeSpan decoding did not accept both tick-range boundaries.");
        Require(Throws<FormatException>(() => BridgeWire.ReadTimeSpan(Json("\"24:00:00\"")))
            && Throws<FormatException>(() => BridgeWire.ReadTimeSpan(Json("\"1.24:00:00\"")))
            && Throws<FormatException>(() => BridgeWire.ReadTimeSpan(Json("\"10675199.02:48:05.4775808\""))),
            "Invariant TimeSpan decoding accepted an invalid component or tick overflow.");

        // JavaScript writes ISO-8601 with millisecond precision (toISOString)
        // or none at all; .NET "O" output has seven digits. Accept all of them.
        var browserUtc = BridgeWire.ReadDateTime(Json("\"2026-10-03T12:34:56.789Z\""));
        Require(browserUtc == new DateTime(2026, 10, 3, 12, 34, 56, 789, DateTimeKind.Utc) && browserUtc.Kind is DateTimeKind.Utc,
            "A browser ISO date-time with three fractional digits was not read as UTC.");
        Require(BridgeWire.ReadDateTime(Json("\"2026-10-03T12:34:56\"")) is { Kind: DateTimeKind.Unspecified, Second: 56 }
            && BridgeWire.ReadDateTime(Json("\"2026-10-03T12:34:56.1234567\"")).Ticks % TimeSpan.TicksPerSecond == 1234567,
            "An ISO date-time without fraction or with seven digits was not read.");
        Require(BridgeWire.ReadDateTimeOffset(Json("\"2026-10-03T12:34:56.789Z\"")) == new DateTimeOffset(2026, 10, 3, 12, 34, 56, 789, TimeSpan.Zero)
            && BridgeWire.ReadDateTimeOffset(Json("\"2026-10-03T14:34:56+02:00\"")).Offset == TimeSpan.FromHours(2),
            "A browser ISO offset date-time was not read with its offset.");
        Require(Throws<FormatException>(() => BridgeWire.ReadDateTimeOffset(Json("\"2026-10-03T12:34:56\""))),
            "A date-time without an offset was accepted as a DateTimeOffset.");
        Require(BridgeWire.ReadTimeOnly(Json("\"08:15:00\"")) == new TimeOnly(8, 15)
            && BridgeWire.ReadTimeOnly(Json("\"08:15:00.5\"")) == new TimeOnly(8, 15, 0, 500),
            "An ISO time without seven fractional digits was not read.");
        var offset = new DateTimeOffset(2026, 10, 3, 14, 34, 56, TimeSpan.FromHours(2));
        var offsetCodec = new BridgeValueCodec<DateTimeOffset>(BridgeWire.ReadDateTimeOffset, BridgeWire.WriteDateTimeOffset);
        var encodedOffset = offsetCodec.Encode(offset);
        Require(Json(encodedOffset).GetString() == "2026-10-03T14:34:56.0000000+02:00" && offsetCodec.Decode(encodedOffset) is var decodedOffset
            && decodedOffset == offset && decodedOffset.Offset == offset.Offset,
            $"A DateTimeOffset did not keep its offset on the wire: {encodedOffset}.");
    }

    private static WireSample Read(JsonElement element) => new(
        BridgeWire.ReadInt64(BridgeWire.RequiredProperty(element, "id")),
        BridgeWire.ReadDecimal(BridgeWire.RequiredProperty(element, "amount")),
        BridgeWire.ReadDateTime(BridgeWire.RequiredProperty(element, "when")),
        BridgeWire.ReadTimeSpan(BridgeWire.RequiredProperty(element, "duration")));

    private static void Write(Utf8JsonWriter writer, WireSample value)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("id"); BridgeWire.WriteInt64(writer, value.Id);
        writer.WritePropertyName("amount"); BridgeWire.WriteDecimal(writer, value.Amount);
        writer.WritePropertyName("when"); BridgeWire.WriteDateTime(writer, value.When);
        writer.WritePropertyName("duration"); BridgeWire.WriteTimeSpan(writer, value.Duration);
        writer.WriteEndObject();
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record WireSample(long Id, decimal Amount, DateTime When, TimeSpan Duration);
}
