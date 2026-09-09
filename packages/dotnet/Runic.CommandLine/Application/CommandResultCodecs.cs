using System;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>A successful command with no application payload.</summary>
public readonly record struct CommandUnit;

/// <summary>Built-in, reflection-free codecs for small commands.</summary>
public static class CommandResultCodecs
{
    /// <summary>Gets the string result codec. Human output ends with a newline.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Names the encoded CLR type.")]
    public static ICommandResultCodec<string> String { get; } = new StringCodec();
    /// <summary>Gets the silent unit result codec.</summary>
    public static ICommandResultCodec<CommandUnit> Unit { get; } = new UnitCodec();

    private sealed class StringCodec : ICommandResultCodec<string>
    {
        public string PayloadType => "runic.text/1";
        public JsonTypeInfo<string> TypeInfo => BuiltInCommandJsonContext.Default.String;
        public ValueTask WriteHumanAsync(string value, ICommandConsole console, CultureInfo culture, CancellationToken cancellationToken) =>
            console.WriteOutAsync(((value ?? string.Empty).EndsWith('\n') ? value : value + "\n").AsMemory(), cancellationToken);
    }
    private sealed class UnitCodec : ICommandResultCodec<CommandUnit>
    {
        public string PayloadType => "runic.unit/1";
        public JsonTypeInfo<CommandUnit> TypeInfo => BuiltInCommandJsonContext.Default.CommandUnit;
        public ValueTask WriteHumanAsync(CommandUnit value, ICommandConsole console, CultureInfo culture, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(CommandUnit))]
internal sealed partial class BuiltInCommandJsonContext : JsonSerializerContext;
