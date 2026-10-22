using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace Runic.Platform.Linux.DBus;

// Converts DBusValue to and from message bodies through the Tmds.DBus.Protocol writer and reader.
internal static class DBusWire
{
    internal static string BodySignature(ImmutableArray<DBusValue> values)
    {
        if (values.IsDefaultOrEmpty) return "";
        var signature = string.Concat(values.Select(static value =>
            value.IsDefault ? throw new ArgumentException("An argument is the default DBusValue, which holds nothing.") : value.Signature.Value));
        if (signature.Length > 255) throw new ArgumentException("The arguments' signature exceeds 255 characters.");
        return signature;
    }

    internal static void WriteBody(ref MessageWriter writer, ImmutableArray<DBusValue> values)
    {
        if (values.IsDefaultOrEmpty) return;
        foreach (var value in values) Write(ref writer, value);
    }

    private static void Write(ref MessageWriter writer, DBusValue value)
    {
        string signature = value.Signature.Value;
        object payload = value.Payload!;
        switch (signature[0])
        {
            case 'y': writer.WriteByte((byte)payload); break;
            case 'b': writer.WriteBool((bool)payload); break;
            case 'n': writer.WriteInt16((short)payload); break;
            case 'q': writer.WriteUInt16((ushort)payload); break;
            case 'i': writer.WriteInt32((int)payload); break;
            case 'u': writer.WriteUInt32((uint)payload); break;
            case 'x': writer.WriteInt64((long)payload); break;
            case 't': writer.WriteUInt64((ulong)payload); break;
            case 'd': writer.WriteDouble((double)payload); break;
            case 's': writer.WriteString((string)payload); break;
            case 'o': writer.WriteObjectPath(((DBusObjectPath)payload).Value); break;
            case 'g': writer.WriteSignature(((DBusSignature)payload).Value); break;
            case 'h': writer.WriteHandle(new BorrowedHandle((SafeHandle)payload)); break;
            case 'v':
                var inner = ((DBusValue.VariantBox)payload).Value;
                writer.WriteSignature(inner.Signature.Value);
                Write(ref writer, inner);
                break;
            case 'a' when payload is DBusValue.BytesBox bytes: writer.WriteArray(bytes.Bytes.AsSpan()); break;
            case 'a' when signature[1] == '{':
                var dictionary = writer.WriteDictionaryStart();
                foreach (var entry in ((DBusValue.DictionaryBox)payload).Entries)
                {
                    writer.WriteDictionaryEntryStart();
                    Write(ref writer, entry.Key);
                    Write(ref writer, entry.Value);
                }
                writer.WriteDictionaryEnd(dictionary);
                break;
            case 'a':
                var array = writer.WriteArrayStart((DBusType)(byte)signature[1]);
                foreach (var item in ((DBusValue.ArrayBox)payload).Items) Write(ref writer, item);
                writer.WriteArrayEnd(array);
                break;
            case '(':
                writer.WriteStructureStart();
                foreach (var field in ((DBusValue.StructBox)payload).Fields) Write(ref writer, field);
                break;
            default: throw new InvalidOperationException($"Unsupported signature \"{signature}\".");
        }
    }

    // Reads a whole body. Received file descriptors belong to the returned values; on failure they are closed.
    internal static ImmutableArray<DBusValue> ReadBody(Message message)
    {
        string signature = message.SignatureAsString;
        if (signature.Length == 0) return [];
        if (!DBusSignatures.IsValid(signature)) throw new DBusException(DBusException.InvalidArgs, $"The message signature \"{signature}\" is not valid.");
        var reader = message.GetBodyReader();
        var values = ImmutableArray.CreateBuilder<DBusValue>();
        try
        {
            foreach (string type in DBusSignatures.Split(signature)) values.Add(Read(ref reader, type));
            return values.ToImmutable();
        }
        catch (Exception error)
        {
            foreach (var value in values) CloseHandles(value);
            if (error is DBusException) throw;
            throw new DBusException(DBusException.InvalidArgs, "The message body does not match its signature.", error);
        }
    }

    // The D-Bus specification's limit on arrays, structs, dictionary entries and variants nested in each other. A peer
    // could otherwise send variants that recurse until the stack overflows, which ends the process.
    private const int MaxDepth = 64;

    private static DBusValue Read(ref Reader reader, string signature, int depth = 0)
    {
        depth += signature[0] switch { 'a' when signature[1] == '{' => 2, 'a' or '(' or 'v' => 1, _ => 0 };
        if (depth > MaxDepth) throw new DBusException(DBusException.InvalidArgs, $"The message nests values deeper than {MaxDepth} levels.");
        switch (signature[0])
        {
            case 'y': return DBusValue.From(reader.ReadByte());
            case 'b': return DBusValue.From(reader.ReadBool());
            case 'n': return DBusValue.From(reader.ReadInt16());
            case 'q': return DBusValue.From(reader.ReadUInt16());
            case 'i': return DBusValue.From(reader.ReadInt32());
            case 'u': return DBusValue.From(reader.ReadUInt32());
            case 'x': return DBusValue.From(reader.ReadInt64());
            case 't': return DBusValue.From(reader.ReadUInt64());
            case 'd': return DBusValue.From(reader.ReadDouble());
            case 's': return DBusValue.FromWire("s", reader.ReadString());
            case 'o': return DBusValue.From(new DBusObjectPath(reader.ReadObjectPathAsString()));
            case 'g': return DBusValue.From(new DBusSignature(reader.ReadSignatureAsString()));
            case 'h': return DBusValue.FromWire("h", reader.ReadHandle<SafeFileHandle>());
            case 'v':
                string inner = reader.ReadSignatureAsString();
                if (!DBusSignatures.IsSingleCompleteType(inner))
                    throw new DBusException(DBusException.InvalidArgs, $"The variant signature \"{inner}\" is not one complete type.");
                return DBusValue.FromWire("v", new DBusValue.VariantBox(Read(ref reader, inner, depth)));
            case 'a' when signature[1] == 'y': return DBusValue.BytesFromWire(reader.ReadArrayOfByte());
            case 'a' when signature[1] == '{':
            {
                string key = signature[2..3], value = signature[3..^1];
                var entries = ImmutableArray.CreateBuilder<KeyValuePair<DBusValue, DBusValue>>();
                var end = reader.ReadDictionaryStart();
                while (reader.HasNext(end))
                {
                    reader.AlignStruct();
                    var entryKey = Read(ref reader, key, depth);
                    entries.Add(KeyValuePair.Create(entryKey, Read(ref reader, value, depth)));
                }
                return DBusValue.DictionaryFromWire(signature, entries.ToImmutable());
            }
            case 'a':
            {
                string element = signature[1..];
                var items = ImmutableArray.CreateBuilder<DBusValue>();
                var end = reader.ReadArrayStart((DBusType)(byte)element[0]);
                while (reader.HasNext(end)) items.Add(Read(ref reader, element, depth));
                return DBusValue.ArrayFromWire(signature, items.ToImmutable());
            }
            case '(':
            {
                reader.AlignStruct();
                var fields = ImmutableArray.CreateBuilder<DBusValue>();
                foreach (string field in DBusSignatures.Split(signature[1..^1])) fields.Add(Read(ref reader, field, depth));
                return DBusValue.StructFromWire(signature, fields.ToImmutable());
            }
            default: throw new DBusException(DBusException.InvalidArgs, $"Unsupported signature \"{signature}\".");
        }
    }

    internal static void CloseHandles(DBusValue value)
    {
        switch (value.Payload)
        {
            case SafeHandle handle: handle.Dispose(); break;
            case DBusValue.VariantBox variant: CloseHandles(variant.Value); break;
            case DBusValue.ArrayBox array: foreach (var item in array.Items) CloseHandles(item); break;
            case DBusValue.StructBox structure: foreach (var field in structure.Fields) CloseHandles(field); break;
            case DBusValue.DictionaryBox dictionary: foreach (var entry in dictionary.Entries) { CloseHandles(entry.Key); CloseHandles(entry.Value); } break;
        }
    }

    // Tmds takes ownership of the handle it writes. Lend it a reference instead of the caller's handle.
    private sealed class BorrowedHandle : SafeHandle
    {
        private readonly SafeHandle _source;
        internal BorrowedHandle(SafeHandle source) : base(-1, true)
        {
            bool retained = false;
            source.DangerousAddRef(ref retained);
            _source = source;
            SetHandle(source.DangerousGetHandle());
        }
        public override bool IsInvalid => handle == -1;
        protected override bool ReleaseHandle() { _source.DangerousRelease(); return true; }
    }
}
