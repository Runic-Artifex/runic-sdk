using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.CommandLine;

/// <summary>Writes byte-stable <c>webuitoolkit.cli/1</c> response envelopes.</summary>
public static class CommandJsonEnvelopeWriter
{
    /// <summary>Serializes one compact UTF-8 JSON object followed by one LF.</summary>
    /// <remarks>
    /// Payload serialization is possible only through caller-supplied source-generated metadata.
    /// The returned byte array never contains a BOM or raw ANSI control sequence.
    /// </remarks>
    public static byte[] Serialize<T>(CommandResponse<T> response, JsonTypeInfo<T> payloadTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(payloadTypeInfo);

        var buffer = new BoundedBufferWriter(CommandJsonEnvelopeReader.DefaultMaximumFrameBytes - 1);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            MaxDepth = 32,
            SkipValidation = false,
        }))
        {
            WriteEnvelope(writer, response, payloadTypeInfo);
        }

        if (buffer.WrittenCount >= CommandJsonEnvelopeReader.DefaultMaximumFrameBytes)
        {
            throw new CommandProtocolException(
                "frame-too-large",
                "The serialized command response exceeds the maximum frame size.");
        }

        byte[] framed = GC.AllocateUninitializedArray<byte>(buffer.WrittenCount + 1);
        buffer.WrittenSpan.CopyTo(framed);
        framed[^1] = (byte)'\n';
        return framed;
    }

    /// <summary>Writes exactly one response frame to standard output.</summary>
    public static ValueTask WriteAsync<T>(
        ICommandConsole console,
        CommandResponse<T> response,
        ICommandResultCodec<T> codec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(codec);
        EnsurePayloadIdentity(response, codec.PayloadType);

        byte[] bytes = Serialize(response, codec.TypeInfo);
        return console.WriteOutBytesAsync(bytes.AsMemory(), cancellationToken);
    }

    private static void WriteEnvelope<T>(
        Utf8JsonWriter writer,
        CommandResponse<T> response,
        JsonTypeInfo<T> payloadTypeInfo)
    {
        writer.WriteStartObject();
        writer.WriteString("protocol", CliProtocol.Identity);
        writer.WriteString("requestId", response.RequestId);
        writer.WriteString("command", response.Command);
        writer.WriteBoolean("success", response.Success);
        writer.WriteNumber("exitCode", response.ExitCode);

        if (response.Success)
        {
            writer.WriteString("payloadType", response.PayloadType);
            writer.WritePropertyName("payload");
            JsonSerializer.Serialize<T>(writer, response.Payload!, payloadTypeInfo);
            writer.WriteNull("fault");
        }
        else
        {
            writer.WriteNull("payloadType");
            writer.WriteNull("payload");
            writer.WritePropertyName("fault");
            WriteFault(writer, response.Fault!);
        }

        writer.WritePropertyName("diagnostics");
        writer.WriteStartArray();
        foreach (CommandDiagnostic diagnostic in response.Diagnostics)
        {
            WriteDiagnostic(writer, diagnostic);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteFault(Utf8JsonWriter writer, CommandFault unsafeFault)
    {
        CommandFault fault = CommandFaultSanitizer.Sanitize(unsafeFault);
        writer.WriteStartObject();
        writer.WriteString("code", fault.Code);
        writer.WriteString("message", fault.Message);
        writer.WritePropertyName("details");
        writer.WriteStartObject();

        var keys = new List<string>(fault.Details.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            writer.WriteString(key, fault.Details[key]);
        }

        writer.WriteEndObject();
        writer.WriteBoolean("retryable", fault.Retryable);
        writer.WriteEndObject();
    }

    private static void WriteDiagnostic(Utf8JsonWriter writer, CommandDiagnostic diagnostic)
    {
        int pathBytes = diagnostic.Path.Count == 0 ? 0 : diagnostic.Path.Count - 1;
        foreach (string segment in diagnostic.Path.Segments)
        {
            pathBytes += Encoding.UTF8.GetByteCount(segment);
        }

        if (pathBytes > 512)
        {
            throw new CommandProtocolException(
                "invalid-diagnostic",
                "A command diagnostic path exceeds the protocol limit.");
        }

        bool technicalContent = CommandFaultSanitizer.ContainsTechnicalContent(diagnostic.Message);
        foreach (string argument in diagnostic.Arguments)
        {
            technicalContent |= CommandFaultSanitizer.ContainsTechnicalContent(argument);
        }

        writer.WriteStartObject();
        writer.WriteString("code", diagnostic.Code);
        writer.WriteString("kind", diagnostic.Kind);
        writer.WritePropertyName("commandPath");
        writer.WriteStartArray();
        foreach (string segment in diagnostic.Path.Segments)
        {
            writer.WriteStringValue(segment);
        }

        writer.WriteEndArray();
        writer.WriteString("messageKey", diagnostic.MessageKey);
        if (Encoding.UTF8.GetByteCount(diagnostic.Kind) > 128)
        {
            throw new CommandProtocolException(
                "invalid-diagnostic",
                "A command diagnostic kind exceeds the protocol limit.");
        }

        writer.WriteString(
            "message",
            technicalContent
                ? "The diagnostic content was redacted."
                : CommandFaultSanitizer.SanitizeRequiredText(diagnostic.Message));
        writer.WriteString("phase", DiagnosticPhaseName(diagnostic.Phase));
        writer.WriteString("severity", DiagnosticSeverityName(diagnostic.Severity));
        if (diagnostic.TokenIndex is int tokenIndex)
        {
            writer.WriteNumber("tokenIndex", tokenIndex);
        }
        else
        {
            writer.WriteNull("tokenIndex");
        }

        writer.WritePropertyName("arguments");
        writer.WriteStartArray();
        if (!technicalContent)
        {
            foreach (string argument in diagnostic.Arguments)
            {
                writer.WriteStringValue(CommandFaultSanitizer.SanitizeArgument(argument));
            }
        }

        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static void EnsurePayloadIdentity<T>(CommandResponse<T> response, string registeredPayloadType)
    {
        CommandResponseValidation.ValidatePayloadType(registeredPayloadType, nameof(registeredPayloadType));
        if (response.Success && !string.Equals(response.PayloadType, registeredPayloadType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The response payload identity does not match its registered codec.");
        }
    }

    private static string DiagnosticPhaseName(CommandDiagnosticPhase phase) => phase switch
    {
        CommandDiagnosticPhase.Parse => "parse",
        CommandDiagnosticPhase.Binding => "binding",
        CommandDiagnosticPhase.Execution => "execution",
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    private static string DiagnosticSeverityName(CommandDiagnosticSeverity severity) => severity switch
    {
        CommandDiagnosticSeverity.Error => "error",
        CommandDiagnosticSeverity.Warning => "warning",
        CommandDiagnosticSeverity.Information => "information",
        _ => throw new ArgumentOutOfRangeException(nameof(severity)),
    };

    private sealed class BoundedBufferWriter : IBufferWriter<byte>
    {
        private readonly ArrayBufferWriter<byte> _inner = new();
        private readonly int _maximumBytes;

        internal BoundedBufferWriter(int maximumBytes) => _maximumBytes = maximumBytes;

        internal int WrittenCount => _inner.WrittenCount;

        internal ReadOnlySpan<byte> WrittenSpan => _inner.WrittenSpan;

        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count > _maximumBytes - _inner.WrittenCount)
            {
                throw FrameTooLarge();
            }

            _inner.Advance(count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            int available = Available(sizeHint);
            Memory<byte> memory = _inner.GetMemory(Math.Max(sizeHint, 1));
            return memory[..Math.Min(memory.Length, available)];
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            int available = Available(sizeHint);
            Span<byte> span = _inner.GetSpan(Math.Max(sizeHint, 1));
            return span[..Math.Min(span.Length, available)];
        }

        private int Available(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            int remaining = _maximumBytes - _inner.WrittenCount;
            if (remaining == 0 || sizeHint > remaining)
            {
                throw FrameTooLarge();
            }

            return remaining;
        }

        private static CommandProtocolException FrameTooLarge() => new(
            "frame-too-large",
            "The serialized command response exceeds the maximum frame size.");
    }
}
