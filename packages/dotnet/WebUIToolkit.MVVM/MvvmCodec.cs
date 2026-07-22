using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace WebUIToolkit.MVVM;

/// <summary>Strict, deterministic, reflection-free protocol version 1 JSON codec.</summary>
public static class MvvmMessageCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] ClientKinds =
        ["handshake", "open", "setProperty", "execute", "cancel", "ack", "requestSnapshot", "close"];
    private static readonly string[] HostKinds =
        ["handshakeResult", "opened", "result", "snapshot", "patch", "fault", "closed"];
    private static readonly string[] CapabilityNames =
        ["cancellation", "collections", "commandResults", "patches", "validation"];

    /// <summary>Decodes and validates exactly one client-to-host frame.</summary>
    public static MvvmWireMessage DecodeClient(ReadOnlySpan<byte> utf8, MvvmLimits? limits = null) =>
        Decode(utf8, MvvmMessageDirection.ClientToHost, limits);

    /// <summary>Decodes and validates exactly one host-to-client frame.</summary>
    public static MvvmWireMessage DecodeHost(ReadOnlySpan<byte> utf8, MvvmLimits? limits = null) =>
        Decode(utf8, MvvmMessageDirection.HostToClient, limits);

    /// <summary>Attempts to decode and validate exactly one client-to-host frame.</summary>
    public static bool TryDecodeClient(
        ReadOnlySpan<byte> utf8,
        out MvvmWireMessage? message,
        out MvvmProtocolException? error,
        MvvmLimits? limits = null) => TryDecode(utf8, MvvmMessageDirection.ClientToHost, out message, out error, limits);

    /// <summary>Attempts to decode and validate exactly one host-to-client frame.</summary>
    public static bool TryDecodeHost(
        ReadOnlySpan<byte> utf8,
        out MvvmWireMessage? message,
        out MvvmProtocolException? error,
        MvvmLimits? limits = null) => TryDecode(utf8, MvvmMessageDirection.HostToClient, out message, out error, limits);

    /// <summary>Writes a validated message in deterministic compact UTF-8 JSON form.</summary>
    public static byte[] Encode(MvvmWireMessage message, MvvmLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        MvvmLimits effectiveLimits = GetLimits(limits);
        ValidateGeneralJson(message.Document, effectiveLimits, 0);
        ValidateDocument(message.Document, message.Direction, effectiveLimits, out string kind);
        if (!string.Equals(kind, message.Kind, StringComparison.Ordinal))
        {
            throw Semantic("The validated message discriminator is inconsistent.");
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
        }))
        {
            WriteCanonical(writer, message.Document, null, applicationValue: false);
        }

        if (buffer.WrittenCount > effectiveLimits.MaxPayloadBytes)
        {
            throw Error(MvvmValidationErrorCodes.FrameLimitExceeded, "The encoded frame exceeds the configured limit.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static bool TryDecode(
        ReadOnlySpan<byte> utf8,
        MvvmMessageDirection direction,
        out MvvmWireMessage? message,
        out MvvmProtocolException? error,
        MvvmLimits? limits)
    {
        try
        {
            message = Decode(utf8, direction, limits);
            error = null;
            return true;
        }
        catch (MvvmProtocolException exception)
        {
            message = null;
            error = exception;
            return false;
        }
    }

    private static MvvmWireMessage Decode(
        ReadOnlySpan<byte> utf8,
        MvvmMessageDirection direction,
        MvvmLimits? limits)
    {
        MvvmLimits effectiveLimits = GetLimits(limits);
        if (utf8.Length > effectiveLimits.MaxPayloadBytes)
        {
            throw Error(MvvmValidationErrorCodes.FrameLimitExceeded, "The frame exceeds the configured byte limit.");
        }

        if (utf8.Length >= 3 && utf8[0] == 0xef && utf8[1] == 0xbb && utf8[2] == 0xbf)
        {
            throw Error(MvvmValidationErrorCodes.ByteOrderMarkForbidden, "A UTF-8 byte-order mark is not permitted.");
        }

        try
        {
            _ = StrictUtf8.GetCharCount(utf8);
        }
        catch (DecoderFallbackException)
        {
            throw Error(MvvmValidationErrorCodes.InvalidUtf8, "The frame is not valid UTF-8.");
        }

        ValidateUnicodeEscapes(utf8);

        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = effectiveLimits.MaxJsonDepth,
            });
            JsonElement root = document.RootElement;
            ValidateGeneralJson(root, effectiveLimits, 0);
            ValidateDocument(root, direction, effectiveLimits, out string kind);
            return new MvvmWireMessage(direction, kind, root);
        }
        catch (MvvmProtocolException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw Error(MvvmValidationErrorCodes.InvalidJson, "The frame is not one valid JSON document.");
        }
    }

    private static MvvmLimits GetLimits(MvvmLimits? limits)
    {
        MvvmLimits effectiveLimits = limits ?? MvvmLimits.Default;
        effectiveLimits.Validate();
        return effectiveLimits;
    }

    private static void ValidateDocument(
        JsonElement root,
        MvvmMessageDirection direction,
        MvvmLimits limits,
        out string kind)
    {
        RequireKind(root, JsonValueKind.Object, "$", "The message envelope must be an object.");
        RequireInteger(root, "v", 1, 1, "$.v");
        kind = RequireString(root, "kind", "$.kind");
        string[] allowedKinds = direction == MvvmMessageDirection.ClientToHost ? ClientKinds : HostKinds;
        if (!allowedKinds.Contains(kind, StringComparer.Ordinal))
        {
            throw Schema("The message kind is not defined for this direction.", "$.kind");
        }

        if (direction == MvvmMessageDirection.ClientToHost)
        {
            ValidateClient(root, kind, limits);
        }
        else
        {
            ValidateHost(root, kind, limits);
        }
    }

    private static void ValidateClient(JsonElement root, string kind, MvvmLimits limits)
    {
        switch (kind)
        {
            case "handshake":
                Closed(root, ["v", "kind", "request", "payload"]);
                Uuid(root, "request", "$.request");
                JsonElement handshake = Payload(root);
                Closed(handshake, ["supportedVersions", "capabilities"], "$.payload");
                JsonElement versions = Property(handshake, "supportedVersions", "$.payload.supportedVersions");
                RequireKind(versions, JsonValueKind.Array, "$.payload.supportedVersions", "Supported versions must be an array.");
                if (versions.GetArrayLength() != 1 || !TryInteger(versions[0], out long offered) || offered != 1)
                {
                    throw Schema("Supported versions must contain exactly protocol version 1.", "$.payload.supportedVersions");
                }

                Capabilities(Property(handshake, "capabilities", "$.payload.capabilities"), "$.payload.capabilities");
                break;
            case "open":
                Closed(root, ["v", "kind", "contract", "view", "request", "payload"]);
                Contract(root, "contract", "$.contract");
                Uuid(root, "view", "$.view");
                Uuid(root, "request", "$.request");
                EmptyPayload(root);
                break;
            case "setProperty":
                SessionEnvelope(root, hasRevision: true);
                JsonElement setter = Payload(root);
                Closed(setter, ["member", "value"], "$.payload");
                Member(setter, "member", "$.payload.member");
                _ = Property(setter, "value", "$.payload.value");
                break;
            case "execute":
                SessionEnvelope(root, hasRevision: true);
                JsonElement execute = Payload(root);
                Closed(execute, ["member"], ["argument"], "$.payload");
                Member(execute, "member", "$.payload.member");
                break;
            case "cancel":
                SessionEnvelope(root, hasRevision: false);
                JsonElement cancel = Payload(root);
                Closed(cancel, ["targetRequest"], "$.payload");
                Uuid(cancel, "targetRequest", "$.payload.targetRequest");
                break;
            case "ack":
                SessionEnvelope(root, hasRevision: false);
                JsonElement ack = Payload(root);
                Closed(ack, ["revision"], "$.payload");
                Revision(ack, "revision", "$.payload.revision");
                break;
            case "requestSnapshot":
                SessionEnvelope(root, hasRevision: false);
                EmptyPayload(root);
                break;
            case "close":
                SessionEnvelope(root, hasRevision: false);
                JsonElement close = Payload(root);
                Closed(close, [], ["reason"], "$.payload");
                if (close.TryGetProperty("reason", out JsonElement reason))
                {
                    Sanitized(reason, "$.payload.reason");
                }

                break;
        }
    }

    private static void ValidateHost(JsonElement root, string kind, MvvmLimits limits)
    {
        switch (kind)
        {
            case "handshakeResult":
                Closed(root, ["v", "kind", "request", "payload"]);
                Uuid(root, "request", "$.request");
                HandshakeResult(Payload(root));
                break;
            case "opened":
                Closed(root, ["v", "kind", "contract", "session", "view", "request", "capability", "payload"]);
                Contract(root, "contract", "$.contract");
                Uuid(root, "session", "$.session");
                Uuid(root, "view", "$.view");
                Uuid(root, "request", "$.request");
                Capability(root, "capability", "$.capability");
                JsonElement opened = Payload(root);
                Closed(opened, ["snapshot"], "$.payload");
                JsonElement initial = Property(opened, "snapshot", "$.payload.snapshot");
                ValidateSnapshot(initial, limits);
                if (Revision(initial, "revision", "$.payload.snapshot.revision") != 0)
                {
                    throw Semantic("An opened snapshot must have revision zero.", "$.payload.snapshot.revision");
                }

                break;
            case "result":
                HostSessionEnvelope(root, requestRequired: true);
                ValidateResult(Payload(root));
                break;
            case "snapshot":
                HostSessionEnvelope(root, requestRequired: true);
                ValidateSnapshot(Payload(root), limits);
                break;
            case "patch":
                HostSessionEnvelope(root, requestRequired: false);
                ValidatePatch(Payload(root), limits);
                break;
            case "fault":
                ValidateFaultEnvelope(root);
                break;
            case "closed":
                HostSessionEnvelope(root, requestRequired: true);
                JsonElement closed = Payload(root);
                Closed(closed, ["revision", "reason"], "$.payload");
                Revision(closed, "revision", "$.payload.revision");
                Sanitized(Property(closed, "reason", "$.payload.reason"), "$.payload.reason");
                break;
        }
    }

    private static void SessionEnvelope(JsonElement root, bool hasRevision)
    {
        if (hasRevision)
        {
            Closed(root, ["v", "kind", "session", "view", "request", "baseRevision", "capability", "payload"]);
            Revision(root, "baseRevision", "$.baseRevision");
        }
        else
        {
            Closed(root, ["v", "kind", "session", "view", "request", "capability", "payload"]);
        }

        Uuid(root, "session", "$.session");
        Uuid(root, "view", "$.view");
        Uuid(root, "request", "$.request");
        Capability(root, "capability", "$.capability");
    }

    private static void HostSessionEnvelope(JsonElement root, bool requestRequired)
    {
        if (requestRequired)
        {
            Closed(root, ["v", "kind", "session", "view", "request", "payload"]);
            Uuid(root, "request", "$.request");
        }
        else
        {
            Closed(root, ["v", "kind", "session", "view", "payload"]);
        }

        Uuid(root, "session", "$.session");
        Uuid(root, "view", "$.view");
    }

    private static void HandshakeResult(JsonElement payload)
    {
        Closed(payload, ["selectedVersion", "capabilities", "limits"], "$.payload");
        RequireInteger(payload, "selectedVersion", 1, 1, "$.payload.selectedVersion");
        Capabilities(Property(payload, "capabilities", "$.payload.capabilities"), "$.payload.capabilities");
        JsonElement limits = Property(payload, "limits", "$.payload.limits");
        string[] names = ["maxFrameBytes", "maxJsonDepth", "maxSessions", "maxPendingRequests", "maxSnapshotMembers", "maxPatchChanges", "maxCollectionItems", "commandTimeoutMilliseconds"];
        Closed(limits, names, "$.payload.limits");
        RequireInteger(limits, names[0], 1, MvvmLimits.MaximumPayloadBytes, "$.payload.limits.maxFrameBytes");
        RequireInteger(limits, names[1], 1, MvvmLimits.MaximumJsonDepth, "$.payload.limits.maxJsonDepth");
        RequireInteger(limits, names[2], 1, MvvmLimits.MaximumSessions, "$.payload.limits.maxSessions");
        RequireInteger(limits, names[3], 1, MvvmLimits.MaximumPendingRequests, "$.payload.limits.maxPendingRequests");
        RequireInteger(limits, names[4], 1, MvvmLimits.MaximumSnapshotMembers, "$.payload.limits.maxSnapshotMembers");
        RequireInteger(limits, names[5], 1, MvvmLimits.MaximumPatchOperations, "$.payload.limits.maxPatchChanges");
        RequireInteger(limits, names[6], 1, MvvmLimits.MaximumCollectionItems, "$.payload.limits.maxCollectionItems");
        RequireInteger(limits, names[7], 1, 300_000, "$.payload.limits.commandTimeoutMilliseconds");
    }

    private static void ValidateResult(JsonElement payload)
    {
        string operation = RequireString(payload, "operation", "$.payload.operation");
        switch (operation)
        {
            case "setProperty":
            case "ack":
                Closed(payload, ["operation", "revision"], "$.payload");
                break;
            case "execute":
                Closed(payload, ["operation", "revision"], ["value"], "$.payload");
                break;
            case "cancel":
                Closed(payload, ["operation", "revision", "targetRequest", "accepted"], "$.payload");
                Uuid(payload, "targetRequest", "$.payload.targetRequest");
                Boolean(payload, "accepted", "$.payload.accepted");
                break;
            default:
                throw Schema("The result operation is not defined by protocol version 1.", "$.payload.operation");
        }

        Revision(payload, "revision", "$.payload.revision");
    }

    private static void ValidateSnapshot(JsonElement snapshot, MvvmLimits limits)
    {
        Closed(snapshot, ["revision", "members"], "$.payload");
        Revision(snapshot, "revision", "$.payload.revision");
        JsonElement members = Property(snapshot, "members", "$.payload.members");
        RequireKind(members, JsonValueKind.Array, "$.payload.members", "Snapshot members must be an array.");
        if (members.GetArrayLength() > limits.MaxSnapshotMembers)
        {
            throw Limit("The snapshot contains too many members.", "$.payload.members");
        }

        var identities = new HashSet<(string Type, int Member)>();
        var principalKinds = new Dictionary<int, string>();
        int previousMember = 0;
        int previousTypeOrder = -1;
        foreach (JsonElement member in members.EnumerateArray())
        {
            string type = RequireString(member, "type", "$.payload.members[].type");
            int id = Member(member, "member", "$.payload.members[].member");
            int typeOrder = SnapshotTypeOrder(type);
            if (id < previousMember || (id == previousMember && typeOrder < previousTypeOrder))
            {
                throw Semantic("Snapshot members are not in canonical member and type order.", "$.payload.members");
            }

            if (id != previousMember)
            {
                previousTypeOrder = -1;
            }

            previousMember = id;
            previousTypeOrder = typeOrder;
            if (!identities.Add((type, id)))
            {
                throw Semantic("A snapshot cannot repeat a member identity.", "$.payload.members");
            }

            if (type is "property" or "collection" or "command")
            {
                if (!principalKinds.TryAdd(id, type))
                {
                    throw Semantic("A member identifier cannot have more than one principal kind.", "$.payload.members");
                }
            }
            else if (type == "validation" &&
                (!principalKinds.TryGetValue(id, out string? principal) || principal == "command"))
            {
                throw Semantic("A validation projection requires a property or collection principal.", "$.payload.members");
            }

            ValidateProjectedMember(member, type, limits, patch: false);
        }
    }

    private static void ValidatePatch(JsonElement patch, MvvmLimits limits)
    {
        Closed(patch, ["fromRevision", "toRevision", "changes"], "$.payload");
        long from = Revision(patch, "fromRevision", "$.payload.fromRevision");
        long to = Revision(patch, "toRevision", "$.payload.toRevision");
        if (from == long.MaxValue || to != from + 1)
        {
            throw Semantic("A patch must advance exactly one revision.", "$.payload.toRevision");
        }

        JsonElement changes = Property(patch, "changes", "$.payload.changes");
        RequireKind(changes, JsonValueKind.Array, "$.payload.changes", "Patch changes must be an array.");
        if (changes.GetArrayLength() == 0 || changes.GetArrayLength() > limits.MaxPatchOperations)
        {
            throw Limit("A patch must contain a bounded non-empty change set.", "$.payload.changes");
        }

        int insertedOrReplaced = 0;
        foreach (JsonElement change in changes.EnumerateArray())
        {
            string type = RequireString(change, "type", "$.payload.changes[].type");
            ValidateProjectedMember(change, type, limits, patch: true);
            if (type == "collection")
            {
                string operation = RequireString(change, "operation", "$.payload.changes[].operation");
                if (operation is "insert" or "replace")
                {
                    insertedOrReplaced = checked(insertedOrReplaced + change.GetProperty("items").GetArrayLength());
                    if (insertedOrReplaced > limits.MaxCollectionItems)
                    {
                        throw Limit("A patch inserts or replaces too many collection items.", "$.payload.changes");
                    }
                }
            }
        }
    }

    private static void ValidateProjectedMember(JsonElement value, string type, MvvmLimits limits, bool patch)
    {
        Member(value, "member", patch ? "$.payload.changes[].member" : "$.payload.members[].member");
        switch (type)
        {
            case "property":
                Closed(value, ["type", "member", "value"]);
                _ = Property(value, "value", "$.value");
                break;
            case "collection" when !patch:
                Closed(value, ["type", "member", "items"]);
                CollectionItems(Property(value, "items", "$.items"), limits, "$.items");
                break;
            case "collection":
                Closed(value, ["type", "member", "operation", "index", "items"]);
                string operation = RequireString(value, "operation", "$.operation");
                if (operation is not ("insert" or "remove" or "replace" or "reset"))
                {
                    throw Schema("The collection operation is not defined by protocol version 1.", "$.operation");
                }

                long index = RequireInteger(value, "index", 0, 9_999, "$.index");
                if (operation == "reset" && index != 0)
                {
                    throw Semantic("A collection reset must use index zero.", "$.index");
                }

                CollectionItems(Property(value, "items", "$.items"), limits, "$.items");
                if (operation != "reset" && value.GetProperty("items").GetArrayLength() == 0)
                {
                    throw Semantic("Insert, remove, and replace operations require non-empty items.", "$.items");
                }

                break;
            case "collectionMove" when patch:
                Closed(value, ["type", "member", "from", "to", "count"]);
                RequireInteger(value, "from", 0, 9_999, "$.from");
                RequireInteger(value, "to", 0, 9_999, "$.to");
                RequireInteger(value, "count", 1, limits.MaxCollectionItems, "$.count");
                break;
            case "command":
                Closed(value, ["type", "member", "canExecute", "isExecuting"]);
                Boolean(value, "canExecute", "$.canExecute");
                Boolean(value, "isExecuting", "$.isExecuting");
                break;
            case "validation":
                Closed(value, ["type", "member", "errors"]);
                JsonElement errors = Property(value, "errors", "$.errors");
                RequireKind(errors, JsonValueKind.Array, "$.errors", "Validation errors must be an array.");
                if (errors.GetArrayLength() > 32)
                {
                    throw Limit("A validation member contains too many errors.", "$.errors");
                }

                foreach (JsonElement error in errors.EnumerateArray())
                {
                    Sanitized(error, "$.errors[]");
                }

                break;
            default:
                throw Schema("The projected member type is not defined by protocol version 1.", "$.type");
        }
    }

    private static void ValidateFaultEnvelope(JsonElement root)
    {
        bool hasSession = root.TryGetProperty("session", out _);
        bool hasView = root.TryGetProperty("view", out _);
        if (hasSession != hasView)
        {
            throw Schema("A fault must include both session identities or neither.");
        }

        if (hasSession)
        {
            Closed(root, ["v", "kind", "session", "view", "request", "payload"]);
            Uuid(root, "session", "$.session");
            Uuid(root, "view", "$.view");
        }
        else
        {
            Closed(root, ["v", "kind", "request", "payload"]);
        }

        Uuid(root, "request", "$.request");
        JsonElement payload = Payload(root);
        Closed(payload, ["code", "message", "retryable"], ["currentRevision", "snapshotRequired"], "$.payload");
        string code = RequireString(payload, "code", "$.payload.code");
        if (!MvvmFaultCodes.IsDefined(code))
        {
            throw Schema("The fault code is not defined by protocol version 1.", "$.payload.code");
        }

        Sanitized(Property(payload, "message", "$.payload.message"), "$.payload.message");
        bool retryable = Boolean(payload, "retryable", "$.payload.retryable");
        bool expectedRetryable = code is MvvmFaultCodes.RevisionStale or MvvmFaultCodes.LimitExceeded or MvvmFaultCodes.RequestTimeout;
        if (retryable != expectedRetryable)
        {
            throw Semantic("The fault retry flag is inconsistent with its code.", "$.payload.retryable");
        }

        bool hasRevision = payload.TryGetProperty("currentRevision", out _);
        bool hasSnapshot = payload.TryGetProperty("snapshotRequired", out JsonElement snapshotRequired);
        if (code == MvvmFaultCodes.RevisionStale)
        {
            if (!hasRevision || !hasSnapshot || !Boolean(snapshotRequired, "$.payload.snapshotRequired"))
            {
                throw Semantic("A stale-revision fault requires its recovery fields.", "$.payload");
            }

            Revision(payload, "currentRevision", "$.payload.currentRevision");
        }
        else if (hasRevision || hasSnapshot)
        {
            throw Semantic("Recovery fields are only valid for a stale-revision fault.", "$.payload");
        }

        if (code == MvvmFaultCodes.ProtocolUnsupported && hasSession)
        {
            throw Semantic("An unsupported-protocol fault is only valid before a session is opened.");
        }

        if (!hasSession && code is not (MvvmFaultCodes.ProtocolUnsupported or MvvmFaultCodes.RequestInvalid or MvvmFaultCodes.LimitExceeded))
        {
            throw Semantic("This fault code is not valid before a session is opened.", "$.payload.code");
        }
    }

    private static void ValidateGeneralJson(JsonElement value, MvvmLimits limits, int depth)
    {
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && depth >= limits.MaxJsonDepth)
        {
            throw Limit("The JSON value exceeds the configured nesting depth.");
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                int propertyCount = 0;
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    propertyCount++;
                    if (propertyCount > limits.MaxObjectProperties)
                    {
                        throw Limit("A JSON object contains too many properties.");
                    }

                    if (!names.Add(property.Name))
                    {
                        throw Error(MvvmValidationErrorCodes.DuplicateProperty, "A JSON object contains a duplicate property name.");
                    }

                    int nameBytes = StrictUtf8.GetByteCount(property.Name);
                    if (nameBytes == 0 || nameBytes > limits.MaxPropertyNameBytes)
                    {
                        throw Limit("A JSON property name is empty or exceeds the byte limit.");
                    }

                    ValidateGeneralJson(property.Value, limits, depth + 1);
                }

                break;
            case JsonValueKind.Array:
                if (value.GetArrayLength() > limits.MaxArrayItems)
                {
                    throw Limit("A JSON array contains too many items.");
                }

                foreach (JsonElement item in value.EnumerateArray())
                {
                    ValidateGeneralJson(item, limits, depth + 1);
                }

                break;
            case JsonValueKind.String:
                string text = value.GetString()!;
                if (StrictUtf8.GetByteCount(text) > limits.MaxStringBytes)
                {
                    throw Limit("A JSON string exceeds the configured byte limit.");
                }

                break;
            case JsonValueKind.Undefined:
                throw Error(MvvmValidationErrorCodes.InvalidJson, "The frame contains an undefined JSON value.");
        }
    }

    private static void ValidateUnicodeEscapes(ReadOnlySpan<byte> utf8)
    {
        bool inString = false;
        for (int index = 0; index < utf8.Length; index++)
        {
            byte current = utf8[index];
            if (!inString)
            {
                if (current == (byte)'"')
                {
                    inString = true;
                }

                continue;
            }

            if (current == (byte)'"')
            {
                inString = false;
                continue;
            }

            if (current != (byte)'\\')
            {
                continue;
            }

            if (++index >= utf8.Length)
            {
                return;
            }

            if (utf8[index] != (byte)'u')
            {
                continue;
            }

            if (!TryHex16(utf8, index + 1, out int scalar))
            {
                return;
            }

            index += 4;
            if (scalar is >= 0xdc00 and <= 0xdfff)
            {
                throw Error(MvvmValidationErrorCodes.InvalidJson, "A JSON string contains an unpaired Unicode surrogate.");
            }

            if (scalar is >= 0xd800 and <= 0xdbff)
            {
                if (index + 6 >= utf8.Length || utf8[index + 1] != (byte)'\\' || utf8[index + 2] != (byte)'u' ||
                    !TryHex16(utf8, index + 3, out int low) || low is < 0xdc00 or > 0xdfff)
                {
                    throw Error(MvvmValidationErrorCodes.InvalidJson, "A JSON string contains an unpaired Unicode surrogate.");
                }

                index += 6;
            }
        }
    }

    private static bool TryHex16(ReadOnlySpan<byte> utf8, int start, out int value)
    {
        value = 0;
        if (start + 4 > utf8.Length)
        {
            return false;
        }

        for (int offset = 0; offset < 4; offset++)
        {
            int digit = utf8[start + offset] switch
            {
                >= (byte)'0' and <= (byte)'9' => utf8[start + offset] - '0',
                >= (byte)'a' and <= (byte)'f' => utf8[start + offset] - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => utf8[start + offset] - 'A' + 10,
                _ => -1,
            };
            if (digit < 0)
            {
                return false;
            }

            value = (value << 4) | digit;
        }

        return true;
    }

    private static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement value,
        string? propertyName,
        bool applicationValue)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                IEnumerable<JsonProperty> properties = applicationValue
                    ? value.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal)
                    : value.EnumerateObject()
                        .OrderBy(property => PropertyRank(property.Name, propertyName))
                        .ThenBy(static property => property.Name, StringComparer.Ordinal);
                foreach (JsonProperty property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    bool childApplication = applicationValue || property.Name is "value" or "argument" or "items";
                    WriteCanonical(writer, property.Value, property.Name, childApplication);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                IEnumerable<JsonElement> items = value.EnumerateArray();
                if (propertyName == "capabilities")
                {
                    items = items.OrderBy(static item => item.GetString(), StringComparer.Ordinal);
                }

                foreach (JsonElement item in items)
                {
                    WriteCanonical(writer, item, propertyName, applicationValue);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                WriteCanonicalNumber(writer, value.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw Error(MvvmValidationErrorCodes.InvalidJson, "The message contains an undefined JSON value.");
        }
    }

    private static void WriteCanonicalNumber(Utf8JsonWriter writer, string raw)
    {
        writer.WriteRawValue(CanonicalNumber(raw), skipInputValidation: true);
    }

    private static string CanonicalNumber(string raw)
    {
        int cursor = 0;
        bool negative = raw[0] == '-';
        if (negative)
        {
            cursor++;
        }

        int exponentIndex = raw.IndexOfAny(['e', 'E'], cursor);
        int mantissaEnd = exponentIndex < 0 ? raw.Length : exponentIndex;
        BigInteger exponent = BigInteger.Zero;
        if (exponentIndex >= 0)
        {
            ReadOnlySpan<char> exponentText = raw.AsSpan(exponentIndex + 1);
            bool negativeExponent = exponentText.Length > 0 && exponentText[0] == '-';
            int exponentDigitsStart = exponentText.Length > 0 && exponentText[0] is '+' or '-' ? 1 : 0;
            while (exponentDigitsStart < exponentText.Length && exponentText[exponentDigitsStart] == '0')
            {
                exponentDigitsStart++;
            }

            // A very long exponent is cheap to place on the wire but disproportionately expensive
            // for arbitrary-precision parsing. V1 frames cannot materialize a corresponding positive
            // integral value, so cap significant exponent digits before invoking BigInteger.
            if (exponentText.Length - exponentDigitsStart > 9)
            {
                throw Limit("The JSON number exponent exceeds the bounded canonicalization range.");
            }

            if (exponentDigitsStart < exponentText.Length)
            {
                _ = int.TryParse(
                    exponentText[exponentDigitsStart..],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int exponentMagnitude);
                exponent = negativeExponent ? -exponentMagnitude : exponentMagnitude;
            }
        }

        int decimalPoint = raw.IndexOf('.', cursor, mantissaEnd - cursor);
        int fractionalDigits = decimalPoint < 0 ? 0 : mantissaEnd - decimalPoint - 1;
        var digitsBuilder = new StringBuilder(mantissaEnd - cursor);
        for (int index = cursor; index < mantissaEnd; index++)
        {
            if (raw[index] != '.')
            {
                digitsBuilder.Append(raw[index]);
            }
        }

        string untrimmed = digitsBuilder.ToString();
        int firstNonzero = 0;
        while (firstNonzero < untrimmed.Length && untrimmed[firstNonzero] == '0')
        {
            firstNonzero++;
        }

        if (firstNonzero == untrimmed.Length)
        {
            return "0";
        }

        int lastNonzero = untrimmed.Length - 1;
        while (lastNonzero > firstNonzero && untrimmed[lastNonzero] == '0')
        {
            lastNonzero--;
        }

        int removedTrailingZeros = untrimmed.Length - lastNonzero - 1;
        string digits = untrimmed[firstNonzero..(lastNonzero + 1)];
        exponent += removedTrailingZeros - fractionalDigits;

        BigInteger decimalPosition = exponent + digits.Length;
        BigInteger scientificExponent = decimalPosition - 1;
        string scientific = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
        if (!scientificExponent.IsZero)
        {
            scientific += "e" + scientificExponent.ToString(CultureInfo.InvariantCulture);
        }

        BigInteger plainLength = decimalPosition.Sign switch
        {
            <= 0 => 2 - decimalPosition + digits.Length,
            _ when decimalPosition >= digits.Length => decimalPosition,
            _ => digits.Length + 1,
        };

        bool integerValue = decimalPosition >= digits.Length;
        if (integerValue && plainLength > MvvmLimits.MaximumPayloadBytes)
        {
            throw Error(MvvmValidationErrorCodes.FrameLimitExceeded, "The canonical number cannot fit in a protocol frame.");
        }

        string canonical = scientific;
        if ((integerValue || plainLength <= scientific.Length) && plainLength <= int.MaxValue)
        {
            int point = (int)decimalPosition;
            if (point <= 0)
            {
                canonical = "0." + new string('0', -point) + digits;
            }
            else if (point >= digits.Length)
            {
                canonical = digits + new string('0', point - digits.Length);
            }
            else
            {
                canonical = digits[..point] + "." + digits[point..];
            }
        }

        return negative ? "-" + canonical : canonical;
    }

    private static int PropertyRank(string name, string? context)
    {
        if (context is "members" or "changes")
        {
            return name switch
            {
                "type" => 0,
                "member" => 1,
                "value" => 2,
                "operation" => 2,
                "index" => 3,
                "items" => 4,
                "from" => 2,
                "to" => 3,
                "count" => 4,
                "canExecute" => 2,
                "isExecuting" => 3,
                "errors" => 2,
                _ => 100,
            };
        }

        return name switch
    {
        "v" => 0,
        "kind" => 1,
        "contract" => 2,
        "session" => 3,
        "view" => 4,
        "request" => 5,
        "baseRevision" => 6,
        "capability" => 7,
        "payload" => 8,
        "selectedVersion" => 10,
        "supportedVersions" => 11,
        "capabilities" => 12,
        "limits" => 13,
        "maxFrameBytes" => 20,
        "maxJsonDepth" => 21,
        "maxSessions" => 22,
        "maxPendingRequests" => 23,
        "maxSnapshotMembers" => 24,
        "maxPatchChanges" => 25,
        "maxCollectionItems" => 26,
        "commandTimeoutMilliseconds" => 27,
        "snapshot" => 30,
        "operation" => 31,
        "revision" => 32,
        "targetRequest" => 33,
        "accepted" => 34,
        "fromRevision" => 35,
        "toRevision" => 36,
        "changes" => 37,
        "code" => 38,
        "message" => 39,
        "retryable" => 40,
        "currentRevision" => 41,
        "snapshotRequired" => 42,
        "reason" => 43,
        "members" => 44,
        "type" => 50,
        "member" => 51,
        "value" => 52,
        "argument" => 53,
        "items" => 54,
        "from" => 55,
        "to" => 56,
        "count" => 57,
        "canExecute" => 58,
        "isExecuting" => 59,
        "errors" => 60,
        _ => 100,
    };
    }

    private static JsonElement Payload(JsonElement root)
    {
        JsonElement payload = Property(root, "payload", "$.payload");
        RequireKind(payload, JsonValueKind.Object, "$.payload", "The message payload must be an object.");
        return payload;
    }

    private static void EmptyPayload(JsonElement root) => Closed(Payload(root), [], "$.payload");

    private static void Closed(JsonElement value, string[] required, string path = "$") =>
        Closed(value, required, [], path);

    private static void Closed(JsonElement value, string[] required, string[] optional, string path = "$")
    {
        RequireKind(value, JsonValueKind.Object, path, "The protocol value must be an object.");
        foreach (string name in required)
        {
            if (!value.TryGetProperty(name, out _))
            {
                throw Schema("A required protocol property is missing.", path);
            }
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!required.Contains(property.Name, StringComparer.Ordinal) &&
                !optional.Contains(property.Name, StringComparer.Ordinal))
            {
                throw Schema("The closed protocol object contains an unknown property.", path);
            }
        }
    }

    private static JsonElement Property(JsonElement value, string name, string path)
    {
        if (!value.TryGetProperty(name, out JsonElement property))
        {
            throw Schema("A required protocol property is missing.", path);
        }

        return property;
    }

    private static string RequireString(JsonElement value, string name, string path) =>
        RequireString(Property(value, name, path), path);

    private static string RequireString(JsonElement value, string path)
    {
        RequireKind(value, JsonValueKind.String, path, "The protocol property must be a string.");
        return value.GetString()!;
    }

    private static bool Boolean(JsonElement value, string name, string path) => Boolean(Property(value, name, path), path);

    private static bool Boolean(JsonElement value, string path)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Schema("The protocol property must be a Boolean.", path);
        }

        return value.GetBoolean();
    }

    private static long Revision(JsonElement value, string name, string path) =>
        RequireInteger(value, name, 0, long.MaxValue, path);

    private static int Member(JsonElement value, string name, string path) =>
        checked((int)RequireInteger(value, name, 1, int.MaxValue, path));

    private static long RequireInteger(JsonElement value, string name, long minimum, long maximum, string path) =>
        RequireInteger(Property(value, name, path), minimum, maximum, path);

    private static long RequireInteger(JsonElement value, long minimum, long maximum, string path)
    {
        if (!TryInteger(value, out long number) || number < minimum || number > maximum)
        {
            throw Schema("The protocol property is outside its integer range.", path);
        }

        return number;
    }

    private static bool TryInteger(JsonElement value, out long number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number);
    }

    private static void Contract(JsonElement value, string name, string path)
    {
        string contract = RequireString(value, name, path);
        if (contract.Length == 0 || StrictUtf8.GetByteCount(contract) > 128 || contract.Any(char.IsControl))
        {
            throw Schema("The contract identifier is invalid.", path);
        }
    }

    private static void Uuid(JsonElement value, string name, string path)
    {
        string text = RequireString(value, name, path);
        bool validShape = text.Length == 36 && text[8] == '-' && text[13] == '-' && text[18] == '-' && text[23] == '-' &&
            text.All(static character => character == '-' || character is >= '0' and <= '9' || character is >= 'a' and <= 'f') &&
            text[14] is >= '1' and <= '8' && text[19] is '8' or '9' or 'a' or 'b';
        if (!validShape || !Guid.TryParseExact(text, "D", out Guid parsed) || parsed == Guid.Empty ||
            !string.Equals(parsed.ToString("D"), text, StringComparison.Ordinal))
        {
            throw Schema("The protocol UUID is not canonical lowercase RFC 4122 text.", path);
        }
    }

    private static void Capability(JsonElement value, string name, string path)
    {
        string token = RequireString(value, name, path);
        if (token.Length != 43 || token.Any(static character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')))
        {
            throw Schema("The capability token is not canonical base64url.", path);
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=");
            if (bytes.Length != 32)
            {
                throw Schema("The capability token does not encode exactly 32 bytes.", path);
            }
        }
        catch (FormatException)
        {
            throw Schema("The capability token is not canonical base64url.", path);
        }
    }

    private static void Capabilities(JsonElement value, string path)
    {
        RequireKind(value, JsonValueKind.Array, path, "Capabilities must be an array.");
        if (value.GetArrayLength() > CapabilityNames.Length)
        {
            throw Schema("The capability set is too large.", path);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        foreach (JsonElement item in value.EnumerateArray())
        {
            string name = RequireString(item, path + "[]");
            if (!CapabilityNames.Contains(name, StringComparer.Ordinal) || !seen.Add(name))
            {
                throw Schema("The capability set contains an unknown or duplicate name.", path);
            }

            if (previous is not null && string.CompareOrdinal(previous, name) >= 0)
            {
                throw Schema("Capability names must be in strict ordinal order.", path);
            }

            previous = name;
        }
    }

    private static void Sanitized(JsonElement value, string path)
    {
        string text = RequireString(value, path);
        if (text.Length == 0 || StrictUtf8.GetByteCount(text) > 256 || text.Any(char.IsControl))
        {
            throw Schema("The diagnostic message is not a bounded control-free string.", path);
        }
    }

    private static void CollectionItems(JsonElement items, MvvmLimits limits, string path)
    {
        RequireKind(items, JsonValueKind.Array, path, "Collection items must be an array.");
        if (items.GetArrayLength() > limits.MaxCollectionItems)
        {
            throw Limit("A projected collection exceeds the configured item limit.", path);
        }
    }

    private static void RequireKind(JsonElement value, JsonValueKind kind, string path, string message)
    {
        if (value.ValueKind != kind)
        {
            throw Schema(message, path);
        }
    }

    private static int SnapshotTypeOrder(string type) => type switch
    {
        "property" => 0,
        "collection" => 1,
        "command" => 2,
        "validation" => 3,
        _ => 4,
    };

    private static MvvmProtocolException Error(string code, string message, string path = "$") => new(code, message, path);

    private static MvvmProtocolException Schema(string message, string path = "$") =>
        Error(MvvmValidationErrorCodes.SchemaInvalid, message, path);

    private static MvvmProtocolException Semantic(string message, string path = "$") =>
        Error(MvvmValidationErrorCodes.SemanticInvalid, message, path);

    private static MvvmProtocolException Limit(string message, string path = "$") =>
        Error(MvvmValidationErrorCodes.JsonLimitExceeded, message, path);
}
