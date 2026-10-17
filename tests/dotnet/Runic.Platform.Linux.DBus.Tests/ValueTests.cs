using System.Collections.Immutable;
using Runic.Platform;
using Runic.Platform.Linux.DBus;
using TUnit.Core;

// Values, signatures and names need no bus.
internal sealed class ValueTests
{
    [Test, DisplayName("values carry their signature and read back with the matching getter")]
    public void Values()
    {
        Check(DBusValue.From((byte)7).GetByte() == 7 && DBusValue.From((byte)7).Signature.Value == "y", "y");
        Check(DBusValue.From(true).GetBoolean() && DBusValue.From((short)-2).GetInt16() == -2 && DBusValue.From((ushort)2).GetUInt16() == 2
            && DBusValue.From(-3).GetInt32() == -3 && DBusValue.From(3u).GetUInt32() == 3 && DBusValue.From(-4L).GetInt64() == -4
            && DBusValue.From(4UL).GetUInt64() == 4 && DBusValue.From(0.5).GetDouble() == 0.5, "numbers");
        Check(DBusValue.From("text").GetString() == "text" && DBusValue.From(new DBusObjectPath("/a/b")).GetObjectPath().Value == "/a/b"
            && DBusValue.From(new DBusSignature("a{sv}")).GetTypeSignature().Value == "a{sv}", "strings");
        var variant = DBusValue.Variant(DBusValue.From(5));
        Check(variant.Signature.Value == "v" && variant.GetVariant().GetInt32() == 5, "variant");
        var strings = DBusValue.StringArray(["a", "b"]);
        Check(strings.Signature.Value == "as" && strings.GetStringArray().SequenceEqual(["a", "b"]) && strings.GetItems().Length == 2, "as");
        Check(DBusValue.ByteArray([1, 2]).GetByteArray().SequenceEqual(new byte[] { 1, 2 }) && DBusValue.ObjectPathArray(["/x"]).GetObjectPathArray()[0].Value == "/x",
            "ay and ao");
        var properties = DBusValue.VariantDictionary([KeyValuePair.Create("Volume", DBusValue.From(0.5)), KeyValuePair.Create("Name", DBusValue.From("x"))]);
        Check(properties.Signature.Value == "a{sv}" && properties.GetVariantDictionary()["Volume"].GetDouble() == 0.5, "a{sv}");
        var structure = DBusValue.Struct(DBusValue.From("a"), DBusValue.From(1), DBusValue.StringArray([]));
        Check(structure.Signature.Value == "(sias)" && structure.GetFields()[1].GetInt32() == 1, "struct");
        var nested = DBusValue.Array("(si)", [DBusValue.Struct(DBusValue.From("a"), DBusValue.From(1))]);
        Check(nested.Signature.Value == "a(si)", "array of struct");
        var dictionary = DBusValue.Dictionary("o", "a{sv}", [KeyValuePair.Create(DBusValue.From(new DBusObjectPath("/d")), properties)]);
        Check(dictionary.Signature.Value == "a{oa{sv}}" && dictionary.GetEntries()[0].Value == properties, "dictionary of dictionaries");
        Check(default(DBusValue).IsEmpty && default(DBusValue).Signature.Value == "", "default");
    }

    [Test, DisplayName("values compare by signature and content, and format for logs")]
    public void Equality()
    {
        Check(DBusValue.From(1) == DBusValue.From(1) && DBusValue.From(1) != DBusValue.From(1u) && DBusValue.From(1) != DBusValue.From(2), "basic equality");
        Check(DBusValue.StringArray(["a"]) == DBusValue.StringArray(["a"]) && DBusValue.StringArray(["a"]).GetHashCode() == DBusValue.StringArray(["a"]).GetHashCode(),
            "structural equality");
        Check(DBusValue.Array("s", []) != DBusValue.Array("i", []), "empty arrays of different types differ");
        Check(DBusValue.VariantDictionary([KeyValuePair.Create("a", DBusValue.From(1))]).ToString() == "{'a': <1>}", "dictionary text");
        Check(DBusValue.Struct(DBusValue.From("it's"), DBusValue.From(true)).ToString() == "('it\\'s', true)", "struct text");
    }

    [Test, DisplayName("values refuse what D-Bus cannot carry")]
    public void Validation()
    {
        Throws<InvalidOperationException>(() => DBusValue.From(1).GetString(), "wrong getter");
        Throws<InvalidOperationException>(() => default(DBusValue).GetInt32(), "default getter");
        Throws<InvalidOperationException>(() => DBusValue.From(1).GetItems(), "items of a number");
        Throws<ArgumentException>(() => DBusValue.From("a\0b"), "NUL in a string");
        Throws<ArgumentException>(() => DBusValue.Array("s", [DBusValue.From(1)]), "item of another type");
        Throws<ArgumentException>(() => DBusValue.Array("{sv}", []), "bare dict entry as element");
        Throws<ArgumentException>(() => DBusValue.Dictionary("as", "s", []), "container key");
        Throws<ArgumentException>(() => DBusValue.Dictionary("s", "s", [KeyValuePair.Create(DBusValue.From("a"), DBusValue.From("1")),
            KeyValuePair.Create(DBusValue.From("a"), DBusValue.From("2"))]), "repeated key");
        Throws<ArgumentException>(() => DBusValue.Struct(), "empty struct");
        Throws<ArgumentException>(() => DBusValue.Variant(default), "variant of nothing");
        Throws<ArgumentException>(() => DBusValue.Struct(default(DBusValue)), "field of nothing");
        var deep = DBusValue.From(1);
        Throws<ArgumentException>(() => { for (int depth = 0; depth < 40; depth++) deep = DBusValue.Array(deep.Signature, [deep]); }, "arrays nested beyond 32");
    }

    [Test, DisplayName("paths, signatures and names follow the D-Bus syntax")]
    public void Names()
    {
        foreach (string valid in new[] { "/", "/a", "/a/b_c/D9" }) _ = new DBusObjectPath(valid);
        foreach (string invalid in new[] { "", "a", "/a/", "//a", "/a//b", "/a-b", "/ä" })
            Throws<ArgumentException>(() => _ = new DBusObjectPath(invalid), $"path {invalid}");
        Check(default(DBusObjectPath).Value == "/" && DBusObjectPath.Root.Value == "/", "default path");
        foreach (string valid in new[] { "", "s", "a{sv}", "(ii)as", "a(sa{oa{sv}})", "v" }) _ = new DBusSignature(valid);
        foreach (string invalid in new[] { "a", "{sv}", "a{vs}", "()", "(i", "z", "a{s}", "a{sii}" })
            Throws<ArgumentException>(() => _ = new DBusSignature(invalid), $"signature {invalid}");
        Check(new DBusSignature("a{sv}").IsSingleCompleteType && !new DBusSignature("ii").IsSingleCompleteType, "single complete type");
        Throws<ArgumentException>(() => _ = new DBusException("NotAName", "x"), "error name");
        Check(new DBusException("org.example.Error.Nope", "x").ErrorName == "org.example.Error.Nope", "error name kept");
    }

    [Test, DisplayName("connecting without a bus reports it unavailable")]
    public async Task NoBusAsync()
    {
        string socket = Path.Combine(Path.GetTempPath(), $"runic-no-bus-{Guid.NewGuid():N}");
        Check(await DBusConnection.ConnectToAddressAsync($"unix:path={socket}") is PlatformResult<DBusConnection>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "an address without a listener");
        // Linux would connect to the machine's own system bus, which tests leave alone.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
            Check(await DBusConnection.ConnectAsync(DBusBus.System) is PlatformResult<DBusConnection>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured },
                "no D-Bus on this platform");
        Throws<ArgumentOutOfRangeException>(() => DBusConnection.ConnectAsync((DBusBus)5).AsTask(), "undefined bus");
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}: {message}");
    }
}
