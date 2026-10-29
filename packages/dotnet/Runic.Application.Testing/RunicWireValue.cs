using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Runic.Application.Views;

namespace Runic.Application.Testing;

// Encodes values exactly as the generated codecs and TypeScript clients do: Int64, UInt64, BigInteger and decimal as
// strings, enums by wire name, records and classes as objects of their wire-named properties, union cases with
// their $case, lists as arrays and string-keyed dictionaries as objects.
internal static class RunicWireValue
{
    // The deepest value the protocol allows (values.md).
    private const int MaximumDepth = 64;

    internal static void Write(Utf8JsonWriter writer, object? value, Type type) => Write(writer, value, type, null, 0);

    private static void Write(Utf8JsonWriter writer, object? value, Type type, PropertyInfo? property, int depth)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        var target = Nullable.GetUnderlyingType(type) ?? type;
        switch (value)
        {
            case string text: writer.WriteStringValue(text); return;
            case bool flag: writer.WriteBooleanValue(flag); return;
            case int or short or byte or sbyte or ushort or uint:
                writer.WriteNumberValue(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)); return;
            case long number: BridgeWire.WriteInt64(writer, number); return;
            case ulong number: BridgeWire.WriteUInt64(writer, number); return;
            case BigInteger number: BridgeWire.WriteBigInteger(writer, number); return;
            case float number: BridgeWire.WriteFiniteNumber(writer, number); return;
            case double number: BridgeWire.WriteFiniteNumber(writer, number); return;
            case decimal number: BridgeWire.WriteDecimal(writer, number); return;
            case Guid id: BridgeWire.WriteGuid(writer, id); return;
            case DateOnly date: BridgeWire.WriteDateOnly(writer, date); return;
            case TimeOnly time: BridgeWire.WriteTimeOnly(writer, time); return;
            case DateTimeOffset moment: BridgeWire.WriteDateTimeOffset(writer, moment); return;
            case DateTime moment: BridgeWire.WriteDateTime(writer, moment); return;
            case TimeSpan duration: BridgeWire.WriteTimeSpan(writer, duration); return;
        }
        if (target.IsEnum || value is Enum)
        {
            writer.WriteStringValue(EnumName(value.GetType(), value));
            return;
        }
        if (depth >= MaximumDepth)
            throw new NotSupportedException($"The value is nested more than {MaximumDepth} deep, or refers to itself.");
        var runtime = value.GetType();
        // An explicit codec of the member or the type writes the value itself.
        var codec = property?.GetCustomAttribute<RunicCodecAttribute>(false)?.CodecType
            ?? runtime.GetCustomAttribute<RunicCodecAttribute>(false)?.CodecType
            ?? target.GetCustomAttribute<RunicCodecAttribute>(false)?.CodecType;
        if (codec is not null)
        {
            WriteWithCodec(writer, value, codec);
            return;
        }
        switch (value)
        {
            case JsonElement element: element.WriteTo(writer); return;
            case System.Text.Json.Nodes.JsonNode node: node.WriteTo(writer); return;
        }
        if (DictionaryValueType(runtime) is not null)
        {
            writer.WriteStartObject();
            foreach (var entry in (System.Collections.IEnumerable)value)
            {
                var entryType = entry!.GetType();
                var key = (string)entryType.GetProperty("Key")!.GetValue(entry)!;
                var item = entryType.GetProperty("Value")!;
                writer.WritePropertyName(key);
                Write(writer, item.GetValue(entry), item.PropertyType, null, depth + 1);
            }
            writer.WriteEndObject();
            return;
        }
        if (value is System.Collections.IEnumerable items)
        {
            var itemType = ItemType(runtime) ?? typeof(object);
            writer.WriteStartArray();
            foreach (var item in items) Write(writer, item, itemType, null, depth + 1);
            writer.WriteEndArray();
            return;
        }
        WriteObject(writer, value, runtime, depth);
    }

    // A record or class: every public property the contract includes, by wire name; a union case starts with $case.
    private static void WriteObject(Utf8JsonWriter writer, object value, Type type, int depth)
    {
        if (type.IsPrimitive || type == typeof(object) || typeof(Delegate).IsAssignableFrom(type)
            || type == typeof(CancellationToken) || typeof(Type).IsAssignableFrom(type))
            throw new NotSupportedException($"{type.Name} has no Bridge representation.");
        writer.WriteStartObject();
        if (type.GetCustomAttribute<RunicUnionCaseAttribute>(false) is { } unionCase) writer.WriteString("$case", unionCase.Name);
        var honorJsonIgnore = BridgeDtoContract.HonorsJsonIgnore(type.Assembly);
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetMethod is not { IsPublic: true } || property.GetIndexParameters().Length > 0
                || BridgeDtoContract.IsIgnored(property, honorJsonIgnore))
                continue;
            var member = property.GetValue(value);
            // An optional json field that holds no value is left out.
            if (member is null && property.PropertyType.IsAssignableTo(typeof(System.Text.Json.Nodes.JsonNode))
                || member is JsonElement { ValueKind: JsonValueKind.Undefined })
                continue;
            writer.WritePropertyName(RunicMembers.WireName(property));
            Write(writer, member, property.PropertyType, property, depth + 1);
        }
        writer.WriteEndObject();
    }

    // IRunicCodec<T>.Write(writer, value), a static member of the codec type.
    private static void WriteWithCodec(Utf8JsonWriter writer, object value, Type codec)
    {
        var write = codec.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == "Write" && method.GetParameters() is [{ } first, { } second]
                && first.ParameterType == typeof(Utf8JsonWriter) && second.ParameterType.IsInstanceOfType(value))
            ?? throw new NotSupportedException($"{codec.Name} has no public static Write(Utf8JsonWriter, {value.GetType().Name}).");
        try { write.Invoke(null, [writer, value]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        }
    }

    // The value type of a dictionary with string keys, or null for another type.
    private static Type? DictionaryValueType(Type type) =>
        (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>) ? [type] : type.GetInterfaces())
            .Select(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                && candidate.GetGenericArguments()[0] is { IsGenericType: true } entry
                && entry.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)
                && entry.GetGenericArguments()[0] == typeof(string)
                    ? entry.GetGenericArguments()[1] : null)
            .FirstOrDefault(found => found is not null);

    private static Type? ItemType(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        return type.GetInterfaces().Prepend(type)
            .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    internal static string Encode(object? value, Type type) => RunicJson.Write(writer => Write(writer, value, type));

    private static string EnumName(Type type, object value)
    {
        var name = Enum.GetName(type, value) ?? throw new ArgumentOutOfRangeException(nameof(value), "A bridge enum value must be a declared name.");
        return type.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetCustomAttribute<RunicAliasAttribute>()?.Name ?? name;
    }
}
