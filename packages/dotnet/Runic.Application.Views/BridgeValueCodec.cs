using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Runic.Application.Views;

/// <summary>
/// A generated, typed JSON value codec for a Runic bridge contract.
/// </summary>
/// <remarks>
/// Generated bridge code supplies direct readers and writers for every member.
/// This keeps application execution independent of reflection-based JSON
/// metadata, which is important for trimming and Native AOT.
/// </remarks>
public sealed class BridgeValueCodec<T>(Func<JsonElement, T> read, Action<Utf8JsonWriter, T> write)
{
    private readonly Func<JsonElement, T> _read = read ?? throw new ArgumentNullException(nameof(read));
    private readonly Action<Utf8JsonWriter, T> _write = write ?? throw new ArgumentNullException(nameof(write));

    /// <summary>Decodes one JSON value.</summary>
    public T Decode(JsonElement value) => _read(value);

    /// <summary>Decodes one complete JSON document.</summary>
    public T Decode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        return Decode(document.RootElement);
    }

    /// <summary>Writes one JSON value into the current writer context.</summary>
    public void Encode(Utf8JsonWriter writer, T value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _write(writer, value);
    }

    /// <summary>Writes a complete, canonical JSON value.</summary>
    public string Encode(T value) => BridgeWire.EncodeCanonical(writer => Encode(writer, value));

    /// <summary>
    /// Compares values by their canonical wire representation. It deliberately
    /// does not rely on CLR equality, which may have different semantics from
    /// the generated frontend contract.
    /// </summary>
    public bool StructuralEquals(T left, T right) =>
        BridgeWire.StructuralEquals(Encode(left), Encode(right));
}

/// <summary>
/// Static, allocation-conscious JSON primitives used by generated bridge
/// codecs. Values whose JavaScript number representation can lose precision
/// are represented as invariant strings on the wire.
/// </summary>
public static class BridgeWire
{
    /// <summary>Creates a consistent invalid-wire exception for generated readers.</summary>
    public static FormatException Invalid(string message) => new(message);

    /// <summary>Reads a named property from a JSON object.</summary>
    public static JsonElement RequiredProperty(JsonElement value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property))
            throw Invalid($"Expected required property '{name}'.");
        return property;
    }

    /// <summary>Reads a non-null JSON string.</summary>
    public static string ReadString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw Invalid("Expected a string.");
        return value.GetString() ?? throw Invalid("Expected a string.");
    }

    /// <summary>Compatibility spelling for a non-null JSON string.</summary>
    public static string ReadRequiredString(JsonElement value) => ReadString(value);

    /// <summary>Reads a JSON string or null.</summary>
    public static string? ReadNullableString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        _ => throw Invalid("Expected a string or null."),
    };

    /// <summary>Reads a JSON Boolean.</summary>
    public static bool ReadBoolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean()
        : throw Invalid("Expected a boolean.");

    /// <summary>Reads a JSON number as a 32-bit integer.</summary>
    public static int ReadInt32(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
        ? result
        : throw Invalid("Expected a 32-bit integer.");

    /// <summary>Reads a JSON number as a signed 8-bit integer.</summary>
    public static sbyte ReadInt8(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetSByte(out var result)
        ? result
        : throw Invalid("Expected an 8-bit integer.");

    /// <summary>Reads a JSON number as an unsigned 8-bit integer.</summary>
    public static byte ReadUInt8(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetByte(out var result)
        ? result
        : throw Invalid("Expected an unsigned 8-bit integer.");

    /// <summary>Reads a JSON number as a 16-bit integer.</summary>
    public static short ReadInt16(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt16(out var result)
        ? result
        : throw Invalid("Expected a 16-bit integer.");

    /// <summary>Reads a JSON number as an unsigned 16-bit integer.</summary>
    public static ushort ReadUInt16(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetUInt16(out var result)
        ? result
        : throw Invalid("Expected an unsigned 16-bit integer.");

    /// <summary>Reads a JSON number as an unsigned 32-bit integer.</summary>
    public static uint ReadUInt32(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var result)
        ? result
        : throw Invalid("Expected an unsigned 32-bit integer.");

    /// <summary>Reads a finite JSON number as a single-precision value.</summary>
    public static float ReadSingle(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var result) || !float.IsFinite(result))
            throw Invalid("Expected a finite single-precision number.");
        return result;
    }

    /// <summary>Reads an exact 64-bit integer represented as an invariant string.</summary>
    public static long ReadInt64(JsonElement value) => long.TryParse(ReadRequiredString(value), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var result)
        ? result
        : throw Invalid("Expected a 64-bit integer string.");

    /// <summary>The same as <see cref="ReadInt64(JsonElement)"/>.</summary>
    public static long ReadInt64String(JsonElement value) => ReadInt64(value);

    /// <summary>Reads an exact unsigned 64-bit integer represented as an invariant string.</summary>
    public static ulong ReadUInt64(JsonElement value) => ulong.TryParse(ReadString(value), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var result)
        ? result
        : throw Invalid("Expected an unsigned 64-bit integer string.");

    /// <summary>The same as <see cref="ReadUInt64(JsonElement)"/>.</summary>
    public static ulong ReadUInt64String(JsonElement value) => ReadUInt64(value);

    /// <summary>Reads an exact arbitrary-precision integer represented as an invariant string.</summary>
    public static BigInteger ReadBigInteger(JsonElement value) => BigInteger.TryParse(ReadString(value), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var result)
        ? result
        : throw Invalid("Expected an arbitrary-precision integer string.");

    /// <summary>Reads a finite JSON number as a double-precision value.</summary>
    public static double ReadDouble(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) || !double.IsFinite(result))
            throw Invalid("Expected a finite number.");
        return result;
    }

    /// <summary>Reads an exact decimal represented as an invariant string.</summary>
    public static decimal ReadDecimal(JsonElement value) => decimal.TryParse(ReadRequiredString(value), NumberStyles.Number,
        CultureInfo.InvariantCulture, out var result)
        ? result
        : throw Invalid("Expected a decimal string.");

    /// <summary>Reads a GUID string in <c>D</c> format.</summary>
    public static Guid ReadGuid(JsonElement value) => Guid.TryParseExact(ReadRequiredString(value), "D", out var result)
        ? result
        : throw Invalid("Expected a GUID in D format.");

    /// <summary>Reads an ISO <c>yyyy-MM-dd</c> date string.</summary>
    public static DateOnly ReadDateOnly(JsonElement value) => DateOnly.TryParseExact(ReadRequiredString(value), "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
        ? result
        : throw Invalid("Expected an ISO date.");

    // The "O" round-trip format requires exactly seven fractional digits, but
    // browsers write ISO-8601 with any precision (Date.toISOString uses three).
    // These patterns accept zero to seven digits and otherwise match "O".
    private const string IsoTime = "HH':'mm':'ss.FFFFFFF";
    private const string IsoDateTime = "yyyy'-'MM'-'dd'T'" + IsoTime;
    private static readonly string[] IsoDateTimeOffset = [IsoDateTime + "zzz", IsoDateTime + "'Z'"];

    /// <summary>Reads an ISO time string with zero to seven fractional digits.</summary>
    public static TimeOnly ReadTimeOnly(JsonElement value) => TimeOnly.TryParseExact(ReadRequiredString(value), IsoTime,
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
        ? result
        : throw Invalid("Expected an ISO time.");

    /// <summary>Reads an ISO date-time with an explicit offset or <c>Z</c>, preserving the offset.</summary>
    public static DateTimeOffset ReadDateTimeOffset(JsonElement value) => DateTimeOffset.TryParseExact(ReadRequiredString(value), IsoDateTimeOffset,
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result)
        ? result
        : throw Invalid("Expected an ISO date-time with an offset.");

    /// <summary>Reads an ISO date-time while preserving its UTC/local/unspecified kind.</summary>
    public static DateTime ReadDateTime(JsonElement value) => DateTime.TryParseExact(ReadString(value), IsoDateTime + "K",
        CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result)
        ? result
        : throw Invalid("Expected an ISO date-time.");

    /// <summary>Reads an invariant <c>c</c>-format time span string.</summary>
    public static TimeSpan ReadTimeSpan(JsonElement value) => TimeSpan.TryParseExact(ReadRequiredString(value), "c",
        CultureInfo.InvariantCulture, out var result)
        ? result
        : throw Invalid("Expected an invariant time span.");

    /// <summary>Reads a declared enum name.</summary>
    public static TEnum ReadEnum<TEnum>(JsonElement value) where TEnum : struct, Enum
    {
        var name = ReadRequiredString(value);
        if (!Enum.TryParse<TEnum>(name, ignoreCase: false, out var result)
            || Enum.GetName(result) is null)
            throw Invalid($"Expected a declared {typeof(TEnum).Name} name.");
        return result;
    }

    /// <summary>Writes a JSON string or <c>null</c>.</summary>
    public static void WriteNullableString(Utf8JsonWriter writer, string? value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }

    /// <summary>Writes a 64-bit integer as an invariant JSON string.</summary>
    public static void WriteInt64(Utf8JsonWriter writer, long value)
    {
        WriteIntegerString(writer, value);
    }

    /// <summary>Writes an unsigned 64-bit integer as an invariant JSON string.</summary>
    public static void WriteUInt64(Utf8JsonWriter writer, ulong value) => WriteIntegerString(writer, value);

    /// <summary>Writes an arbitrary-precision integer as an invariant JSON string.</summary>
    public static void WriteBigInteger(Utf8JsonWriter writer, BigInteger value) => WriteIntegerString(writer, value);

    /// <summary>Writes an exact integer as an invariant JSON string.</summary>
    public static void WriteIntegerString<TInteger>(Utf8JsonWriter writer, TInteger value)
        where TInteger : IBinaryInteger<TInteger>
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString(null, CultureInfo.InvariantCulture));
    }

    /// <summary>Writes a finite double-precision JSON number.</summary>
    public static void WriteDouble(Utf8JsonWriter writer, double value)
    {
        WriteFiniteNumber(writer, value);
    }

    /// <summary>Writes a finite single-precision JSON number.</summary>
    public static void WriteSingle(Utf8JsonWriter writer, float value)
    {
        WriteFiniteNumber(writer, value);
    }

    /// <summary>Writes a finite single-precision JSON number.</summary>
    public static void WriteFiniteNumber(Utf8JsonWriter writer, float value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "A bridge number must be finite.");
        writer.WriteNumberValue(value);
    }

    /// <summary>Writes a finite double-precision JSON number.</summary>
    public static void WriteFiniteNumber(Utf8JsonWriter writer, double value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "A bridge number must be finite.");
        writer.WriteNumberValue(value);
    }

    /// <summary>Writes a decimal as an invariant JSON string.</summary>
    public static void WriteDecimal(Utf8JsonWriter writer, decimal value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Writes a GUID string in <c>D</c> format.</summary>
    public static void WriteGuid(Utf8JsonWriter writer, Guid value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString("D"));
    }

    /// <summary>Writes an ISO <c>yyyy-MM-dd</c> date string.</summary>
    public static void WriteDateOnly(Utf8JsonWriter writer, DateOnly value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    /// <summary>Writes an ISO round-trip time string.</summary>
    public static void WriteTimeOnly(Utf8JsonWriter writer, TimeOnly value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>Writes an ISO round-trip date-time string that keeps the offset.</summary>
    public static void WriteDateTimeOffset(Utf8JsonWriter writer, DateTimeOffset value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        // Keep the offset: it is part of a DateTimeOffset value.
        writer.WriteStringValue(value.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>Writes an ISO date-time while preserving its UTC/local/unspecified kind.</summary>
    public static void WriteDateTime(Utf8JsonWriter writer, DateTime value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>Writes an invariant <c>c</c>-format time span string.</summary>
    public static void WriteTimeSpan(Utf8JsonWriter writer, TimeSpan value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString("c", CultureInfo.InvariantCulture));
    }

    /// <summary>Writes a declared enum name.</summary>
    public static void WriteEnum<TEnum>(Utf8JsonWriter writer, TEnum value) where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(writer);
        var name = Enum.GetName(value);
        if (name is null) throw new ArgumentOutOfRangeException(nameof(value), "A bridge enum value must be a declared name.");
        writer.WriteStringValue(name);
    }

    /// <summary>Writes a complete canonical JSON value.</summary>
    public static string EncodeCanonical(Action<Utf8JsonWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
            writer.Flush();
        }
        return Encoding.UTF8.GetString(Canonicalize(buffer.WrittenSpan));
    }

    /// <summary>Compares two complete JSON documents after canonicalization.</summary>
    public static bool StructuralEquals(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return Canonicalize(Encoding.UTF8.GetBytes(left)).AsSpan()
            .SequenceEqual(Canonicalize(Encoding.UTF8.GetBytes(right)));
    }

    /// <summary>Compares two JSON values after canonicalization.</summary>
    public static bool StructuralEquals(JsonElement left, JsonElement right) =>
        Canonicalize(left).AsSpan().SequenceEqual(Canonicalize(right));

    /// <summary>Returns a canonical UTF-8 encoding for a complete JSON document.</summary>
    public static byte[] Canonicalize(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        return Canonicalize(document.RootElement);
    }

    /// <summary>Returns a canonical UTF-8 encoding for one JSON value.</summary>
    public static byte[] Canonicalize(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, value);
            writer.Flush();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var properties = value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
                for (var index = 1; index < properties.Length; index++)
                    if (string.Equals(properties[index - 1].Name, properties[index].Name, StringComparison.Ordinal))
                        throw new FormatException("A bridge JSON object cannot contain duplicate property names.");
                writer.WriteStartObject();
                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            }
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new FormatException("A bridge value must be a JSON value.");
        }
    }
}

/// <summary>Marks a public data member as deliberately included in a generated bridge contract.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = false, Inherited = true)]
public sealed class RunicIncludeAttribute : Attribute;

/// <summary>Sets a stable JSON field or enum-case name without exposing CLR naming as protocol.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Enum,
    AllowMultiple = false, Inherited = false)]
public sealed class RunicAliasAttribute(string name) : Attribute
{
    /// <summary>The stable wire name.</summary>
    public string Name { get; } = !string.IsNullOrWhiteSpace(name)
        ? name
        : throw new ArgumentException("A Runic wire alias is required.", nameof(name));
}

/// <summary>Declares the complete derived-type set allowed at a polymorphic bridge boundary.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class RunicUnionAttribute(params Type[] cases) : Attribute
{
    /// <summary>The concrete case types.</summary>
    public Type[] Cases { get; } = cases is { Length: > 0 } && cases.All(type => type is not null)
        ? [.. cases]
        : throw new ArgumentException("A Runic union needs at least one concrete case.", nameof(cases));
}

/// <summary>Sets the stable discriminator used for one case of a Runic union.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class RunicUnionCaseAttribute(string name) : Attribute
{
    /// <summary>The stable wire name.</summary>
    public string Name { get; } = !string.IsNullOrWhiteSpace(name)
        ? name
        : throw new ArgumentException("A Runic union case name is required.", nameof(name));
}

/// <summary>Associates a contract type or member with an explicit AOT-safe bridge codec.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class RunicBridgeCodecAttribute(Type codecType) : Attribute
{
    /// <summary>The codec type, which implements <see cref="IRunicBridgeCodec{T}"/>.</summary>
    public Type CodecType { get; } = codecType ?? throw new ArgumentNullException(nameof(codecType));
}

/// <summary>
/// Supplies the TypeScript representation and decoder expression for an
/// explicit bridge codec. The expression must contain <c>$value</c>, which the
/// generator replaces with the untrusted JSON value at the call site.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class RunicCodecShapeAttribute(string typeScriptType, string decoderExpression, string encoderExpression = "$value") : Attribute
{
    /// <summary>The public TypeScript type of the value.</summary>
    public string TypeScriptType { get; } = !string.IsNullOrWhiteSpace(typeScriptType)
        ? typeScriptType
        : throw new ArgumentException("A TypeScript codec shape is required.", nameof(typeScriptType));

    /// <summary>Frontend conversion from the JSON wire value; <c>$value</c> is replaced with the JSON value.</summary>
    public string DecoderExpression { get; } = !string.IsNullOrWhiteSpace(decoderExpression)
        && decoderExpression.Contains("$value", StringComparison.Ordinal)
        ? decoderExpression
        : throw new ArgumentException("A codec decoder expression must contain the $value placeholder.", nameof(decoderExpression));

    /// <summary>
    /// Frontend conversion from the public TypeScript value to its JSON wire
    /// value. The generator replaces <c>$value</c> with the source expression.
    /// </summary>
    public string EncoderExpression { get; } = !string.IsNullOrWhiteSpace(encoderExpression)
        && encoderExpression.Contains("$value", StringComparison.Ordinal)
        ? encoderExpression
        : throw new ArgumentException("A codec encoder expression must contain the $value placeholder.", nameof(encoderExpression));
}

/// <summary>
/// Implement this interface with public static methods to define an explicit
/// bridge representation for a type the generated standard codec does not
/// cover. The generator verifies the closed generic type and emits direct
/// static calls, so no codec discovery occurs at runtime.
/// </summary>
public interface IRunicBridgeCodec<T>
{
    /// <summary>Decodes one JSON value.</summary>
    static abstract T Read(JsonElement value);
    /// <summary>Writes one JSON value.</summary>
    static abstract void Write(Utf8JsonWriter writer, T value);
}
