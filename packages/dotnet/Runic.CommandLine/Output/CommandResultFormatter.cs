using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>Formats typed results using their registered JSON metadata, without reflection or record debug strings.</summary>
public static class CommandResultFormatter
{
    /// <summary>Writes object fields as labeled rows and scalar results as text.</summary>
    public static ValueTask WriteHumanAsync<T>(T value, JsonTypeInfo<T> typeInfo, ICommandConsole console, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentNullException.ThrowIfNull(console);
        JsonElement element = JsonSerializer.SerializeToElement(value, typeInfo);
        var text = new StringBuilder();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
                text.Append(property.Name).Append(": ").Append(property.Value.ToString()).Append('\n');
        }
        else text.Append(element.ToString()).Append('\n');
        return console.WriteOutAsync(text.ToString().AsMemory(), cancellationToken);
    }
}
