using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Runic.Desktop;

/// <summary>Contains a schema-identified, strictly validated UTF-8 JSON payload.</summary>
public sealed class StructuredPayload
{
    private StructuredPayload(string schemaId, ReadOnlyMemory<byte> utf8Json)
    {
        SchemaId = schemaId;
        Utf8Json = utf8Json;
    }

    public string SchemaId { get; }

    public ReadOnlyMemory<byte> Utf8Json { get; }

    /// <summary>Validates and copies a structured JSON payload.</summary>
    public static StructuredPayload Parse(
        string schemaId,
        ReadOnlySpan<byte> utf8Json,
        int maximumBytes = 1024 * 1024,
        int maximumDepth = 64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDepth);
        if (utf8Json.Length > maximumBytes)
        {
            throw new DesktopException(
                DesktopErrorCategory.LimitExceeded,
                "structured-payload-too-large",
                "The structured payload exceeds its configured size limit.");
        }

        var bytes = utf8Json.ToArray();
        try
        {
            using var document = JsonDocument.Parse(bytes.AsMemory(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = maximumDepth,
            });
            EnsureUniqueObjectKeys(document.RootElement);
        }
        catch (DuplicateKeyException)
        {
            throw new DesktopException(
                DesktopErrorCategory.InvalidFrame,
                "duplicate-object-key",
                "The structured payload contains a duplicate object key.");
        }
        catch (JsonException)
        {
            throw new DesktopException(
                DesktopErrorCategory.InvalidFrame,
                "invalid-structured-json",
                "The structured payload is not valid UTF-8 JSON.");
        }

        return new StructuredPayload(schemaId, bytes);
    }

    /// <summary>Deserializes through explicit metadata suitable for trimming and NativeAOT.</summary>
    public T? Deserialize<T>(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return JsonSerializer.Deserialize(Utf8Json.Span, typeInfo);
    }

    private static void EnsureUniqueObjectKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new DuplicateKeyException();
                }
                EnsureUniqueObjectKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                EnsureUniqueObjectKeys(item);
            }
        }
    }

    private sealed class DuplicateKeyException : Exception;
}
