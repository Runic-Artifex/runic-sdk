using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Runic.Application.Views;

namespace Runic.Application.Testing;

// Encodes scalar values exactly as the generated codecs and TypeScript clients do:
// Int64, UInt64, BigInteger and decimal as strings, enums by wire name.
internal static class RunicWireValue
{
    internal static void Write(Utf8JsonWriter writer, object? value, Type type)
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
        if (target.IsEnum)
        {
            writer.WriteStringValue(EnumName(target, value));
            return;
        }
        throw new NotSupportedException(
            $"The test drivers encode scalar values; pass the wire JSON of {target.Name} to SetJson instead.");
    }

    internal static string Encode(object? value, Type type) => RunicJson.Write(writer => Write(writer, value, type));

    private static string EnumName(Type type, object value)
    {
        var name = Enum.GetName(type, value) ?? throw new ArgumentOutOfRangeException(nameof(value), "A bridge enum value must be a declared name.");
        return type.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetCustomAttribute<RunicAliasAttribute>()?.Name ?? name;
    }
}
