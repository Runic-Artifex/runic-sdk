using System.Text;
using System.Text.Json;

namespace RunicToolkit.ApplicationBridge;

/// <summary>Strict, bounded, source-generated Application Bridge framing.</summary>
public static class ApplicationBridgeCodec
{
    /// <summary>Decodes one untrusted client frame.</summary>
    public static bool TryDecodeClient(
        ReadOnlySpan<byte> frame,
        out BridgeClientEnvelope? envelope,
        BridgeLimits? limits = null)
    {
        BridgeLimits selected = limits ?? BridgeLimits.Default;
        selected.Validate();
        envelope = null;
        if (frame.Length == 0 || frame.Length > selected.MaxFrameBytes)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(frame, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = selected.MaxDepth,
            });
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            bool hasTrailingValue = reader.Read();
            if (hasTrailingValue || reader.BytesConsumed != frame.Length || !ValidateJson(document.RootElement, selected))
            {
                return false;
            }
            envelope = JsonSerializer.Deserialize(
                document.RootElement,
                ApplicationBridgeJsonContext.Default.BridgeClientEnvelope);
            return envelope is not null && Validate(envelope);
        }
        catch (JsonException)
        {
            envelope = null;
            return false;
        }
    }

    /// <summary>Encodes one validated host envelope.</summary>
    public static byte[] EncodeHost(BridgeHostEnvelope envelope, BridgeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        BridgeLimits selected = limits ?? BridgeLimits.Default;
        selected.Validate();
        byte[] frame = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            ApplicationBridgeJsonContext.Default.BridgeHostEnvelope);
        if (frame.Length > selected.MaxFrameBytes)
        {
            throw new InvalidOperationException("The encoded Application Bridge frame exceeds its configured limit.");
        }
        return frame;
    }

    /// <summary>Writes one validated host envelope into an existing bounded JSON frame.</summary>
    public static void WriteHost(
        Utf8JsonWriter writer,
        BridgeHostEnvelope envelope,
        BridgeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(envelope);
        BridgeLimits selected = limits ?? BridgeLimits.Default;
        selected.Validate();
        long before = writer.BytesCommitted + writer.BytesPending;
        JsonSerializer.Serialize(writer, envelope, ApplicationBridgeJsonContext.Default.BridgeHostEnvelope);
        long encodedBytes = writer.BytesCommitted + writer.BytesPending - before;
        if (encodedBytes > selected.MaxFrameBytes)
        {
            throw new InvalidOperationException("The encoded Application Bridge frame exceeds its configured limit.");
        }
    }

    private static bool Validate(BridgeClientEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.Protocol) || envelope.Version < 1 || envelope.CommandId == Guid.Empty)
        {
            return false;
        }

        return envelope.Kind switch
        {
            "initialize" => envelope.SessionId is null && envelope.ExpectedRevision is null,
            "dispatch" => envelope.SessionId is not null && envelope.ExpectedRevision is >= 0,
            "cancelOperation" => envelope.SessionId is not null && envelope.ExpectedRevision is >= 0,
            "uiReady" or "uiRendered" => envelope.SessionId is not null,
            _ => false,
        };
    }

    private static bool ValidateJson(JsonElement element, BridgeLimits limits)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                int count = 0;
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (++count > limits.MaxCollectionItems ||
                        Encoding.UTF8.GetByteCount(property.Name) > limits.MaxStringBytes ||
                        !names.Add(property.Name) ||
                        !ValidateJson(property.Value, limits))
                    {
                        return false;
                    }
                }
                return true;
            }
            case JsonValueKind.Array:
            {
                int count = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (++count > limits.MaxCollectionItems || !ValidateJson(item, limits))
                    {
                        return false;
                    }
                }
                return true;
            }
            case JsonValueKind.String:
                return Encoding.UTF8.GetByteCount(element.GetString()!) <= limits.MaxStringBytes;
            default:
                return true;
        }
    }
}
