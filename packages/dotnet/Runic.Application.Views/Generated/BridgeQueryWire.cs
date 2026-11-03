using System.Buffers;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace Runic.Application.Views.Generated;

/// <summary>
/// The wire form of query collections (<c>query-collections/1</c>, specs/application/README.md, "Query collections").
/// Generated bridges write a <see cref="RunicQueryCollection{TRow, TKey}"/> state member with it.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class BridgeQueryWire
{
    /// <summary>Writes a query collection's state member: <c>{query, total, version}</c>.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="collection">The ViewModel's query collection.</param>
    /// <exception cref="InvalidOperationException">The property is <see langword="null"/> or not a query collection.</exception>
    public static void WriteState(Utf8JsonWriter writer, object? collection)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (collection is not IBridgeQueryCollection query)
            throw new InvalidOperationException("A RunicQueryCollection state property is null.");
        query.WriteState(writer);
    }

    internal static string Encode(RunicQuery query)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, query);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // {"sort":[{"field","descending"}],"filters":[{"field","op","value"?,"min"?,"max"?}]}, members in this order.
    internal static void Write(Utf8JsonWriter writer, RunicQuery query)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("sort");
        foreach (var key in query.Sort)
        {
            writer.WriteStartObject();
            writer.WriteString("field", key.Field);
            writer.WriteBoolean("descending", key.Descending);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("filters");
        foreach (var filter in query.Filters)
        {
            writer.WriteStartObject();
            writer.WriteString("field", filter.Field);
            writer.WriteString("op", Operator(filter.Operator));
            WriteValue(writer, "value", filter.Value);
            WriteValue(writer, "min", filter.Minimum);
            WriteValue(writer, "max", filter.Maximum);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    // Reads a query of a query-set request. Throws FormatException for one that is not a query.
    internal static RunicQuery Read(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException("A query is a JSON object.");
        var sort = new List<RunicSortKey>();
        var filters = new List<RunicFilter>();
        if (element.TryGetProperty("sort", out var keys) && keys.ValueKind != JsonValueKind.Null)
        {
            if (keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() > RunicQuery.MaximumSortKeys)
                throw new FormatException($"A query's sort is an array of at most {RunicQuery.MaximumSortKeys} keys.");
            foreach (var key in keys.EnumerateArray())
            {
                if (key.ValueKind != JsonValueKind.Object) throw new FormatException("A sort key is a JSON object.");
                var descending = key.TryGetProperty("descending", out var flag) && flag.ValueKind != JsonValueKind.Null
                    ? flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean()
                        : throw new FormatException("A sort key's descending is a Boolean.")
                    : false;
                sort.Add(new(Field(key), descending));
            }
        }
        if (element.TryGetProperty("filters", out var all) && all.ValueKind != JsonValueKind.Null)
        {
            if (all.ValueKind != JsonValueKind.Array || all.GetArrayLength() > RunicQuery.MaximumFilters)
                throw new FormatException($"A query's filters are an array of at most {RunicQuery.MaximumFilters} filters.");
            foreach (var filter in all.EnumerateArray())
            {
                if (filter.ValueKind != JsonValueKind.Object) throw new FormatException("A filter is a JSON object.");
                var field = Field(filter);
                if (!filter.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String)
                    throw new FormatException("A filter has an op.");
                var value = filter.TryGetProperty("value", out var found) ? found : default;
                filters.Add(op.GetString() switch
                {
                    "equals" => value.ValueKind == JsonValueKind.Undefined ? throw new FormatException("An equals filter has a value.")
                        : RunicFilter.Equal(field, value),
                    "contains" => value.ValueKind == JsonValueKind.String ? RunicFilter.Contains(field, value.GetString()!)
                        : throw new FormatException("A contains filter's value is a string."),
                    "range" => RunicFilter.Range(field, filter.TryGetProperty("min", out var min) ? min : default,
                        filter.TryGetProperty("max", out var max) ? max : default),
                    "custom" => RunicFilter.Custom(field, value),
                    _ => throw new FormatException("A filter's op is equals, contains, range or custom."),
                });
            }
        }
        return new RunicQuery(sort, filters);
    }

    private static string Field(JsonElement owner)
    {
        if (!owner.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.String
            || field.GetString() is not { Length: > 0 and <= RunicQuery.MaximumFieldLength } name)
            throw new FormatException($"A query field is a string of 1 to {RunicQuery.MaximumFieldLength} characters.");
        return name;
    }

    private static string Operator(RunicFilterOperator value) => value switch
    {
        RunicFilterOperator.Equal => "equals",
        RunicFilterOperator.Contains => "contains",
        RunicFilterOperator.Range => "range",
        _ => "custom",
    };

    private static void WriteValue(Utf8JsonWriter writer, string name, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined) return;
        writer.WritePropertyName(name);
        value.WriteTo(writer);
    }
}
