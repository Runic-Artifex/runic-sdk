using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace RunicCommandLine;

/// <summary>Reads bounded <c>runic.commandline/1</c> response envelopes.</summary>
public static class CommandJsonEnvelopeReader
{
    /// <summary>The default maximum frame size accepted by <see cref="Read{T}"/>.</summary>
    public const int DefaultMaximumFrameBytes = 1_048_576;

    /// <summary>
    /// Reads one UTF-8 JSON response terminated by one LF and deserializes its registered payload.
    /// </summary>
    /// <remarks>
    /// Unknown additive object members are accepted. Required members, their types, and response
    /// invariants remain strict. Payload deserialization uses only the supplied source-generated metadata.
    /// </remarks>
    public static CommandResponse<T> Read<T>(
        ReadOnlySpan<byte> frame,
        string expectedPayloadType,
        JsonTypeInfo<T> payloadTypeInfo,
        int maximumFrameBytes = DefaultMaximumFrameBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPayloadType);
        ArgumentNullException.ThrowIfNull(payloadTypeInfo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrameBytes);
        CommandResponseValidation.ValidatePayloadType(expectedPayloadType, nameof(expectedPayloadType));

        ValidateFrame(frame, maximumFrameBytes);
        ValidateDepth(frame[..^1]);

        try
        {
            var jsonReader = new Utf8JsonReader(frame[..^1], new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                AllowMultipleValues = true,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            using JsonDocument document = JsonDocument.ParseValue(ref jsonReader);
            if (jsonReader.Read())
            {
                throw Error("multiple-json-values", "The command response contains more than one JSON value.");
            }

            JsonElement root = document.RootElement;
            RequireObject(root, "response");
            RejectDuplicateMembers(root, "response");

            string protocol = RequireString(root, "protocol");
            if (!string.Equals(protocol, CliProtocol.Identity, StringComparison.Ordinal))
            {
                throw Error("unsupported-protocol", "The command response protocol is not supported.");
            }

            JsonElement requestIdElement = RequireProperty(root, "requestId");
            if (requestIdElement.ValueKind != JsonValueKind.String)
            {
                throw Error("invalid-request-id", "The command response request ID must be a string.");
            }

            string requestId = requestIdElement.GetString()!;
            string command = RequireString(root, "command");
            try
            {
                CommandResponseValidation.ValidateRequestId(requestId, nameof(requestId));
            }
            catch (ArgumentException exception)
            {
                throw Error("invalid-request-id", "The command response request ID is invalid.", exception);
            }

            try
            {
                CommandResponseValidation.ValidateCommand(command, nameof(command));
            }
            catch (ArgumentException exception)
            {
                throw Error("invalid-command", "The command response command path is invalid.", exception);
            }

            bool success = RequireBoolean(root, "success");
            int exitCode = RequireInt32(root, "exitCode");
            string? payloadType = RequireNullableString(root, "payloadType");
            JsonElement payloadElement = RequireProperty(root, "payload");
            JsonElement faultElement = RequireProperty(root, "fault");
            JsonElement diagnosticsElement = RequireProperty(root, "diagnostics");

            var diagnostics = ReadDiagnostics(diagnosticsElement);
            if (success)
            {
                if (exitCode != 0)
                {
                    throw Error("outcome-invariant", "A successful response must use exit code zero.");
                }

                if (payloadType is null)
                {
                    throw Error("missing-payload-type", "A successful response must declare a payload type.");
                }

                try
                {
                    CommandResponseValidation.ValidatePayloadType(payloadType, nameof(payloadType));
                }
                catch (ArgumentException exception)
                {
                    throw Error("invalid-payload-type", "The response payload type is invalid.", exception);
                }

                if (!string.Equals(payloadType, expectedPayloadType, StringComparison.Ordinal))
                {
                    throw Error("unsupported-payload-type", "The response payload type is not supported by the selected codec.");
                }

                if (faultElement.ValueKind != JsonValueKind.Null)
                {
                    throw Error("outcome-invariant", "A successful response cannot contain a fault.");
                }

                T? payload;
                try
                {
                    payload = payloadElement.Deserialize(payloadTypeInfo);
                }
                catch (JsonException exception)
                {
                    throw Error(
                        "payload-shape-mismatch",
                        "The response payload does not match its registered JSON contract.",
                        exception);
                }
                return CommandResponse<T>.Read(
                    requestId,
                    command,
                    true,
                    exitCode,
                    payloadType,
                    payload,
                    null,
                    diagnostics);
            }

            if (exitCode == 0)
            {
                throw Error("outcome-invariant", "A failed response cannot use exit code zero.");
            }

            if (payloadType is not null || payloadElement.ValueKind != JsonValueKind.Null)
            {
                throw Error("outcome-invariant", "A failed response cannot contain a payload.");
            }

            if (faultElement.ValueKind == JsonValueKind.Null)
            {
                throw Error("missing-fault", "A failed response must contain a fault.");
            }

            CommandFault fault = ReadFault(faultElement);
            return CommandResponse<T>.Read(
                requestId,
                command,
                false,
                exitCode,
                null,
                default,
                fault,
                diagnostics);
        }
        catch (CommandProtocolException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Error("malformed-json", "The command response contains malformed JSON.", exception);
        }
        catch (ArgumentException exception)
        {
            throw Error("invalid-envelope", "The command response violates a protocol invariant.", exception);
        }
    }

    private static void ValidateFrame(ReadOnlySpan<byte> frame, int maximumFrameBytes)
    {
        if (frame.Length == 0)
        {
            throw Error("missing-envelope", "The command response frame is empty.");
        }

        if (frame.Length > maximumFrameBytes)
        {
            throw Error("framed-byte-limit", "The command response exceeds the configured frame bound.");
        }

        if (frame.Length >= 3 && frame[0] == 0xEF && frame[1] == 0xBB && frame[2] == 0xBF)
        {
            throw Error("utf8-bom", "The command response must not contain a UTF-8 BOM.");
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(frame[..^1]);
        }
        catch (DecoderFallbackException exception)
        {
            throw Error("invalid-utf8", "The command response is not valid UTF-8.", exception);
        }

        if (frame.Length < 3 || frame[^1] != (byte)'\n' ||
            frame[^2] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            throw Error("invalid-framing", "The command response must be one compact JSON object followed by one LF.");
        }

        if (frame[0] != (byte)'{')
        {
            string kind = frame[0] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'
                ? "invalid-framing"
                : "stdout-contamination";
            throw Error(kind, "Standard output contains content before the command response.");
        }
    }

    private static CommandFault ReadFault(JsonElement element)
    {
        RequireObject(element, "fault");
        RejectDuplicateMembers(element, "fault");

        string code = RequireString(element, "code");
        string message = RequireString(element, "message");
        if (!CommandFaultSanitizer.IsSafeCode(code) ||
            !CommandFaultSanitizer.IsSafeText(message, 4_096, allowEmpty: false))
        {
            throw Error("unsafe-fault-content", "The command fault contains unsafe text.");
        }

        bool retryable = RequireBoolean(element, "retryable");
        JsonElement detailsElement = RequireProperty(element, "details");
        RequireObject(detailsElement, "fault.details");
        RejectDuplicateMembers(detailsElement, "fault.details");

        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in detailsElement.EnumerateObject())
        {
            if (details.Count == 32)
            {
                throw Error("invalid-envelope", "The command fault contains too many detail members.");
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw Error("invalid-envelope", "Command fault detail values must be strings.");
            }

            string detailValue = property.Value.GetString()!;
            if (!CommandFaultSanitizer.IsSafeText(property.Name, 64, allowEmpty: false) ||
                !CommandFaultSanitizer.IsSafeText(detailValue, 1_024, allowEmpty: true))
            {
                throw Error("unsafe-fault-content", "The command fault contains unsafe detail text.");
            }

            details.Add(property.Name, detailValue);
        }

        return new CommandFault(code, message, details, retryable);
    }

    private static void ValidateDepth(ReadOnlySpan<byte> json)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int index = 0; index < json.Length; index++)
        {
            byte value = json[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (value == (byte)'\\')
                {
                    escaped = true;
                }
                else if (value == (byte)'"')
                {
                    inString = false;
                }

                continue;
            }

            if (value == (byte)'"')
            {
                inString = true;
            }
            else if (value is (byte)'{' or (byte)'[')
            {
                depth++;
                if (depth > 32)
                {
                    throw Error("json-depth-limit", "The command response exceeds the JSON depth limit.");
                }
            }
            else if (value is (byte)'}' or (byte)']')
            {
                depth--;
                if (depth == 0 && HasNonWhitespace(json[(index + 1)..]))
                {
                    throw Error(
                        "multiple-json-values",
                        "The command response contains content after its root JSON object.");
                }
            }
        }
    }

    private static bool HasNonWhitespace(ReadOnlySpan<byte> value)
    {
        foreach (byte character in value)
        {
            if (character is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return true;
            }
        }

        return false;
    }

    private static List<CommandDiagnostic> ReadDiagnostics(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Error("invalid-envelope", "The response diagnostics member must be an array.");
        }

        if (element.GetArrayLength() > 64)
        {
            throw Error("invalid-envelope", "The response contains too many diagnostics.");
        }

        var diagnostics = new List<CommandDiagnostic>(element.GetArrayLength());
        foreach (JsonElement diagnosticElement in element.EnumerateArray())
        {
            RequireObject(diagnosticElement, "diagnostic");
            RejectDuplicateMembers(diagnosticElement, "diagnostic");

            string code = RequireString(diagnosticElement, "code");
            string kind = RequireString(diagnosticElement, "kind");
            CommandPath path = ReadCommandPath(RequireProperty(diagnosticElement, "commandPath"));
            string messageKey = RequireString(diagnosticElement, "messageKey");
            if (!IsMessageKey(messageKey))
            {
                throw Error("invalid-diagnostic-message-key", "A diagnostic message key is invalid.");
            }

            string message = RequireString(diagnosticElement, "message");
            if (Encoding.UTF8.GetByteCount(kind) > 128 ||
                !CommandFaultSanitizer.IsSafeText(message, 4_096, allowEmpty: false))
            {
                throw Error("unsafe-diagnostic-content", "A command diagnostic contains unsafe text.");
            }
            CommandDiagnosticPhase phase = ReadPhase(RequireString(diagnosticElement, "phase"));
            CommandDiagnosticSeverity severity = ReadSeverity(RequireString(diagnosticElement, "severity"));
            int? tokenIndex = RequireNullableInt32(diagnosticElement, "tokenIndex");
            IReadOnlyList<string> arguments = ReadDiagnosticArguments(
                RequireProperty(diagnosticElement, "arguments"));
            try
            {
                diagnostics.Add(new CommandDiagnostic(
                    code,
                    kind,
                    message,
                    phase,
                    severity,
                    tokenIndex,
                    arguments,
                    path,
                    messageKey));
            }
            catch (ArgumentException exception)
            {
                throw Error("invalid-diagnostic", "A command diagnostic is invalid.", exception);
            }
        }

        return diagnostics;
    }

    private static CommandPath ReadCommandPath(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 64)
        {
            throw Error("invalid-diagnostic", "A diagnostic command path must be a bounded array.");
        }

        var segments = new List<string>(element.GetArrayLength());
        int utf8Bytes = 0;
        foreach (JsonElement segmentElement in element.EnumerateArray())
        {
            if (segmentElement.ValueKind != JsonValueKind.String)
            {
                throw Error("invalid-diagnostic", "Diagnostic command path segments must be strings.");
            }

            string segment = segmentElement.GetString()!;
            utf8Bytes += Encoding.UTF8.GetByteCount(segment);
            if (segments.Count != 0)
            {
                utf8Bytes++;
            }

            if (utf8Bytes > 512)
            {
                throw Error("invalid-diagnostic", "A diagnostic command path exceeds the protocol limit.");
            }

            segments.Add(segment);
        }

        try
        {
            return segments.Count == 0 ? CommandPath.Root : new CommandPath(segments);
        }
        catch (ArgumentException exception)
        {
            throw Error(
                "invalid-diagnostic-command-path",
                "A diagnostic command path is invalid.",
                exception);
        }
    }

    private static bool IsMessageKey(string value)
    {
        if (value.Length is 0 or > 128 || !IsAsciiLetter(value[0]))
        {
            return false;
        }

        for (int index = 1; index < value.Length; index++)
        {
            char character = value[index];
            if (!IsAsciiLetter(character) &&
                character is not ('.' or '_' or '-' or >= '0' and <= '9'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static List<string> ReadDiagnosticArguments(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 16)
        {
            throw Error("invalid-envelope", "Diagnostic arguments must be an array of at most 16 strings.");
        }

        var arguments = new List<string>(element.GetArrayLength());
        foreach (JsonElement argument in element.EnumerateArray())
        {
            if (argument.ValueKind != JsonValueKind.String)
            {
                throw Error("invalid-envelope", "Diagnostic arguments must be strings.");
            }

            string value = argument.GetString()!;
            if (!CommandFaultSanitizer.IsSafeText(value, 1_024, allowEmpty: true))
            {
                throw Error("unsafe-diagnostic-content", "A command diagnostic argument contains unsafe text.");
            }

            arguments.Add(value);
        }

        return arguments;
    }

    private static CommandDiagnosticPhase ReadPhase(string value) => value switch
    {
        "parse" => CommandDiagnosticPhase.Parse,
        "binding" => CommandDiagnosticPhase.Binding,
        "execution" => CommandDiagnosticPhase.Execution,
        _ => throw Error("invalid-diagnostic", "The response contains an unknown diagnostic phase."),
    };

    private static CommandDiagnosticSeverity ReadSeverity(string value) => value switch
    {
        "error" => CommandDiagnosticSeverity.Error,
        "warning" => CommandDiagnosticSeverity.Warning,
        "information" => CommandDiagnosticSeverity.Information,
        _ => throw Error("invalid-diagnostic", "The response contains an unknown diagnostic severity."),
    };

    private static JsonElement RequireProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            throw Error("missing-required-field", $"The command response is missing required member '{name}'.");
        }

        return value;
    }

    private static string RequireString(JsonElement element, string name)
    {
        JsonElement value = RequireProperty(element, name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Error("invalid-envelope", $"The command response member '{name}' must be a string.");
        }

        return value.GetString()!;
    }

    private static string? RequireNullableString(JsonElement element, string name)
    {
        JsonElement value = RequireProperty(element, name);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Error("invalid-envelope", $"The command response member '{name}' must be a string or null.");
        }

        return value.GetString();
    }

    private static bool RequireBoolean(JsonElement element, string name)
    {
        JsonElement value = RequireProperty(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Error("invalid-envelope", $"The command response member '{name}' must be Boolean."),
        };
    }

    private static int RequireInt32(JsonElement element, string name)
    {
        JsonElement value = RequireProperty(element, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
        {
            throw Error("invalid-envelope", $"The command response member '{name}' must be a 32-bit integer.");
        }

        return result;
    }

    private static int? RequireNullableInt32(JsonElement element, string name)
    {
        JsonElement value = RequireProperty(element, name);
        return value.ValueKind == JsonValueKind.Null ? null : RequireInt32(element, name);
    }

    private static void RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Error("invalid-envelope", $"The command response member '{name}' must be an object.");
        }
    }

    private static void RejectDuplicateMembers(JsonElement element, string name)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!members.Add(property.Name))
            {
                throw Error("duplicate-property", $"The command response member '{name}' contains a duplicate property.");
            }
        }
    }

    private static CommandProtocolException Error(string kind, string message) => new(kind, message);

    private static CommandProtocolException Error(string kind, string message, Exception innerException) =>
        new(kind, message, innerException);
}
