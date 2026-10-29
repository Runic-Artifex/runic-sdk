using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Runic.Application.Views;

namespace Runic.Application.Views.Generated;

/// <summary>
/// A generated, typed JSON value codec for a Runic bridge contract.
/// </summary>
/// <remarks>
/// Generated bridge code supplies direct readers and writers for every member.
/// This keeps application execution independent of reflection-based JSON
/// metadata, which is important for trimming and Native AOT.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
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
    public string Encode(T value) => BridgeCanonicalJson.Encode(_write, value);

    /// <summary>
    /// Compares values by their canonical wire representation. It deliberately
    /// does not rely on CLR equality, which may have different semantics from
    /// the generated frontend contract.
    /// </summary>
    public bool StructuralEquals(T left, T right) => BridgeCanonicalJson.Equal(_write, left, right);
}

/// <summary>
/// Static, allocation-conscious JSON primitives used by generated bridge
/// codecs. Values whose JavaScript number representation can lose precision
/// are represented as invariant strings on the wire.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
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

    // The longest number string a reader parses. BigInteger parsing is superlinear in the
    // number of digits; no exact wire number of a generated contract needs more.
    private const int MaximumNumberStringLength = 4_096;

    private static string ReadNumberString(JsonElement value, string message)
    {
        var text = ReadString(value);
        return text.Length <= MaximumNumberStringLength ? text : throw Invalid(message);
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

    // Integer wire types are read by value (values.md, Scalars): 1.0, 1e2 and -0 are integers.
    private static double ReadIntegral(JsonElement value, double minimum, double maximum, string message)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            && Math.Truncate(number) == number && number >= minimum && number <= maximum)
            return number == 0 ? 0 : number;
        throw Invalid(message);
    }

    /// <summary>Reads a JSON number whose value is a 32-bit integer.</summary>
    public static int ReadInt32(JsonElement value) => (int)ReadIntegral(value, int.MinValue, int.MaxValue, "Expected a 32-bit integer.");

    /// <summary>Reads a JSON number whose value is a signed 8-bit integer.</summary>
    public static sbyte ReadInt8(JsonElement value) => (sbyte)ReadIntegral(value, sbyte.MinValue, sbyte.MaxValue, "Expected an 8-bit integer.");

    /// <summary>Reads a JSON number whose value is an unsigned 8-bit integer.</summary>
    public static byte ReadUInt8(JsonElement value) => (byte)ReadIntegral(value, byte.MinValue, byte.MaxValue, "Expected an unsigned 8-bit integer.");

    /// <summary>Reads a JSON number whose value is a 16-bit integer.</summary>
    public static short ReadInt16(JsonElement value) => (short)ReadIntegral(value, short.MinValue, short.MaxValue, "Expected a 16-bit integer.");

    /// <summary>Reads a JSON number whose value is an unsigned 16-bit integer.</summary>
    public static ushort ReadUInt16(JsonElement value) => (ushort)ReadIntegral(value, ushort.MinValue, ushort.MaxValue, "Expected an unsigned 16-bit integer.");

    /// <summary>Reads a JSON number whose value is an unsigned 32-bit integer.</summary>
    public static uint ReadUInt32(JsonElement value) => (uint)ReadIntegral(value, uint.MinValue, uint.MaxValue, "Expected an unsigned 32-bit integer.");

    /// <summary>Reads a JSON number as the nearest single-precision value, which must be finite.</summary>
    public static float ReadSingle(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw Invalid("Expected a finite single-precision number.");
        var single = (float)number;
        if (!float.IsFinite(single)) throw Invalid("Expected a finite single-precision number.");
        return single == 0 ? 0f : single;
    }

    // An exact integer's text: -?(0|[1-9][0-9]*) (values.md, Scalars).
    private static bool IsIntegerText(ReadOnlySpan<char> text)
    {
        if (text.StartsWith('-')) text = text[1..];
        if (text.IsEmpty) return false;
        if (text[0] == '0') return text.Length == 1;
        foreach (var character in text)
            if (character is < '0' or > '9') return false;
        return true;
    }

    private static string ReadIntegerText(JsonElement value, string message)
    {
        var text = ReadNumberString(value, message);
        return IsIntegerText(text) ? text : throw Invalid(message);
    }

    /// <summary>Reads an exact 64-bit integer represented as a decimal string.</summary>
    public static long ReadInt64(JsonElement value) => long.TryParse(ReadIntegerText(value, "Expected a 64-bit integer string."),
        NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result)
        ? result
        : throw Invalid("Expected a 64-bit integer string.");

    /// <summary>The same as <see cref="ReadInt64(JsonElement)"/>.</summary>
    public static long ReadInt64String(JsonElement value) => ReadInt64(value);

    /// <summary>Reads an exact unsigned 64-bit integer represented as a decimal string.</summary>
    public static ulong ReadUInt64(JsonElement value) => ulong.TryParse(ReadIntegerText(value, "Expected an unsigned 64-bit integer string."),
        NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result)
        ? result
        : throw Invalid("Expected an unsigned 64-bit integer string.");

    /// <summary>The same as <see cref="ReadUInt64(JsonElement)"/>.</summary>
    public static ulong ReadUInt64String(JsonElement value) => ReadUInt64(value);

    /// <summary>Reads an exact arbitrary-precision integer represented as a decimal string.</summary>
    public static BigInteger ReadBigInteger(JsonElement value) => BigInteger.TryParse(ReadIntegerText(value, "Expected an arbitrary-precision integer string."),
        NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result)
        ? result
        : throw Invalid("Expected an arbitrary-precision integer string.");

    /// <summary>Reads a finite JSON number as a double-precision value.</summary>
    public static double ReadDouble(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) || !double.IsFinite(result))
            throw Invalid("Expected a finite number.");
        return result == 0 ? 0 : result;
    }

    // 2^96, the bound of a decimal's digits read as an integer.
    private const string DecimalCoefficientLimit = "79228162514264337593543950336";

    /// <summary>
    /// Reads an exact decimal string, <c>-?(0|[1-9][0-9]*)(\.[0-9]+)?</c> with at most 28 digits after the point and
    /// digits below 2^96. The scale is the number of digits after the point.
    /// </summary>
    public static decimal ReadDecimal(JsonElement value)
    {
        const string message = "Expected a decimal string.";
        var text = ReadNumberString(value, message);
        var unsigned = text.AsSpan(text.StartsWith('-') ? 1 : 0);
        var point = unsigned.IndexOf('.');
        var whole = point < 0 ? unsigned : unsigned[..point];
        var fraction = point < 0 ? [] : unsigned[(point + 1)..];
        if (whole.IsEmpty || whole.StartsWith('-') || !IsIntegerText(whole) || point >= 0 && fraction.IsEmpty || fraction.Length > 28)
            throw Invalid(message);
        foreach (var character in fraction)
            if (character is < '0' or > '9') throw Invalid(message);
        // The digits without the point, read as an integer, are below 2^96.
        var significant = 0;
        var compared = 0;
        Count(whole, ref significant, ref compared);
        Count(fraction, ref significant, ref compared);
        if (significant > DecimalCoefficientLimit.Length || significant == DecimalCoefficientLimit.Length && compared >= 0)
            throw Invalid(message);
        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw Invalid(message);

        // Counts the significant digits and compares them with 2^96 digit by digit.
        static void Count(ReadOnlySpan<char> digits, ref int significant, ref int compared)
        {
            foreach (var character in digits)
            {
                if (significant == 0 && character == '0') continue;
                if (significant < DecimalCoefficientLimit.Length && compared == 0)
                    compared = character.CompareTo(DecimalCoefficientLimit[significant]);
                significant++;
            }
        }
    }

    /// <summary>Reads a UUID string of 8-4-4-4-12 hexadecimal digits in either case.</summary>
    public static Guid ReadGuid(JsonElement value) => Guid.TryParseExact(ReadRequiredString(value), "D", out var result)
        ? result
        : throw Invalid("Expected a UUID.");

    /// <summary>Reads an RFC 3339 <c>full-date</c> (<c>yyyy-MM-dd</c>).</summary>
    public static DateOnly ReadDateOnly(JsonElement value) => DateOnly.TryParseExact(ReadRequiredString(value), "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
        ? result
        : throw Invalid("Expected an ISO date.");

    // Readers accept a fraction of 1 to 7 digits (browsers write 3); writers drop its trailing zeros and the point of
    // a zero fraction, which the F specifiers do.
    private const string IsoTime = "HH':'mm':'ss.FFFFFFF";
    private const string IsoDate = "yyyy'-'MM'-'dd";
    private const string IsoDateTime = IsoDate + "'T'" + IsoTime;
    private static readonly string[] IsoDateTimeOffset = [IsoDateTime + "zzz", IsoDateTime + "'Z'"];

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
            if (character is < '0' or > '9') return false;
        return true;
    }

    // hh:mm:ss with an optional fraction of 1 to 7 digits; the ranges are checked by the parser.
    private static bool IsTimeText(ReadOnlySpan<char> text) =>
        text.Length >= 8 && IsDigits(text[..2]) && text[2] == ':' && IsDigits(text[3..5]) && text[5] == ':' && IsDigits(text[6..8])
        && (text.Length == 8 || text[8] == '.' && text.Length is >= 10 and <= 16 && IsDigits(text[9..]));

    // A date, T and a time, then nothing, Z or ±hh:mm within ±14:00 (values.md, date-time).
    private static bool IsDateTimeText(ReadOnlySpan<char> text, bool offsetRequired)
    {
        if (text.Length < 19 || !IsDigits(text[..4]) || text[4] != '-' || !IsDigits(text[5..7]) || text[7] != '-'
            || !IsDigits(text[8..10]) || text[10] != 'T')
            return false;
        var time = text[11..];
        var zone = time.IndexOfAny('Z', '+', '-');
        if (zone < 0) return !offsetRequired && IsTimeText(time);
        if (!IsTimeText(time[..zone])) return false;
        var offset = time[zone..];
        if (offset is "Z") return true;
        if (offset.Length != 6 || !IsDigits(offset[1..3]) || offset[3] != ':' || !IsDigits(offset[4..])) return false;
        var minutes = int.Parse(offset[1..3], CultureInfo.InvariantCulture) * 60 + int.Parse(offset[4..], CultureInfo.InvariantCulture);
        return int.Parse(offset[4..], CultureInfo.InvariantCulture) < 60 && minutes <= 14 * 60;
    }

    /// <summary>Reads an RFC 3339 <c>partial-time</c> with a fraction of up to seven digits.</summary>
    public static TimeOnly ReadTimeOnly(JsonElement value)
    {
        var text = ReadRequiredString(value);
        return IsTimeText(text) && TimeOnly.TryParseExact(text, IsoTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            ? result
            : throw Invalid("Expected an ISO time.");
    }

    /// <summary>Reads an RFC 3339 date-time whose offset (<c>Z</c> or <c>±hh:mm</c>) is required, keeping the offset.</summary>
    public static DateTimeOffset ReadDateTimeOffset(JsonElement value)
    {
        var text = ReadRequiredString(value);
        return IsDateTimeText(text, offsetRequired: true) && DateTimeOffset.TryParseExact(text, IsoDateTimeOffset,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result)
            ? result
            : throw Invalid("Expected an ISO date-time with an offset.");
    }

    /// <summary>
    /// Reads an RFC 3339 date-time with an optional offset: none is <see cref="DateTimeKind.Unspecified"/>, <c>Z</c> is
    /// <see cref="DateTimeKind.Utc"/>, and an offset is <see cref="DateTimeKind.Local"/>, converted to local time.
    /// </summary>
    public static DateTime ReadDateTime(JsonElement value)
    {
        var text = ReadString(value);
        return IsDateTimeText(text, offsetRequired: false) && DateTime.TryParseExact(text, IsoDateTime + "K",
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result)
            ? result
            : throw Invalid("Expected an ISO date-time.");
    }

    /// <summary>
    /// Reads an ISO 8601 duration <c>-?P(nD)?(T(nH)?(nM)?(n(.f)?S)?)?</c> (values.md, duration). A day is 24 hours;
    /// components may exceed their usual range, and the total must fit a <see cref="TimeSpan"/>.
    /// </summary>
    public static TimeSpan ReadTimeSpan(JsonElement value) => TryParseDuration(ReadRequiredString(value), out var result)
        ? result
        : throw Invalid("Expected an ISO 8601 duration.");

    private static bool TryParseDuration(ReadOnlySpan<char> text, out TimeSpan result)
    {
        result = default;
        var negative = text.StartsWith('-');
        if (negative) text = text[1..];
        if (!text.StartsWith('P')) return false;
        text = text[1..];
        Int128 ticks = 0;
        var components = 0;
        try
        {
            if (TryComponent(ref text, 'D', out var days)) { ticks += days * TimeSpan.TicksPerDay; components++; }
            if (text.StartsWith('T'))
            {
                text = text[1..];
                var timeComponents = 0;
                if (TryComponent(ref text, 'H', out var hours)) { ticks += hours * TimeSpan.TicksPerHour; timeComponents++; }
                if (TryComponent(ref text, 'M', out var minutes)) { ticks += minutes * TimeSpan.TicksPerMinute; timeComponents++; }
                if (TrySeconds(ref text, out var seconds)) { ticks += seconds; timeComponents++; }
                if (timeComponents == 0) return false;
                components += timeComponents;
            }
        }
        catch (OverflowException)
        {
            return false;
        }
        if (!text.IsEmpty || components == 0) return false;
        if (negative) ticks = -ticks;
        if (ticks < long.MinValue || ticks > long.MaxValue) return false;
        result = new TimeSpan((long)ticks);
        return true;

        static bool TryComponent(ref ReadOnlySpan<char> text, char designator, out Int128 value)
        {
            value = 0;
            var length = DigitCount(text);
            if (length == 0 || length >= text.Length || text[length] != designator) return false;
            foreach (var character in text[..length]) value = checked(value * 10 + (character - '0'));
            text = text[(length + 1)..];
            return true;
        }

        static bool TrySeconds(ref ReadOnlySpan<char> text, out Int128 ticks)
        {
            ticks = 0;
            var length = DigitCount(text);
            if (length == 0) return false;
            var rest = text[length..];
            var fraction = ReadOnlySpan<char>.Empty;
            if (rest.StartsWith('.'))
            {
                var digits = DigitCount(rest[1..]);
                if (digits is 0 or > 7) return false;
                fraction = rest.Slice(1, digits);
                rest = rest[(digits + 1)..];
            }
            if (!rest.StartsWith('S')) return false;
            Int128 seconds = 0;
            foreach (var character in text[..length]) seconds = checked(seconds * 10 + (character - '0'));
            Int128 fractionTicks = 0;
            for (var index = 0; index < 7; index++)
                fractionTicks = fractionTicks * 10 + (index < fraction.Length ? fraction[index] - '0' : 0);
            ticks = checked(seconds * TimeSpan.TicksPerSecond + fractionTicks);
            text = rest[1..];
            return true;
        }

        static int DigitCount(ReadOnlySpan<char> text)
        {
            var count = 0;
            while (count < text.Length && text[count] is >= '0' and <= '9') count++;
            return count;
        }
    }
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

    /// <summary>Writes a 64-bit integer as a decimal JSON string.</summary>
    public static void WriteInt64(Utf8JsonWriter writer, long value)
    {
        WriteIntegerString(writer, value);
    }

    /// <summary>Writes an unsigned 64-bit integer as a decimal JSON string.</summary>
    public static void WriteUInt64(Utf8JsonWriter writer, ulong value) => WriteIntegerString(writer, value);

    /// <summary>Writes an arbitrary-precision integer as a decimal JSON string.</summary>
    public static void WriteBigInteger(Utf8JsonWriter writer, BigInteger value) => WriteIntegerString(writer, value);

    /// <summary>Writes an exact integer as a decimal JSON string.</summary>
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

    /// <summary>
    /// Writes a finite single-precision value as the shortest decimal number that rounds to it (<c>0.1</c>, not
    /// <c>0.100000001</c>); negative zero is written as <c>0</c>.
    /// </summary>
    public static void WriteFiniteNumber(Utf8JsonWriter writer, float value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "A bridge number must be finite.");
        // "R" is the shortest text that round-trips the single; the JSON number is the double nearest that text.
        writer.WriteNumberValue(value == 0 ? 0d : double.Parse(value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
    }

    /// <summary>Writes a finite double-precision JSON number; negative zero is written as <c>0</c>.</summary>
    public static void WriteFiniteNumber(Utf8JsonWriter writer, double value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "A bridge number must be finite.");
        writer.WriteNumberValue(value == 0 ? 0d : value);
    }

    /// <summary>Writes a decimal as a JSON string with its scale; a negative zero loses its sign.</summary>
    public static void WriteDecimal(Utf8JsonWriter writer, decimal value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var text = value.ToString(CultureInfo.InvariantCulture);
        writer.WriteStringValue(value == 0 && text.StartsWith('-') ? text[1..] : text);
    }

    /// <summary>Writes a UUID string of lowercase hexadecimal digits in <c>D</c> format.</summary>
    public static void WriteGuid(Utf8JsonWriter writer, Guid value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString("D"));
    }

    /// <summary>Writes an RFC 3339 <c>full-date</c> (<c>yyyy-MM-dd</c>).</summary>
    public static void WriteDateOnly(Utf8JsonWriter writer, DateOnly value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    /// <summary>Writes a time without trailing zeros in its fraction (<c>10:30:00</c>, <c>10:30:00.5</c>).</summary>
    public static void WriteTimeOnly(Utf8JsonWriter writer, TimeOnly value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString(IsoTime, CultureInfo.InvariantCulture));
    }

    /// <summary>Writes a date-time with its offset, <c>Z</c> for a zero offset.</summary>
    public static void WriteDateTimeOffset(Utf8JsonWriter writer, DateTimeOffset value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString(IsoDateTime, CultureInfo.InvariantCulture)
            + (value.Offset == TimeSpan.Zero ? "Z" : value.ToString("zzz", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Writes a date-time: nothing after the time for <see cref="DateTimeKind.Unspecified"/>, <c>Z</c> for
    /// <see cref="DateTimeKind.Utc"/>, and the machine's offset for <see cref="DateTimeKind.Local"/>.
    /// </summary>
    public static void WriteDateTime(Utf8JsonWriter writer, DateTime value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString(IsoDateTime + "K", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Writes an ISO 8601 duration in its normal form: the sign, days, then <c>T</c> and the nonzero hours, minutes and
    /// seconds (<c>P1DT2H3M4.5S</c>); zero is <c>PT0S</c>.
    /// </summary>
    public static void WriteTimeSpan(Utf8JsonWriter writer, TimeSpan value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(FormatDuration(value));
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.Ticks == 0) return "PT0S";
        var magnitude = value.Ticks < 0 ? (ulong)-(value.Ticks + 1) + 1 : (ulong)value.Ticks;
        var days = magnitude / TimeSpan.TicksPerDay;
        var hours = magnitude / TimeSpan.TicksPerHour % 24;
        var minutes = magnitude / TimeSpan.TicksPerMinute % 60;
        var seconds = magnitude / TimeSpan.TicksPerSecond % 60;
        var fraction = magnitude % TimeSpan.TicksPerSecond;
        var text = new StringBuilder(value.Ticks < 0 ? "-P" : "P");
        if (days > 0) text.Append(CultureInfo.InvariantCulture, $"{days}D");
        if (hours > 0 || minutes > 0 || seconds > 0 || fraction > 0)
        {
            text.Append('T');
            if (hours > 0) text.Append(CultureInfo.InvariantCulture, $"{hours}H");
            if (minutes > 0) text.Append(CultureInfo.InvariantCulture, $"{minutes}M");
            if (seconds > 0 || fraction > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $"{seconds}");
                if (fraction > 0) text.Append('.').Append(fraction.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0'));
                text.Append('S');
            }
        }
        return text.ToString();
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
    /// <remarks>
    /// The canonical form is RFC 8785 (JCS): object members are sorted by the UTF-16 code units of their names,
    /// duplicate names are rejected, numbers are read as IEEE 754 doubles and written as ECMAScript writes them
    /// (<c>1.0</c> and <c>1e0</c> are both <c>1</c>, and <c>-0</c> is <c>0</c>), and strings escape only
    /// <c>"</c>, <c>\</c> and control characters. A number beyond the range of a double and a string that is not
    /// valid Unicode are rejected.
    /// </remarks>
    /// <exception cref="FormatException">The value has a duplicate member name, a number beyond the range of a double or a string that is not valid Unicode.</exception>
    public static string EncodeCanonical(Action<Utf8JsonWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        return BridgeCanonicalJson.Encode(static (writer, write) => write(writer), write);
    }

    /// <summary>Compares two complete JSON documents after canonicalization.</summary>
    public static bool StructuralEquals(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        using var first = BridgeCanonicalJson.Canonical(Encoding.UTF8.GetBytes(left));
        using var second = BridgeCanonicalJson.Canonical(Encoding.UTF8.GetBytes(right));
        return first.Written.SequenceEqual(second.Written);
    }

    /// <summary>Compares two JSON values after canonicalization.</summary>
    public static bool StructuralEquals(JsonElement left, JsonElement right)
    {
        using var first = BridgeCanonicalJson.Canonical(JsonMarshal.GetRawUtf8Value(left));
        using var second = BridgeCanonicalJson.Canonical(JsonMarshal.GetRawUtf8Value(right));
        return first.Written.SequenceEqual(second.Written);
    }

    /// <summary>Returns a canonical UTF-8 encoding for a complete JSON document.</summary>
    public static byte[] Canonicalize(ReadOnlySpan<byte> json)
    {
        using var canonical = BridgeCanonicalJson.Canonical(json);
        return canonical.Written.ToArray();
    }

    /// <summary>Returns a canonical UTF-8 encoding for one JSON value.</summary>
    public static byte[] Canonicalize(JsonElement value) => Canonicalize(JsonMarshal.GetRawUtf8Value(value));
}
