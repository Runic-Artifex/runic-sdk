using System.Buffers;
using System;
using System.Collections.Generic;
using System.Text.Json;
using RunicToolkit.MVVM;

namespace RunicToolkit.Hosting.CsWebUi.Mvvm;

internal static class MvvmHostFrameEncoder
{
    internal static byte[] HandshakeResult(
        Guid request,
        IReadOnlyList<string> capabilities,
        MvvmLimits limits) =>
        Encode(limits, writer =>
        {
            Envelope(writer, "handshakeResult");
            Uuid(writer, "request", request);
            writer.WritePropertyName("payload");
            writer.WriteStartObject();
            writer.WriteNumber("selectedVersion", 1);
            writer.WritePropertyName("capabilities");
            writer.WriteStartArray();
            foreach (string capability in capabilities)
            {
                writer.WriteStringValue(capability);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("limits");
            writer.WriteStartObject();
            writer.WriteNumber("maxFrameBytes", limits.MaxPayloadBytes);
            writer.WriteNumber("maxJsonDepth", limits.MaxJsonDepth);
            writer.WriteNumber("maxSessions", 1);
            writer.WriteNumber("maxPendingRequests", limits.MaxPendingRequests);
            writer.WriteNumber("maxSnapshotMembers", limits.MaxSnapshotMembers);
            writer.WriteNumber("maxPatchChanges", limits.MaxPatchOperations);
            writer.WriteNumber("maxCollectionItems", limits.MaxCollectionItems);
            writer.WriteNumber(
                "commandTimeoutMilliseconds",
                checked((int)limits.MaxCommandDuration.TotalMilliseconds));
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    internal static byte[] Opened(
        IMvvmSession session,
        Guid view,
        Guid request,
        JsonElement snapshot,
        MvvmLimits limits) =>
        Encode(limits, writer =>
        {
            Envelope(writer, "opened");
            writer.WriteString("contract", session.Contract.Value);
            Uuid(writer, "session", session.Id.Value);
            Uuid(writer, "view", view);
            Uuid(writer, "request", request);
            writer.WriteString("capability", session.CapabilityToken);
            writer.WritePropertyName("payload");
            writer.WriteStartObject();
            writer.WritePropertyName("snapshot");
            WriteSnapshot(writer, snapshot, session.Revision);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    internal static byte[] Patch(
        IMvvmSession session,
        Guid view,
        long fromRevision,
        MvvmResponse response,
        MvvmLimits limits) =>
        Encode(limits, writer =>
        {
            SessionEnvelope(writer, "patch", session.Id.Value, view);
            writer.WritePropertyName("payload");
            writer.WriteStartObject();
            writer.WriteNumber("fromRevision", fromRevision);
            writer.WriteNumber("toRevision", response.Revision);
            writer.WritePropertyName("changes");
            writer.WriteStartArray();
            foreach (MvvmPatch patch in response.Patches)
            {
                WritePatch(writer, patch);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    internal static byte[] Result(
        IMvvmSession session,
        Guid view,
        MvvmRequest request,
        MvvmResponse response,
        MvvmLimits limits) =>
        Encode(limits, writer =>
        {
            SessionEnvelope(writer, "result", session.Id.Value, view);
            Uuid(writer, "request", request.RequestId.Value);
            writer.WritePropertyName("payload");
            writer.WriteStartObject();
            switch (request)
            {
                case MvvmMutationRequest { Kind: MvvmMutationKind.SetProperty }:
                    writer.WriteString("operation", "setProperty");
                    break;
                case MvvmMutationRequest { Kind: MvvmMutationKind.ExecuteCommand }:
                    writer.WriteString("operation", "execute");
                    break;
                case MvvmAcknowledgeRequest:
                    writer.WriteString("operation", "ack");
                    break;
                case MvvmCancelRequest cancellation:
                    writer.WriteString("operation", "cancel");
                    Uuid(writer, "targetRequest", cancellation.TargetRequestId.Value);
                    writer.WriteBoolean("accepted", response.CancellationAccepted ?? false);
                    break;
                default:
                    throw new InvalidOperationException("The request does not have a result envelope.");
            }

            writer.WriteNumber("revision", response.Revision);
            if (request is MvvmMutationRequest { Kind: MvvmMutationKind.ExecuteCommand } &&
                response.Payload is JsonElement value)
            {
                writer.WritePropertyName("value");
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    internal static byte[] Snapshot(
        IMvvmSession session,
        Guid view,
        MvvmRequestId request,
        JsonElement snapshot,
        MvvmLimits limits) =>
        Encode(limits, writer =>
        {
            SessionEnvelope(writer, "snapshot", session.Id.Value, view);
            Uuid(writer, "request", request.Value);
            writer.WritePropertyName("payload");
            WriteSnapshot(writer, snapshot, session.Revision);
            writer.WriteEndObject();
        });

    internal static byte[] Fault(
        IMvvmSession? session,
        Guid? view,
        Guid request,
        string code,
        string message,
        long? currentRevision,
        MvvmLimits limits) =>
        Encode(limits, writer =>
        {
            Envelope(writer, "fault");
            if (session is not null && view is Guid viewId)
            {
                Uuid(writer, "session", session.Id.Value);
                Uuid(writer, "view", viewId);
            }

            Uuid(writer, "request", request);
            writer.WritePropertyName("payload");
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteBoolean(
                "retryable",
                code is MvvmFaultCodes.RevisionStale or
                    MvvmFaultCodes.LimitExceeded or
                    MvvmFaultCodes.RequestTimeout);
            if (code == MvvmFaultCodes.RevisionStale)
            {
                writer.WriteNumber("currentRevision", currentRevision ?? 0);
                writer.WriteBoolean("snapshotRequired", true);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    internal static byte[] Closed(
        IMvvmSession session,
        Guid view,
        Guid request,
        string reason,
        MvvmLimits limits) =>
        Encode(limits, writer =>
        {
            SessionEnvelope(writer, "closed", session.Id.Value, view);
            Uuid(writer, "request", request);
            writer.WritePropertyName("payload");
            writer.WriteStartObject();
            writer.WriteNumber("revision", session.Revision);
            writer.WriteString("reason", reason);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    private static byte[] Encode(MvvmLimits limits, Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        MvvmWireMessage validated = MvvmMessageCodec.DecodeHost(buffer.WrittenSpan, limits);
        return MvvmMessageCodec.Encode(validated, limits);
    }

    private static void Envelope(Utf8JsonWriter writer, string kind)
    {
        writer.WriteStartObject();
        writer.WriteNumber("v", 1);
        writer.WriteString("kind", kind);
    }

    private static void SessionEnvelope(
        Utf8JsonWriter writer,
        string kind,
        Guid session,
        Guid view)
    {
        Envelope(writer, kind);
        Uuid(writer, "session", session);
        Uuid(writer, "view", view);
    }

    private static void Uuid(Utf8JsonWriter writer, string name, Guid value) =>
        writer.WriteString(name, value.ToString("D"));

    private static void WritePatch(Utf8JsonWriter writer, MvvmPatch patch)
    {
        writer.WriteStartObject();
        switch (patch)
        {
            case MvvmPropertyPatch property:
                writer.WriteString("type", "property");
                writer.WriteNumber("member", property.MemberId);
                writer.WritePropertyName("value");
                property.Value.WriteTo(writer);
                break;
            case MvvmCollectionPatch collection:
                writer.WriteString("type", "collection");
                writer.WriteNumber("member", collection.MemberId);
                writer.WriteString("operation", collection.Operation switch
                {
                    MvvmCollectionOperation.Insert => "insert",
                    MvvmCollectionOperation.Remove => "remove",
                    MvvmCollectionOperation.Replace => "replace",
                    MvvmCollectionOperation.Reset => "reset",
                    _ => throw new InvalidOperationException("Unknown collection operation."),
                });
                writer.WriteNumber("index", collection.Index);
                writer.WritePropertyName("items");
                writer.WriteStartArray();
                foreach (JsonElement item in collection.Items)
                {
                    item.WriteTo(writer);
                }

                writer.WriteEndArray();
                break;
            case MvvmCollectionMovePatch move:
                writer.WriteString("type", "collectionMove");
                writer.WriteNumber("member", move.MemberId);
                writer.WriteNumber("from", move.From);
                writer.WriteNumber("to", move.To);
                writer.WriteNumber("count", move.Count);
                break;
            case MvvmCommandPatch command:
                writer.WriteString("type", "command");
                writer.WriteNumber("member", command.MemberId);
                writer.WriteBoolean("canExecute", command.CanExecute);
                writer.WriteBoolean("isExecuting", command.IsExecuting);
                break;
            case MvvmValidationPatch validation:
                writer.WriteString("type", "validation");
                writer.WriteNumber("member", validation.MemberId);
                writer.WritePropertyName("errors");
                writer.WriteStartArray();
                foreach (string error in validation.Errors)
                {
                    writer.WriteStringValue(error);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new InvalidOperationException("Unknown patch kind.");
        }

        writer.WriteEndObject();
    }

    private static void WriteSnapshot(
        Utf8JsonWriter writer,
        JsonElement snapshot,
        long revision)
    {
        writer.WriteStartObject();
        writer.WriteNumber("revision", revision);
        writer.WritePropertyName("members");
        snapshot.GetProperty("members").WriteTo(writer);
        writer.WriteEndObject();
    }
}
