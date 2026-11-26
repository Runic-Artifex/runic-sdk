using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace Runic.Application.Views;

// The one canonical JSON form of a Bridge value: RFC 8785, the JSON
// Canonicalization Scheme (JCS). Checked-field equality, receipt digests,
// operation input digests, interaction reply signatures and
// BridgeWire.EncodeCanonical all use it:
//
// - No whitespace. Object members are sorted by the UTF-16 code units of
//   their names, and an object with a duplicate member name is rejected:
//   JavaScript and System.Text.Json both read the last duplicate, so it is
//   ambiguous input.
// - A number is read as an IEEE 754 double, as a page reads it, and written
//   as ECMAScript Number::toString writes that double (the shortest digits
//   that read back as it): 1.0, 1e0 and 10e-1 are all 1, -0 is 0, 1e-400 is 0,
//   and 0.1 and 0.10000000000000000001 are one value. A number beyond the
//   double range (1e400) is rejected with a FormatException. 64-bit integers
//   and decimals travel as strings, so only JSON-valued members
//   (JsonElement, JsonNode) can hold numbers that lose precision this way;
//   they compare as the page compares them.
// - Strings are written with BridgeJsonEncoder's minimal escaping. A string
//   or name that is not valid Unicode (a lone surrogate escape, invalid UTF-8)
//   is rejected with a FormatException (I-JSON, RFC 7493).
// - Depth is bounded by 64, Utf8JsonReader's default.
//
// Every failure carries one of the codes of the conformance corpus
// (specs/application/conformance/README.md, "Canonical form"): ErrorCode
// reads it. Malformed JSON and too deep a value throw a JsonException, the
// other failures a FormatException, as before the codes.
//
// It reads with Utf8JsonReader instead of building a JsonDocument. Objects
// whose members are already sorted are copied in one pass.
internal static class BridgeCanonicalJson
{
    // The reader allows one level more, so that this class reports too deep a
    // value itself (Read) instead of reading it from the reader's message.
    private const int MaximumDepth = 64;
    private static readonly JsonReaderOptions s_readerOptions = new() { MaxDepth = MaximumDepth + 1 };

    public static void Write(Utf8JsonWriter writer, ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, s_readerOptions);
        try
        {
            if (!Read(ref reader)) throw new BridgeCanonicalJsonException(BridgeCanonicalJsonErrors.InvalidJson, "A bridge value must be a JSON value.");
            WriteValue(writer, json, ref reader);
            // Utf8JsonReader rejects a second top-level value or trailing data.
            if (Read(ref reader)) throw new BridgeCanonicalJsonException(BridgeCanonicalJsonErrors.InvalidJson, "A bridge value must be one JSON value.");
        }
        // Utf8JsonReader's own failures are syntax errors.
        catch (JsonException error) when (error is not BridgeCanonicalJsonException)
        {
            throw new BridgeCanonicalJsonException(BridgeCanonicalJsonErrors.InvalidJson, error);
        }
    }

    // The corpus code of a failure of Write and of the members built on it,
    // or null for an exception they did not throw.
    public static string? ErrorCode(Exception error) => error switch
    {
        BridgeCanonicalJsonException json => json.Code,
        BridgeCanonicalFormatException format => format.Code,
        _ => null,
    };

    // Reads the next token of a value nested at most MaximumDepth deep.
    private static bool Read(ref Utf8JsonReader reader)
    {
        if (!reader.Read()) return false;
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && reader.CurrentDepth >= MaximumDepth)
            throw new BridgeCanonicalJsonException(BridgeCanonicalJsonErrors.DepthExceeded,
                $"A bridge JSON value cannot be nested deeper than {MaximumDepth}.");
        return true;
    }

    // Utf8JsonReader.Skip through Read: the reader is on a value's first token
    // and ends on its last.
    private static void Skip(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray)) return;
        var depth = reader.CurrentDepth;
        while (Read(ref reader) && reader.CurrentDepth > depth) { }
    }

    public static string ToString(ReadOnlySpan<byte> json)
    {
        using var scratch = BridgeJsonScratch.Rent();
        Write(scratch.Writer, json);
        scratch.Writer.Flush();
        return Encoding.UTF8.GetString(scratch.Written);
    }

    // Values written by the same writer are usually byte-identical when equal,
    // so equal raw output skips canonicalization. Byte-identical values are
    // therefore equal even when they are not valid canonical input (a
    // duplicate member name, a number beyond the double range), for which Hash and
    // the digests throw. Generated writers cannot produce such values.
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
                while (Read(ref reader) && reader.TokenType != JsonTokenType.EndArray) WriteValue(writer, json, ref reader);
                writer.WriteEndArray();
                break;
            case JsonTokenType.String:
                if (reader.ValueIsEscaped) writer.WriteStringValue(Decode(ref reader));
                else if (Utf8.IsValid(reader.ValueSpan)) writer.WriteStringValue(reader.ValueSpan);
                else throw InvalidUtf8();
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
                throw new BridgeCanonicalJsonException(BridgeCanonicalJsonErrors.InvalidJson, "A bridge value must be a JSON value.");
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, ReadOnlySpan<byte> json, ref Utf8JsonReader reader)
    {
        var members = ArrayPool<Member>.Shared.Rent(8);
        var count = 0;
        try
        {
            var sorted = true;
            while (Read(ref reader) && reader.TokenType != JsonTokenType.EndObject)
            {
                var member = new Member(json, ref reader);
                Read(ref reader);
                member.ValueStart = checked((int)reader.TokenStartIndex);
                Skip(ref reader);
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
                Sort(json, span);
                for (var index = 1; index < span.Length; index++)
                    if (Member.Compare(json, span[index - 1], span[index]) == 0) throw Duplicate();
            }

            writer.WriteStartObject();
            foreach (var member in span)
            {
                if (member.Name is { } name) writer.WritePropertyName(name);
                else writer.WritePropertyName(json.Slice(member.NameStart, member.NameLength));
                var value = json[member.ValueStart..member.ValueEnd];
                // The outer reader has checked the value's depth.
                var nested = new Utf8JsonReader(value, s_readerOptions);
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

    // A stable merge sort over the member table. Names are compared in place
    // in the input, so sorting an object costs O(m log m) comparisons of its
    // own names whatever the size of the enclosing document.
    private static void Sort(ReadOnlySpan<byte> json, Span<Member> members)
    {
        if (members.Length <= 16)
        {
            for (var index = 1; index < members.Length; index++)
            {
                var current = members[index];
                var at = index - 1;
                for (; at >= 0 && Member.Compare(json, members[at], current) > 0; at--) members[at + 1] = members[at];
                members[at + 1] = current;
            }
            return;
        }
        var rented = ArrayPool<Member>.Shared.Rent(members.Length);
        try { MergeSort(json, members, rented.AsSpan(0, members.Length)); }
        finally { ArrayPool<Member>.Shared.Return(rented, clearArray: true); }
    }

    private static void MergeSort(ReadOnlySpan<byte> json, Span<Member> members, Span<Member> scratch)
    {
        if (members.Length <= 16)
        {
            Sort(json, members);
            return;
        }
        var middle = members.Length / 2;
        MergeSort(json, members[..middle], scratch[..middle]);
        MergeSort(json, members[middle..], scratch[middle..]);
        members.CopyTo(scratch);
        int left = 0, right = middle, target = 0;
        while (left < middle && right < members.Length)
            members[target++] = Member.Compare(json, scratch[right], scratch[left]) < 0 ? scratch[right++] : scratch[left++];
        while (left < middle) members[target++] = scratch[left++];
        while (right < members.Length) members[target++] = scratch[right++];
    }

    private static BridgeCanonicalFormatException Duplicate() =>
        new(BridgeCanonicalJsonErrors.DuplicateMember, "A bridge JSON object cannot contain duplicate property names.");

    // Utf8JsonReader rejects an escaped lone surrogate and invalid UTF-8 when
    // it decodes, both with an InvalidOperationException. Escapes are ASCII,
    // so text whose raw bytes are well-formed UTF-8 failed on a surrogate.
    private static string Decode(ref Utf8JsonReader reader)
    {
        try { return reader.GetString()!; }
        catch (InvalidOperationException error)
        {
            if (!Utf8.IsValid(reader.ValueSpan)) throw InvalidUtf8(error);
            throw new BridgeCanonicalFormatException(BridgeCanonicalJsonErrors.LoneSurrogate,
                "A bridge JSON string cannot contain a lone surrogate.", error);
        }
    }

    private static BridgeCanonicalFormatException InvalidUtf8(Exception? inner = null) =>
        new(BridgeCanonicalJsonErrors.InvalidUtf8, "A bridge JSON string must be well-formed UTF-8.", inner);

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
            if (reader.ValueIsEscaped || !Ascii.IsValid(reader.ValueSpan)) Name = Decode(ref reader);
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
            if (left.Name is { } leftName && right.Name is { } rightName) return string.CompareOrdinal(leftName, rightName);
            return left.Name is { } name
                ? -CompareAscii(json.Slice(right.NameStart, right.NameLength), name)
                : CompareAscii(json.Slice(left.NameStart, left.NameLength), right.Name!);
        }

        // Ordinal UTF-16 order of an ASCII name against a decoded name.
        private static int CompareAscii(ReadOnlySpan<byte> ascii, ReadOnlySpan<char> name)
        {
            var length = Math.Min(ascii.Length, name.Length);
            for (var index = 0; index < length; index++)
                if (ascii[index] != name[index]) return ascii[index] - name[index];
            return ascii.Length - name.Length;
        }
    }

    // raw is a valid JSON number: -?(0|[1-9][0-9]*)(.[0-9]+)?([eE][+-]?[0-9]+)?
    internal static void WriteNumber(Utf8JsonWriter writer, ReadOnlySpan<byte> raw)
    {
        Span<byte> output = stackalloc byte[MaximumNumberLength];
        writer.WriteRawValue(output[..FormatNumber(raw, output)], skipInputValidation: true);
    }

    // The ECMAScript form of a double has at most 25 bytes: -0.00000 and 17 digits.
    internal const int MaximumNumberLength = 32;

    // At most 15 significant digits (DBL_DIG) survive a trip through a
    // normal double unchanged, so for such a number the shortest digits of
    // the nearest double are its own digits. Within this decimal exponent
    // every such number is a normal double.
    private const int ExactDigits = 15;
    private const int NormalPoint = 300;

    internal static int FormatNumber(ReadOnlySpan<byte> raw, Span<byte> output)
    {
        Span<byte> digits = stackalloc byte[ExactDigits];
        if (TryReadDigits(raw, digits, NormalPoint, out var negative, out var count, out var point))
            return count == 0 ? Zero(output) : Layout(negative, digits[..count], point, output);
        // Parsing rounds to the nearest double, overflows to infinity and
        // underflows to zero.
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) throw OutOfRange();
        return FormatNumber(value, output);
    }

    // ECMAScript Number::toString of a finite double, as RFC 8785 specifies.
    internal static int FormatNumber(double value, Span<byte> output)
    {
        if (!double.IsFinite(value)) throw OutOfRange();
        if (value == 0) return Zero(output);
        // "R" is the shortest round-trip form, the digits ECMAScript chooses,
        // in .NET's layout: -1.2345E-07, 1E+21, 123.45.
        Span<byte> shortest = stackalloc byte[MaximumNumberLength];
        Span<byte> digits = stackalloc byte[MaximumNumberLength];
        if (!value.TryFormat(shortest, out var length, "R", CultureInfo.InvariantCulture)
            || !TryReadDigits(shortest[..length], digits, 1000, out var negative, out var count, out var point))
            throw OutOfRange();
        return Layout(negative, digits[..count], point, output);
    }

    private static int Zero(Span<byte> output)
    {
        output[0] = (byte)'0';
        return 1;
    }

    // Reads the significant digits d1..dk of a number in JSON's or .NET's
    // layout, without leading or trailing zeros, and `point` such that the
    // value is 0.d1..dk * 10^point. Fails when there are more digits than
    // fit or |point| is not below maximumPoint; zero has no digits.
    private static bool TryReadDigits(ReadOnlySpan<byte> number, Span<byte> digits, int maximumPoint,
        out bool negative, out int count, out int point)
    {
        var index = 0;
        negative = number[0] == (byte)'-';
        if (negative) index++;
        count = 0;
        point = 0;
        var exponent = 0;
        var zeros = 0;
        for (; index < number.Length && char.IsAsciiDigit((char)number[index]); index++)
            if (!Digit(number[index], digits, ref count, ref zeros)) return false;
        if (index < number.Length && number[index] == (byte)'.')
            for (index++; index < number.Length && char.IsAsciiDigit((char)number[index]); index++)
            {
                exponent--;
                if (!Digit(number[index], digits, ref count, ref zeros)) return false;
            }
        // Zeros still held back are trailing: powers of ten, not digits.
        exponent += zeros;
        if (index < number.Length && (number[index] | 0x20) == (byte)'e')
        {
            index++;
            var exponentNegative = number[index] == (byte)'-';
            if (number[index] is (byte)'-' or (byte)'+') index++;
            var written = 0;
            for (; index < number.Length; index++)
            {
                written = written * 10 + (number[index] - (byte)'0');
                if (written > 10 * maximumPoint) return count == 0;
            }
            exponent += exponentNegative ? -written : written;
        }
        if (count == 0) return true;
        point = count + exponent;
        return point > -maximumPoint && point < maximumPoint;
    }

    // Appends a digit, holding back zeros until a nonzero digit follows.
    private static bool Digit(byte digit, Span<byte> digits, ref int count, ref int zeros)
    {
        if (digit == (byte)'0')
        {
            if (count > 0) zeros++;
            return true;
        }
        if (count + zeros + 1 > digits.Length) return false;
        for (; zeros > 0; zeros--) digits[count++] = (byte)'0';
        digits[count++] = digit;
        return true;
    }

    // ECMAScript Number::toString's layout of digits d1..dk (k >= 1) with
    // value 0.d1..dk * 10^point.
    private static int Layout(bool negative, ReadOnlySpan<byte> significant, int point, Span<byte> output)
    {
        var count = significant.Length;
        var at = 0;
        if (negative) output[at++] = (byte)'-';
        if (count <= point && point <= 21)
        {
            significant.CopyTo(output[at..]);
            at += count;
            for (var zero = count; zero < point; zero++) output[at++] = (byte)'0';
        }
        else if (0 < point && point <= 21)
        {
            significant[..point].CopyTo(output[at..]);
            at += point;
            output[at++] = (byte)'.';
            significant[point..].CopyTo(output[at..]);
            at += count - point;
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

    private static BridgeCanonicalFormatException OutOfRange() =>
        new(BridgeCanonicalJsonErrors.NumberOutOfRange, "A bridge JSON number must be within the range of a double.");
}

// The error codes of the canonical form, as the conformance corpus names them
// (specs/application/conformance/README.md, "Canonical form").
internal static class BridgeCanonicalJsonErrors
{
    public const string InvalidUtf8 = "invalid-utf8";
    public const string InvalidJson = "invalid-json";
    public const string LoneSurrogate = "lone-surrogate";
    public const string DuplicateMember = "duplicate-member";
    public const string DepthExceeded = "depth-exceeded";
    public const string NumberOutOfRange = "number-out-of-range";
}

// invalid-json and depth-exceeded.
internal sealed class BridgeCanonicalJsonException : JsonException
{
    public BridgeCanonicalJsonException(string code, string message) : base(message) => Code = code;

    // Keeps the position of a failure of Utf8JsonReader.
    public BridgeCanonicalJsonException(string code, JsonException inner)
        : base(inner.Message, inner.Path, inner.LineNumber, inner.BytePositionInLine, inner) => Code = code;

    public string Code { get; }
}

// invalid-utf8, lone-surrogate, duplicate-member and number-out-of-range.
internal sealed class BridgeCanonicalFormatException(string code, string message, Exception? inner = null) : FormatException(message, inner)
{
    public string Code { get; } = code;
}

// A per-thread, reusable UTF-8 buffer and writer. Nested use (a codec that
// itself encodes canonically) gets a fresh instance.
internal sealed class BridgeJsonScratch : IDisposable
{
    private const int RetainedCapacity = 64 * 1024;
    [ThreadStatic] private static BridgeJsonScratch? s_cached;
    private readonly ArrayBufferWriter<byte> _buffer = new(1024);

    private BridgeJsonScratch() => Writer = new Utf8JsonWriter(_buffer, BridgeJsonEncoder.WriterOptions);

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
