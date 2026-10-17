using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Runic.Platform.Linux.DBus;

/// <summary>One D-Bus value with its type signature: a basic value, a variant, an array, a dictionary or a structure.</summary>
/// <remarks>
/// Create values with the static factories, such as <see cref="From(string)"/> or
/// <see cref="Array(DBusSignature, IEnumerable{DBusValue})"/>, and read them with the matching getter, such as
/// <see cref="GetString"/>. A getter for another type throws <see cref="InvalidOperationException"/>. Values compare by
/// signature and content. A <c>h</c> value carries a file descriptor: one received in a message belongs to the
/// receiver, which disposes it. The default value has an empty signature and holds nothing.
/// </remarks>
public readonly struct DBusValue : IEquatable<DBusValue>
{
    private readonly string? _signature;
    private readonly object? _value;

    private DBusValue(string signature, object value)
    {
        _signature = signature;
        _value = value;
    }

    /// <summary>The value's type, one complete type such as <c>s</c> or <c>a{sv}</c>; empty for the default value.</summary>
    public DBusSignature Signature => new(_signature ?? "", trusted: true);

    /// <summary>Whether this is the default value, which holds nothing.</summary>
    public bool IsEmpty => _signature is null;

    /// <summary>Creates a <c>y</c> value.</summary>
    public static DBusValue From(byte value) => new("y", value);
    /// <summary>Creates a <c>b</c> value.</summary>
    public static DBusValue From(bool value) => new("b", value);
    /// <summary>Creates an <c>n</c> value.</summary>
    public static DBusValue From(short value) => new("n", value);
    /// <summary>Creates a <c>q</c> value.</summary>
    public static DBusValue From(ushort value) => new("q", value);
    /// <summary>Creates an <c>i</c> value.</summary>
    public static DBusValue From(int value) => new("i", value);
    /// <summary>Creates a <c>u</c> value.</summary>
    public static DBusValue From(uint value) => new("u", value);
    /// <summary>Creates an <c>x</c> value.</summary>
    public static DBusValue From(long value) => new("x", value);
    /// <summary>Creates a <c>t</c> value.</summary>
    public static DBusValue From(ulong value) => new("t", value);
    /// <summary>Creates a <c>d</c> value.</summary>
    public static DBusValue From(double value) => new("d", value);

    /// <summary>Creates an <c>s</c> value.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> contains NUL.</exception>
    public static DBusValue From(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0')) throw new ArgumentException("A D-Bus string cannot contain NUL.", nameof(value));
        return new("s", value);
    }

    /// <summary>Creates an <c>o</c> value.</summary>
    public static DBusValue From(DBusObjectPath value) => new("o", value);

    /// <summary>Creates a <c>g</c> value, a signature carried as data.</summary>
    public static DBusValue From(DBusSignature value) => new("g", value);

    /// <summary>Creates an <c>h</c> value that sends a file descriptor. The caller keeps ownership of the handle.</summary>
    /// <exception cref="ArgumentException">The handle is invalid or closed.</exception>
    public static DBusValue UnixFd(SafeHandle value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.IsInvalid || value.IsClosed) throw new ArgumentException("The handle is invalid or closed.", nameof(value));
        return new("h", value);
    }

    /// <summary>Creates a <c>v</c> value that wraps another value.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is the default value.</exception>
    public static DBusValue Variant(DBusValue value)
    {
        RequireValue(value, nameof(value));
        return new("v", new VariantBox(value));
    }

    /// <summary>Creates an array of <paramref name="elementSignature"/> values, such as <c>as</c> for strings.</summary>
    /// <exception cref="ArgumentException">The element signature is not one complete type, or an item has another signature.</exception>
    public static DBusValue Array(DBusSignature elementSignature, IEnumerable<DBusValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        string element = elementSignature.Value;
        if (!DBusSignatures.IsSingleCompleteType(element) || !DBusSignatures.IsSingleCompleteType("a" + element))
            throw new ArgumentException($"\"{element}\" is not an array element type.", nameof(elementSignature));
        var values = items.ToImmutableArray();
        foreach (var item in values)
            if (item._signature != element)
                throw new ArgumentException($"An item of signature \"{item.Signature}\" is not \"{element}\".", nameof(items));
        return new("a" + element, new ArrayBox(values));
    }

    /// <summary>Creates an <c>as</c> value.</summary>
    public static DBusValue StringArray(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Array("s", items.Select(From));
    }

    /// <summary>Creates an <c>ao</c> value.</summary>
    public static DBusValue ObjectPathArray(IEnumerable<DBusObjectPath> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Array("o", items.Select(From));
    }

    /// <summary>Creates an <c>ay</c> value.</summary>
    public static DBusValue ByteArray(ReadOnlySpan<byte> items)
    {
        var values = ImmutableArray.CreateBuilder<DBusValue>(items.Length);
        foreach (byte item in items) values.Add(From(item));
        return new("ay", new ArrayBox(values.MoveToImmutable()));
    }

    /// <summary>Creates a dictionary, such as <c>a{sv}</c>, from entries whose keys have a basic type.</summary>
    /// <exception cref="ArgumentException">The key signature is not a basic type, the value signature is not one complete type,
    /// an entry has other signatures, or a key repeats.</exception>
    public static DBusValue Dictionary(DBusSignature keySignature, DBusSignature valueSignature, IEnumerable<KeyValuePair<DBusValue, DBusValue>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string key = keySignature.Value, value = valueSignature.Value;
        if (key.Length != 1 || !DBusSignatures.IsBasic(key[0]))
            throw new ArgumentException($"\"{key}\" is not a basic dictionary key type.", nameof(keySignature));
        string signature = "a{" + key + value + "}";
        if (!DBusSignatures.IsSingleCompleteType(value) || !DBusSignatures.IsSingleCompleteType(signature))
            throw new ArgumentException($"\"{value}\" is not a dictionary value type.", nameof(valueSignature));
        var values = entries.ToImmutableArray();
        var keys = new HashSet<DBusValue>();
        foreach (var entry in values)
        {
            if (entry.Key._signature != key || entry.Value._signature != value)
                throw new ArgumentException($"An entry of signatures \"{entry.Key.Signature}\" and \"{entry.Value.Signature}\" is not \"{key}\" and \"{value}\".", nameof(entries));
            if (!keys.Add(entry.Key)) throw new ArgumentException($"The key {entry.Key} repeats.", nameof(entries));
        }
        return new(signature, new DictionaryBox(values));
    }

    /// <summary>Creates an <c>a{sv}</c> value, wrapping each value in a variant.</summary>
    /// <exception cref="ArgumentException">A value is the default value, or a key repeats.</exception>
    public static DBusValue VariantDictionary(IEnumerable<KeyValuePair<string, DBusValue>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return Dictionary("s", "v", entries.Select(static entry => KeyValuePair.Create(From(entry.Key), Variant(entry.Value))));
    }

    /// <summary>Creates a structure of one or more fields, such as <c>(si)</c>.</summary>
    /// <exception cref="ArgumentException">There are no fields, or a field is the default value.</exception>
    public static DBusValue Struct(params ReadOnlySpan<DBusValue> fields)
    {
        if (fields.IsEmpty) throw new ArgumentException("A structure needs at least one field.", nameof(fields));
        var signature = new StringBuilder("(");
        foreach (var field in fields)
        {
            RequireValue(field, nameof(fields));
            signature.Append(field._signature);
        }
        signature.Append(')');
        string text = signature.ToString();
        if (!DBusSignatures.IsSingleCompleteType(text)) throw new ArgumentException("The structure nests too deeply.", nameof(fields));
        return new(text, new StructBox([.. fields]));
    }

    /// <summary>Gets a <c>y</c> value.</summary>
    public byte GetByte() => Get<byte>("y");
    /// <summary>Gets a <c>b</c> value.</summary>
    public bool GetBoolean() => Get<bool>("b");
    /// <summary>Gets an <c>n</c> value.</summary>
    public short GetInt16() => Get<short>("n");
    /// <summary>Gets a <c>q</c> value.</summary>
    public ushort GetUInt16() => Get<ushort>("q");
    /// <summary>Gets an <c>i</c> value.</summary>
    public int GetInt32() => Get<int>("i");
    /// <summary>Gets a <c>u</c> value.</summary>
    public uint GetUInt32() => Get<uint>("u");
    /// <summary>Gets an <c>x</c> value.</summary>
    public long GetInt64() => Get<long>("x");
    /// <summary>Gets a <c>t</c> value.</summary>
    public ulong GetUInt64() => Get<ulong>("t");
    /// <summary>Gets a <c>d</c> value.</summary>
    public double GetDouble() => Get<double>("d");
    /// <summary>Gets an <c>s</c> value.</summary>
    public string GetString() => Get<string>("s");
    /// <summary>Gets an <c>o</c> value.</summary>
    public DBusObjectPath GetObjectPath() => Get<DBusObjectPath>("o");
    /// <summary>Gets a <c>g</c> value.</summary>
    public DBusSignature GetTypeSignature() => Get<DBusSignature>("g");
    /// <summary>Gets the file descriptor of an <c>h</c> value. A received descriptor belongs to the caller, which disposes it.</summary>
    public SafeHandle GetUnixFd() => Get<SafeHandle>("h");
    /// <summary>Gets the value inside a <c>v</c> value.</summary>
    public DBusValue GetVariant() => Get<VariantBox>("v").Value;

    /// <summary>Gets the items of an array that is not a dictionary.</summary>
    public ImmutableArray<DBusValue> GetItems() =>
        _value is ArrayBox array ? array.Items : throw Mismatch("an array");

    /// <summary>Gets the entries of a dictionary, in message order.</summary>
    public ImmutableArray<KeyValuePair<DBusValue, DBusValue>> GetEntries() =>
        _value is DictionaryBox dictionary ? dictionary.Entries : throw Mismatch("a dictionary");

    /// <summary>Gets the fields of a structure.</summary>
    public ImmutableArray<DBusValue> GetFields() =>
        _value is StructBox structure ? structure.Fields : throw Mismatch("a structure");

    /// <summary>Gets the strings of an <c>as</c> value.</summary>
    public ImmutableArray<string> GetStringArray() => _signature == "as" ? [.. GetItems().Select(static item => item.GetString())] : throw Mismatch("as");

    /// <summary>Gets the paths of an <c>ao</c> value.</summary>
    public ImmutableArray<DBusObjectPath> GetObjectPathArray() =>
        _signature == "ao" ? [.. GetItems().Select(static item => item.GetObjectPath())] : throw Mismatch("ao");

    /// <summary>Gets the bytes of an <c>ay</c> value.</summary>
    public byte[] GetByteArray() => _signature == "ay" ? [.. GetItems().Select(static item => item.GetByte())] : throw Mismatch("ay");

    /// <summary>Gets an <c>a{sv}</c> value as a dictionary of the values inside its variants.</summary>
    public ImmutableDictionary<string, DBusValue> GetVariantDictionary() => _signature == "a{sv}"
        ? GetEntries().ToImmutableDictionary(static entry => entry.Key.GetString(), static entry => entry.Value.GetVariant())
        : throw Mismatch("a{sv}");

    private T Get<T>(string signature) => _signature == signature ? (T)_value! : throw Mismatch(signature);

    private InvalidOperationException Mismatch(string expected) => new(IsEmpty
        ? $"The default DBusValue holds no {expected} value."
        : $"A value of signature \"{_signature}\" is not {(expected.Contains(' ') ? expected : $"\"{expected}\"")}.");

    private static void RequireValue(DBusValue value, string parameter)
    {
        if (value.IsEmpty) throw new ArgumentException("The default DBusValue holds nothing.", parameter);
    }

    /// <inheritdoc />
    public bool Equals(DBusValue other)
    {
        if (_signature != other._signature) return false;
        return (_value, other._value) switch
        {
            (null, null) => true,
            (ArrayBox left, ArrayBox right) => left.Items.SequenceEqual(right.Items),
            (StructBox left, StructBox right) => left.Fields.SequenceEqual(right.Fields),
            (DictionaryBox left, DictionaryBox right) => left.Entries.SequenceEqual(right.Entries),
            (VariantBox left, VariantBox right) => left.Value.Equals(right.Value),
            (SafeHandle left, SafeHandle right) => ReferenceEquals(left, right),
            ({ } left, { } right) => left.Equals(right),
            _ => false,
        };
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DBusValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_signature);
        switch (_value)
        {
            case ArrayBox array: foreach (var item in array.Items) hash.Add(item); break;
            case StructBox structure: foreach (var field in structure.Fields) hash.Add(field); break;
            case DictionaryBox dictionary: foreach (var entry in dictionary.Entries) { hash.Add(entry.Key); hash.Add(entry.Value); } break;
            case VariantBox variant: hash.Add(variant.Value); break;
            case SafeHandle handle: hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(handle)); break;
            case { } value: hash.Add(value); break;
        }
        return hash.ToHashCode();
    }

    /// <summary>Compares two values by signature and content.</summary>
    public static bool operator ==(DBusValue left, DBusValue right) => left.Equals(right);
    /// <summary>Compares two values by signature and content.</summary>
    public static bool operator !=(DBusValue left, DBusValue right) => !left.Equals(right);

    /// <summary>Formats the value for logs, such as <c>{'Volume': &lt;0.5&gt;}</c>.</summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        Format(text);
        return text.ToString();
    }

    private void Format(StringBuilder text)
    {
        switch (_value)
        {
            case null: text.Append("<empty>"); break;
            case string value: text.Append('\'').Append(value.Replace("'", "\\'", StringComparison.Ordinal)).Append('\''); break;
            case DBusObjectPath path: text.Append("objectpath '").Append(path.Value).Append('\''); break;
            case DBusSignature signature: text.Append("signature '").Append(signature.Value).Append('\''); break;
            case SafeHandle: text.Append("handle"); break;
            case bool value: text.Append(value ? "true" : "false"); break;
            case double value: text.Append(value.ToString("R", CultureInfo.InvariantCulture)); break;
            case IFormattable value: text.Append(value.ToString(null, CultureInfo.InvariantCulture)); break;
            case VariantBox variant: text.Append('<'); variant.Value.Format(text); text.Append('>'); break;
            case ArrayBox array: Join(text, '[', array.Items, ']'); break;
            case StructBox structure: Join(text, '(', structure.Fields, ')'); break;
            case DictionaryBox dictionary:
                text.Append('{');
                for (int index = 0; index < dictionary.Entries.Length; index++)
                {
                    if (index > 0) text.Append(", ");
                    dictionary.Entries[index].Key.Format(text);
                    text.Append(": ");
                    dictionary.Entries[index].Value.Format(text);
                }
                text.Append('}');
                break;
        }
    }

    private static void Join(StringBuilder text, char open, ImmutableArray<DBusValue> values, char close)
    {
        text.Append(open);
        for (int index = 0; index < values.Length; index++)
        {
            if (index > 0) text.Append(", ");
            values[index].Format(text);
        }
        text.Append(close);
    }

    // Internal constructors for values read from a message, whose signature the wire reader already checked.
    internal static DBusValue FromWire(string signature, object value) => new(signature, value);
    internal static DBusValue ArrayFromWire(string signature, ImmutableArray<DBusValue> items) => new(signature, new ArrayBox(items));
    internal static DBusValue DictionaryFromWire(string signature, ImmutableArray<KeyValuePair<DBusValue, DBusValue>> entries) => new(signature, new DictionaryBox(entries));
    internal static DBusValue StructFromWire(string signature, ImmutableArray<DBusValue> fields) => new(signature, new StructBox(fields));
    internal object? Payload => _value;

    internal sealed record VariantBox(DBusValue Value);
    internal sealed record ArrayBox(ImmutableArray<DBusValue> Items);
    internal sealed record StructBox(ImmutableArray<DBusValue> Fields);
    internal sealed record DictionaryBox(ImmutableArray<KeyValuePair<DBusValue, DBusValue>> Entries);
}
