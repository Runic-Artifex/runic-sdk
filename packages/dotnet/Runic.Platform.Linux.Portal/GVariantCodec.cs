using System.Buffers.Binary;
using System.Text;

namespace Runic.Platform.Linux.Portal;

// GVariant's serialisation format (GLib's gvariant-serialiser.c, normal form, little-endian), for the types libsecret's
// file backend stores: y, u, t, s, arrays, structs and dictionary entries. Values are byte, uint, ulong, string, byte[]
// (for ay), List<object> (other arrays) and object[] (structs and dictionary entries).
internal static class GVariantCodec
{
    internal abstract record GType
    {
        internal abstract int Alignment { get; }
        // The size of a fixed-size type; null for a variable-size one.
        internal abstract int? FixedSize { get; }
    }

    internal sealed record Basic(char Code) : GType
    {
        internal override int Alignment => Code switch { 'y' => 1, 'u' => 4, 't' => 8, _ => 1 };
        internal override int? FixedSize => Code switch { 'y' => 1, 'u' => 4, 't' => 8, _ => null };
    }

    internal sealed record ArrayOf(GType Element) : GType
    {
        internal override int Alignment => Element.Alignment;
        internal override int? FixedSize => null;
    }

    // A struct, or a dictionary entry (two members).
    internal sealed record Tuple(GType[] Members) : GType
    {
        internal override int Alignment => Members.Length == 0 ? 1 : Members.Max(static member => member.Alignment);
        internal override int? FixedSize
        {
            get
            {
                int size = 0;
                foreach (var member in Members)
                {
                    if (member.FixedSize is not { } fixedSize) return null;
                    size = Align(size, member.Alignment) + fixedSize;
                }
                return Members.Length == 0 ? 1 : Align(size, Alignment);
            }
        }
    }

    internal static GType Parse(string signature)
    {
        int position = 0;
        var type = ParseOne(signature, ref position);
        if (position != signature.Length) throw new ArgumentException("Trailing characters in the type.", nameof(signature));
        return type;
    }

    private static GType ParseOne(string signature, ref int position)
    {
        char code = signature[position++];
        switch (code)
        {
            case 'y' or 'u' or 't' or 's': return new Basic(code);
            case 'a': return new ArrayOf(ParseOne(signature, ref position));
            case '(' or '{':
                char close = code == '(' ? ')' : '}';
                var members = new List<GType>();
                while (signature[position] != close) members.Add(ParseOne(signature, ref position));
                position++;
                return new Tuple([.. members]);
            default: throw new ArgumentException($"Unsupported type code '{code}'.", nameof(signature));
        }
    }

    internal static byte[] Serialize(GType type, object value)
    {
        var buffer = new List<byte>();
        Write(buffer, type, value);
        return [.. buffer];
    }

    private static void Write(List<byte> buffer, GType type, object value)
    {
        int start = buffer.Count;
        switch (type)
        {
            case Basic { Code: 'y' }: buffer.Add((byte)value); break;
            case Basic { Code: 'u' }:
                { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value); buffer.AddRange(bytes); break; }
            case Basic { Code: 't' }:
                { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)value); buffer.AddRange(bytes); break; }
            case Basic { Code: 's' }:
                buffer.AddRange(Encoding.UTF8.GetBytes((string)value));
                buffer.Add(0);
                break;
            case ArrayOf { Element: Basic { Code: 'y' } }: buffer.AddRange((byte[])value); break;
            case ArrayOf array:
                {
                    var items = (List<object>)value;
                    var ends = new List<int>();
                    foreach (object item in items)
                    {
                        Pad(buffer, start, array.Element.Alignment);
                        Write(buffer, array.Element, item);
                        ends.Add(buffer.Count - start);
                    }
                    if (array.Element.FixedSize is null) WriteOffsets(buffer, start, ends);
                    break;
                }
            case Tuple tuple:
                {
                    var members = (object[])value;
                    if (members.Length != tuple.Members.Length) throw new ArgumentException("The value does not match the struct type.", nameof(value));
                    var ends = new List<int>();
                    for (int i = 0; i < members.Length; i++)
                    {
                        Pad(buffer, start, tuple.Members[i].Alignment);
                        Write(buffer, tuple.Members[i], members[i]);
                        if (tuple.Members[i].FixedSize is null && i < members.Length - 1) ends.Add(buffer.Count - start);
                    }
                    if (tuple.FixedSize is { } size)
                    {
                        while (buffer.Count - start < size) buffer.Add(0);
                    }
                    else
                    {
                        ends.Reverse();
                        WriteOffsets(buffer, start, ends);
                    }
                    break;
                }
            default: throw new ArgumentException("Unsupported type.", nameof(type));
        }
    }

    // The framing offsets, each in the smallest size that can address the whole container including them.
    private static void WriteOffsets(List<byte> buffer, int start, List<int> offsets)
    {
        if (offsets.Count == 0) return;
        int body = buffer.Count - start;
        int size = OffsetSizeFor(body, offsets.Count);
        foreach (int offset in offsets)
            for (int i = 0; i < size; i++) buffer.Add((byte)((ulong)offset >> (8 * i)));
    }

    private static int OffsetSizeFor(int body, int count)
    {
        if ((long)body + count <= byte.MaxValue) return 1;
        if ((long)body + 2L * count <= ushort.MaxValue) return 2;
        return 4;
    }

    // The offset size a container of this total size uses.
    private static int OffsetSize(int size) => size switch { 0 => 0, <= byte.MaxValue => 1, <= ushort.MaxValue => 2, _ => 4 };

    private static void Pad(List<byte> buffer, int start, int alignment)
    {
        while ((buffer.Count - start) % alignment != 0) buffer.Add(0);
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    /// <exception cref="FormatException">The data is not in normal form for the type.</exception>
    internal static object Deserialize(GType type, ReadOnlySpan<byte> data)
    {
        switch (type)
        {
            case Basic { Code: 'y' }: return data.Length == 1 ? data[0] : throw Invalid();
            case Basic { Code: 'u' }: return data.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : throw Invalid();
            case Basic { Code: 't' }: return data.Length == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(data) : throw Invalid();
            case Basic { Code: 's' }:
                if (data.Length == 0 || data[^1] != 0 || data[..^1].Contains((byte)0) || !System.Text.Unicode.Utf8.IsValid(data[..^1])) throw Invalid();
                return Encoding.UTF8.GetString(data[..^1]);
            case ArrayOf { Element: Basic { Code: 'y' } }: return data.ToArray();
            case ArrayOf array:
                {
                    var items = new List<object>();
                    if (array.Element.FixedSize is { } elementSize)
                    {
                        if (data.Length % elementSize != 0) throw Invalid();
                        for (int i = 0; i < data.Length; i += elementSize) items.Add(Deserialize(array.Element, data.Slice(i, elementSize)));
                        return items;
                    }
                    if (data.Length == 0) return items;
                    int size = OffsetSize(data.Length);
                    int table = ReadOffset(data, data.Length - size, size);
                    if (table > data.Length || (data.Length - table) % size != 0) throw Invalid();
                    int count = (data.Length - table) / size, position = 0;
                    for (int i = 0; i < count; i++)
                    {
                        int end = ReadOffset(data, table + i * size, size);
                        position = Align(position, array.Element.Alignment);
                        if (end < position || end > table) throw Invalid();
                        items.Add(Deserialize(array.Element, data[position..end]));
                        position = end;
                    }
                    return items;
                }
            case Tuple tuple:
                {
                    if (tuple.FixedSize is { } fixedSize && data.Length != fixedSize) throw Invalid();
                    int size = OffsetSize(data.Length);
                    int framing = data.Length, position = 0;
                    var members = new object[tuple.Members.Length];
                    for (int i = 0; i < members.Length; i++)
                    {
                        var member = tuple.Members[i];
                        position = Align(position, member.Alignment);
                        int end;
                        if (member.FixedSize is { } memberSize) end = position + memberSize;
                        else if (i == members.Length - 1) end = framing;
                        else
                        {
                            framing -= size;
                            if (framing < position) throw Invalid();
                            end = ReadOffset(data, framing, size);
                        }
                        if (end < position || end > framing) throw Invalid();
                        members[i] = Deserialize(member, data[position..end]);
                        position = end;
                    }
                    return members;
                }
            default: throw new ArgumentException("Unsupported type.", nameof(type));
        }
    }

    private static int ReadOffset(ReadOnlySpan<byte> data, int at, int size)
    {
        if (at < 0 || at + size > data.Length) throw Invalid();
        ulong value = 0;
        for (int i = 0; i < size; i++) value |= (ulong)data[at + i] << (8 * i);
        return value > int.MaxValue ? throw Invalid() : (int)value;
    }

    private static FormatException Invalid() => new("The data is not a valid GVariant of its type.");
}
