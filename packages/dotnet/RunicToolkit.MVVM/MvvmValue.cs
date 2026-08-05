using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace RunicToolkit.MVVM;

/// <summary>Creates detached JSON values without reflection-based serialization.</summary>
public static class MvvmValue
{
    private static readonly JsonElement NullElement = Create(static writer => writer.WriteNullValue());

    /// <summary>Gets a detached JSON <see langword="null"/> value.</summary>
    public static JsonElement Null => NullElement.Clone();

    /// <summary>Serializes a value using explicit source-generated metadata.</summary>
    /// <remarks>
    /// Passing <see cref="JsonTypeInfo{T}"/> keeps the call usable under trimming and Native AOT.
    /// The supplied metadata controls property names, converters, and their deterministic order.
    /// </remarks>
    public static JsonElement From<T>(T value, JsonTypeInfo<T> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        return JsonSerializer.SerializeToElement(value, jsonTypeInfo);
    }

    /// <summary>Creates one detached JSON value with a low-allocation UTF-8 writer callback.</summary>
    /// <remarks>
    /// The callback is trusted local projection code, not a wire decoder. It should write a bounded
    /// value; the session applies configured depth, string, item, and encoded-payload limits before
    /// publishing the value.
    /// </remarks>
    public static JsonElement Create(Action<Utf8JsonWriter> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writeValue(writer);
            writer.Flush();
        }

        using JsonDocument document = JsonDocument.Parse(
            buffer.WrittenMemory,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MvvmLimits.MaximumJsonDepth,
            });
        return document.RootElement.Clone();
    }

    /// <summary>Creates a JSON string value.</summary>
    public static JsonElement From(string? value) =>
        Create(writer => writer.WriteStringValue(value));

    /// <summary>Creates a JSON Boolean value.</summary>
    public static JsonElement From(bool value) =>
        Create(writer => writer.WriteBooleanValue(value));

    /// <summary>Creates a JSON signed integer value.</summary>
    public static JsonElement From(long value) =>
        Create(writer => writer.WriteNumberValue(value));

    /// <summary>Creates a JSON unsigned integer value.</summary>
    public static JsonElement From(ulong value) =>
        Create(writer => writer.WriteNumberValue(value));

    /// <summary>Creates a JSON decimal value.</summary>
    public static JsonElement From(decimal value) =>
        Create(writer => writer.WriteNumberValue(value));

    /// <summary>Creates a finite JSON double value.</summary>
    public static JsonElement From(double value) =>
        Create(writer => writer.WriteNumberValue(value));
}
