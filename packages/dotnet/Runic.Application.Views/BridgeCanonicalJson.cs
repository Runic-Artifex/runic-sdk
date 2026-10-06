using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Runic.Application.Views;

// The one canonical JSON form of a Bridge value. Checked-field equality,
// receipt digests, operation input digests, interaction reply signatures and
// BridgeWire.EncodeCanonical all use it:
//
// - No whitespace. Object members are sorted by ordinal (UTF-16) name, and an
//   object with a duplicate member name is rejected: JavaScript and
//   System.Text.Json both read the last duplicate, so it is ambiguous input.
// - A number is written by its exact decimal value, laid out like ECMAScript
//   Number::toString does for the same digits: 1.0, 1e0 and 10e-1 are all 1,
//   and -0 is 0. Nothing is rounded, so distinct values remain distinct.
// - Strings are re-escaped by Utf8JsonWriter's default encoder.
//
// It reads with Utf8JsonReader instead of building a JsonDocument. Objects
// whose members are already sorted are copied in one pass.
internal static class BridgeCanonicalJson
{
    private const int MaximumExponent = 100_000;

    public static void Write(Utf8JsonWriter writer, ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read()) throw new JsonException("A bridge value must be a JSON value.");
        WriteValue(writer, json, ref reader);
        // Utf8JsonReader rejects a second top-level value or trailing data.
        if (reader.Read()) throw new JsonException("A bridge value must be one JSON value.");
    }

    public static string ToString(ReadOnlySpan<byte> json)
    {
        using var scratch = BridgeJsonScratch.Rent();
        Write(scratch.Writer, json);
        scratch.Writer.Flush();
        return Encoding.UTF8.GetString(scratch.Written);
    }

    // Values written by the same writer are usually byte-identical when equal,
    // so equal raw output skips canonicalization.
    public static bool Equal<T>(Action<Utf8JsonWriter, T> write, T left, T right)
    {
        using var first = WriteRaw(write, left);
        using var second = WriteRaw(write, right);
        if (first.Written.SequenceEqual(second.Written)) return true;
        using var firstCanonical = Canonical(first.Written);
        using var secondCanonical = Canonical(second.Written);
        return firstCanonical.Written.SequenceEqual(secondCanonical.Written);
    }

    public static int Hash<T>(Action<Utf8JsonWriter, T> write, T value)
    {
        using var raw = WriteRaw(write, value);
        using var canonical = Canonical(raw.Written);
        var hash = new HashCode();
        hash.AddBytes(canonical.Written);
        return hash.ToHashCode();
    }

    public static int Length<T>(Action<Utf8JsonWriter, T> write, T value)
    {
        using var raw = WriteRaw(write, value);
        using var canonical = Canonical(raw.Written);
        return canonical.Written.Length;
    }

    public static string Encode<T>(Action<Utf8JsonWriter, T> write, T value)
    {
        using var raw = WriteRaw(write, value);
        return ToString(raw.Written);
    }

    public static BridgeJsonScratch WriteRaw<T>(Action<Utf8JsonWriter, T> write, T value)
    {
        var scratch = BridgeJsonScratch.Rent();
        try
        {
            write(scratch.Writer, value);
            scratch.Writer.Flush();
            return scratch;
        }
        catch
        {
            scratch.Dispose();
            throw;
        }
    }

    public static BridgeJsonScratch Canonical(ReadOnlySpan<byte> json)
    {
        var scratch = BridgeJsonScratch.Rent();
        try
        {
            Write(scratch.Writer, json);
            scratch.Writer.Flush();
            return scratch;
        }
        catch
        {
            scratch.Dispose();
            throw;
        }
    }

    // The reader is on the first token of a value. On return it is on the last.
    private static void WriteValue(Utf8JsonWriter writer, ReadOnlySpan<byte> json, ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                WriteObject(writer, json, ref reader);
                break;
            case JsonTokenType.StartArray:
                writer.WriteStartArray();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) WriteValue(writer, json, ref reader);
                writer.WriteEndArray();
                break;
            case JsonTokenType.String:
                if (reader.ValueIsEscaped) writer.WriteStringValue(reader.GetString());
                else writer.WriteStringValue(reader.ValueSpan);
                break;
            case JsonTokenType.Number:
                WriteNumber(writer, reader.ValueSpan);
                break;
            case JsonTokenType.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonTokenType.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonTokenType.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException("A bridge value must be a JSON value.");
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, ReadOnlySpan<byte> json, ref Utf8JsonReader reader)
    {
        var members = ArrayPool<Member>.Shared.Rent(8);
        var count = 0;
        try
        {
            var sorted = true;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var member = new Member(json, ref reader);
                reader.Read();
                member.ValueStart = checked((int)reader.TokenStartIndex);
                reader.Skip();
                member.ValueEnd = checked((int)reader.BytesConsumed);
                if (count == members.Length)
                {
                    var larger = ArrayPool<Member>.Shared.Rent(count * 2);
                    members.AsSpan(0, count).CopyTo(larger);
                    ArrayPool<Member>.Shared.Return(members, clearArray: true);
                    members = larger;
                }
                if (count > 0)
                {
                    var order = Member.Compare(json, members[count - 1], member);
                    if (order == 0) throw Duplicate();
                    if (order > 0) sorted = false;
                }
                members[count++] = member;
            }
            var span = members.AsSpan(0, count);
            if (!sorted)
            {
                var source = json.ToArray();
                span.Sort((left, right) => Member.Compare(source, left, right));
                for (var index = 1; index < span.Length; index++)
                    if (Member.Compare(json, span[index - 1], span[index]) == 0) throw Duplicate();
            }

            writer.WriteStartObject();
            foreach (var member in span)
            {
                if (member.Name is { } name) writer.WritePropertyName(name);
                else writer.WritePropertyName(json.Slice(member.NameStart, member.NameLength));
                var value = json[member.ValueStart..member.ValueEnd];
                var nested = new Utf8JsonReader(value);
                nested.Read();
                WriteValue(writer, value, ref nested);
            }
            writer.WriteEndObject();
        }
        finally
        {
            ArrayPool<Member>.Shared.Return(members, clearArray: true);
        }
    }

    private static FormatException Duplicate() => new("A bridge JSON object cannot contain duplicate property names.");

    // An unescaped ASCII name is compared and written from the input bytes;
    // any other name is decoded once.
    private struct Member
    {
        public readonly int NameStart;
        public readonly int NameLength;
        public readonly string? Name;
        public int ValueStart;
        public int ValueEnd;

        public Member(ReadOnlySpan<byte> json, ref Utf8JsonReader reader)
        {
            if (reader.ValueIsEscaped || !Ascii.IsValid(reader.ValueSpan)) Name = reader.GetString();
            else
            {
                // TokenStartIndex is the opening quote of the property name.
                NameStart = checked((int)reader.TokenStartIndex + 1);
                NameLength = reader.ValueSpan.Length;
            }
        }

        public static int Compare(ReadOnlySpan<byte> json, in Member left, in Member right)
        {
            if (left.Name is null && right.Name is null)
                return json.Slice(left.NameStart, left.NameLength).SequenceCompareTo(json.Slice(right.NameStart, right.NameLength));
            return string.CompareOrdinal(left.Name ?? Encoding.ASCII.GetString(json.Slice(left.NameStart, left.NameLength)),
                right.Name ?? Encoding.ASCII.GetString(json.Slice(right.NameStart, right.NameLength)));
        }
    }

    // raw is a valid JSON number: -?(0|[1-9][0-9]*)(.[0-9]+)?([eE][+-]?[0-9]+)?
    internal static void WriteNumber(Utf8JsonWriter writer, ReadOnlySpan<byte> raw)
    {
        byte[]? rented = null;
        Span<byte> digits = raw.Length <= 128 ? stackalloc byte[128] : (rented = ArrayPool<byte>.Shared.Rent(raw.Length));
        Span<byte> output = raw.Length <= 96 ? stackalloc byte[128] : new byte[raw.Length + 32];
        try
        {
            var length = Format(raw, digits, output);
            writer.WriteRawValue(output[..length], skipInputValidation: true);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int Format(ReadOnlySpan<byte> raw, Span<byte> digits, Span<byte> output)
    {
        var index = 0;
        var negative = raw[0] == (byte)'-';
        if (negative) index++;
        var count = 0;
        long exponent = 0;
        for (; index < raw.Length && char.IsAsciiDigit((char)raw[index]); index++)
            if (count > 0 || raw[index] != (byte)'0') digits[count++] = raw[index];
        if (index < raw.Length && raw[index] == (byte)'.')
            for (index++; index < raw.Length && char.IsAsciiDigit((char)raw[index]); index++)
            {
                exponent--;
                if (count > 0 || raw[index] != (byte)'0') digits[count++] = raw[index];
            }
        if (index < raw.Length && (raw[index] | 0x20) == (byte)'e')
        {
            index++;
            var exponentNegative = raw[index] == (byte)'-';
            if (raw[index] is (byte)'-' or (byte)'+') index++;
            long written = 0;
            for (; index < raw.Length; index++)
            {
                written = written * 10 + (raw[index] - (byte)'0');
                if (written > MaximumExponent * 10L) throw OutOfRange();
            }
            exponent += exponentNegative ? -written : written;
        }
        while (count > 0 && digits[count - 1] == (byte)'0')
        {
            count--;
            exponent++;
        }
        if (count == 0)
        {
            output[0] = (byte)'0';
            return 1;
        }

        // The ECMAScript layout of digits d1..dk with the decimal point after
        // position `point`: value = 0.d1..dk * 10^point.
        var point = count + exponent;
        if (point is > MaximumExponent or < -MaximumExponent) throw OutOfRange();
        var at = 0;
        if (negative) output[at++] = (byte)'-';
        var significant = digits[..count];
        if (count <= point && point <= 21)
        {
            significant.CopyTo(output[at..]);
            at += count;
            for (var zero = count; zero < point; zero++) output[at++] = (byte)'0';
        }
        else if (0 < point && point <= 21)
        {
            significant[..(int)point].CopyTo(output[at..]);
            at += (int)point;
            output[at++] = (byte)'.';
            significant[(int)point..].CopyTo(output[at..]);
            at += count - (int)point;
        }
        else if (-6 < point && point <= 0)
        {
            output[at++] = (byte)'0';
            output[at++] = (byte)'.';
            for (var zero = point; zero < 0; zero++) output[at++] = (byte)'0';
            significant.CopyTo(output[at..]);
            at += count;
        }
        else
        {
            output[at++] = significant[0];
            if (count > 1)
            {
                output[at++] = (byte)'.';
                significant[1..].CopyTo(output[at..]);
                at += count - 1;
            }
            output[at++] = (byte)'e';
            var power = point - 1;
            output[at++] = power < 0 ? (byte)'-' : (byte)'+';
            if (!System.Buffers.Text.Utf8Formatter.TryFormat(Math.Abs(power), output[at..], out var written))
                throw OutOfRange();
            at += written;
        }
        return at;
    }

    private static FormatException OutOfRange() => new("A bridge JSON number is out of range.");
}

// A per-thread, reusable UTF-8 buffer and writer. Nested use (a codec that
// itself encodes canonically) gets a fresh instance.
internal sealed class BridgeJsonScratch : IDisposable
{
    private const int RetainedCapacity = 64 * 1024;
    [ThreadStatic] private static BridgeJsonScratch? s_cached;
    private readonly ArrayBufferWriter<byte> _buffer = new(1024);

    private BridgeJsonScratch() => Writer = new Utf8JsonWriter(_buffer);

    public Utf8JsonWriter Writer { get; }

    public ReadOnlySpan<byte> Written => _buffer.WrittenSpan;

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.WrittenMemory;

    public static BridgeJsonScratch Rent()
    {
        var cached = s_cached;
        if (cached is null) return new BridgeJsonScratch();
        s_cached = null;
        return cached;
    }

    public void Dispose()
    {
        if (_buffer.Capacity > RetainedCapacity) return;
        _buffer.ResetWrittenCount();
        Writer.Reset(_buffer);
        s_cached = this;
    }
}
