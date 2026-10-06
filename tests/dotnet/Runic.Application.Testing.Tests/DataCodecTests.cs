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
        CanonicalFormIsShared();
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

    // BridgeWire, checked-field equality, operation input digests and
    // interaction reply signatures share one canonical form.
    private static void CanonicalFormIsShared()
    {
        string Canonical(string json) => System.Text.Encoding.UTF8.GetString(BridgeWire.Canonicalize(System.Text.Encoding.UTF8.GetBytes(json)));
        foreach (var (input, expected) in new[]
        {
            ("1.0", "1"), ("1e0", "1"), ("10e-1", "1"), ("-0", "0"), ("-0.0e5", "0"), ("100", "100"), ("0.5", "0.5"),
            ("1.2500", "1.25"), ("0.000001", "0.000001"), ("1E-7", "1e-7"), ("123456789012345678901", "123456789012345678901"),
            ("1e21", "1e+21"), ("-12.5e30", "-1.25e+31"), ("0.1000000000000000000000000000001", "0.1000000000000000000000000000001"),
            ("1e-30", "1e-30"), ("9007199254740993", "9007199254740993"),
        })
            Require(Canonical(input) == expected, $"The canonical form of {input} is {Canonical(input)}, not {expected}.");
        // Exact: values that differ only beyond double or decimal precision stay distinct.
        Require(!BridgeWire.StructuralEquals("1e-30", "0") && !BridgeWire.StructuralEquals("9007199254740993", "9007199254740992")
            && !BridgeWire.StructuralEquals("0.1000000000000000000000000000001", "0.1"),
            "Canonical numbers were rounded.");
        Require(BridgeWire.StructuralEquals("{\"a\":[1.0,{\"y\":2,\"x\":1e0}]}", "{\"a\":[1,{\"x\":1,\"y\":2.00}]}"),
            "Canonical numbers were not compared by value inside nested values.");
        Require(Canonical("{\"b\":\"\\u0041<\",\"\\u00e9\":1,\"a\":{\"z\":true,\"m\":null}}")
            == "{\"a\":{\"m\":null,\"z\":true},\"b\":\"A\\u003C\",\"\\u00E9\":1}",
            "Canonical members were not sorted or strings were not re-escaped.");
        Require(Throws<FormatException>(() => BridgeWire.Canonicalize("{\"a\":1,\"a\":1}"u8))
            && Throws<FormatException>(() => BridgeWire.Canonicalize("{\"b\":1,\"a\":[{\"x\":1,\"y\":2,\"x\":3}]}"u8))
            && Throws<FormatException>(() => BridgeOperationRequest.CanonicalDigest("{\"a\":1,\"a\":2}")),
            "A canonical value accepted duplicate member names.");
        Require(BridgeOperationRequest.CanonicalDigest("{\"n\":1e-30}") != BridgeOperationRequest.CanonicalDigest("{\"n\":0}")
            && BridgeOperationRequest.CanonicalDigest("{\"n\":[1.50]}") == BridgeOperationRequest.CanonicalDigest("{\"n\":[15e-1]}"),
            "The operation digest did not use the shared canonical numbers.");
        // Generated writers emit declaration order, so a large array of unsorted
        // objects is the common case. Sorting must not copy the enclosing document.
        var rows = "[" + string.Join(",", Enumerable.Range(0, 16000).Select(id => $"{{\"label\":\"row {id}\",\"id\":{id}}}")) + "]";
        var rowBytes = System.Text.Encoding.UTF8.GetBytes(rows);
        _ = BridgeWire.Canonicalize(rowBytes);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var sortedRows = BridgeWire.Canonicalize(rowBytes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Require(System.Text.Encoding.UTF8.GetString(sortedRows).StartsWith("[{\"id\":0,\"label\":\"row 0\"},{\"id\":1,", StringComparison.Ordinal),
            "A large array of unsorted objects was not canonicalized.");
        Require(allocated < 8L * rowBytes.Length,
            $"Canonicalizing {rowBytes.Length} bytes of unsorted objects allocated {allocated} bytes.");
        Require(Canonical("{\"\\u00e9\":1,\"z\":2,\"\\u00e0\":3,\"a\":4}") == "{\"a\":4,\"z\":2,\"\\u00E0\":3,\"\\u00E9\":1}",
            "Decoded and ASCII member names were not ordered ordinally.");
        var codec = new BridgeValueCodec<decimal>(element => element.GetDecimal(), (writer, value) => writer.WriteNumberValue(value));
        Require(codec.StructuralEquals(1.0m, 1.00m) && codec.Encode(1.50m) == "1.5" && !codec.StructuralEquals(1m, 2m),
            "Codec equality did not compare numbers by value.");
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
