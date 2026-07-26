using System.Text;
using System.Text.Json;

namespace WebUIToolkit.MVVM;

/// <summary>The kinds of state-changing requests accepted by a generated binding adapter.</summary>
public enum MvvmMutationKind
{
    /// <summary>Assign a generated property.</summary>
    SetProperty,

    /// <summary>Execute a generated command.</summary>
    ExecuteCommand,
}

/// <summary>The base class for a closed runtime request set.</summary>
public abstract record MvvmRequest
{
    internal MvvmRequest(MvvmRequestId requestId)
    {
        if (requestId.Value == Guid.Empty)
        {
            throw new ArgumentException("A request identifier cannot be empty.", nameof(requestId));
        }

        RequestId = requestId;
    }

    /// <summary>Gets the request correlation identifier.</summary>
    public MvvmRequestId RequestId { get; }
}

/// <summary>A mutation against one generated member.</summary>
public sealed record MvvmMutationRequest : MvvmRequest
{
    /// <summary>Creates a mutation request.</summary>
    public MvvmMutationRequest(
        MvvmRequestId requestId,
        MvvmMutationKind kind,
        long baseRevision,
        int memberId,
        JsonElement payload)
        : base(requestId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memberId);
        if (kind is not MvvmMutationKind.SetProperty and not MvvmMutationKind.ExecuteCommand)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "The mutation kind is not defined by protocol version 1.");
        }

        if (payload.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("A mutation payload must be valid JSON.", nameof(payload));
        }

        Kind = kind;
        BaseRevision = baseRevision;
        MemberId = memberId;
        Payload = payload.Clone();
    }

    /// <summary>Gets the requested mutation.</summary>
    public MvvmMutationKind Kind { get; }

    /// <summary>Gets the authoritative revision observed by the caller.</summary>
    public long BaseRevision { get; }

    /// <summary>Gets the stable generated numeric member identifier.</summary>
    public int MemberId { get; }

    /// <summary>Gets the typed JSON value or command parameter.</summary>
    public JsonElement Payload { get; }
}

/// <summary>Requests an authoritative state snapshot.</summary>
public sealed record MvvmSnapshotRequest : MvvmRequest
{
    /// <summary>Creates a snapshot request.</summary>
    public MvvmSnapshotRequest(MvvmRequestId requestId)
        : base(requestId)
    {
    }
}

/// <summary>Session-internal request that commits adapter-originated notifications.</summary>
internal sealed record MvvmExternalChangeRequest : MvvmRequest
{
    internal MvvmExternalChangeRequest(MvvmRequestId requestId)
        : base(requestId)
    {
    }
}

/// <summary>Acknowledges that a client has applied an authoritative revision.</summary>
public sealed record MvvmAcknowledgeRequest : MvvmRequest
{
    /// <summary>Creates an acknowledgement request.</summary>
    public MvvmAcknowledgeRequest(MvvmRequestId requestId, long revision)
        : base(requestId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        Revision = revision;
    }

    /// <summary>Gets the applied revision.</summary>
    public long Revision { get; }
}

/// <summary>Requests cancellation of an in-flight operation.</summary>
public sealed record MvvmCancelRequest : MvvmRequest
{
    /// <summary>Creates a cancellation request.</summary>
    public MvvmCancelRequest(MvvmRequestId requestId, MvvmRequestId targetRequestId)
        : base(requestId)
    {
        if (targetRequestId.Value == Guid.Empty)
        {
            throw new ArgumentException("A target request identifier cannot be empty.", nameof(targetRequestId));
        }

        TargetRequestId = targetRequestId;
    }

    /// <summary>Gets the request to cancel.</summary>
    public MvvmRequestId TargetRequestId { get; }
}

/// <summary>The closed set of projected patch kinds in protocol version 1.</summary>
public enum MvvmPatchKind
{
    /// <summary>A property value changed.</summary>
    Property,

    /// <summary>A collection range changed.</summary>
    Collection,

    /// <summary>A collection range moved.</summary>
    CollectionMove,

    /// <summary>Command availability or execution state changed.</summary>
    Command,

    /// <summary>Member validation errors changed.</summary>
    Validation,
}

/// <summary>A projected state change from the closed protocol version 1 union.</summary>
public abstract record MvvmPatch
{
    internal MvvmPatch(MvvmPatchKind kind, int memberId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memberId);
        Kind = kind;
        MemberId = memberId;
    }

    /// <summary>Gets the closed patch discriminator.</summary>
    public MvvmPatchKind Kind { get; }

    /// <summary>Gets the stable generated member identifier.</summary>
    public int MemberId { get; }
}

/// <summary>A property replacement.</summary>
public sealed record MvvmPropertyPatch : MvvmPatch
{
    /// <summary>Creates a property replacement.</summary>
    public MvvmPropertyPatch(int memberId, JsonElement value)
        : base(MvvmPatchKind.Property, memberId)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("A property patch value must be valid JSON.", nameof(value));
        }

        Value = value.Clone();
    }

    /// <summary>Gets the new property value.</summary>
    public JsonElement Value { get; }
}

/// <summary>The closed collection range operations in protocol version 1.</summary>
public enum MvvmCollectionOperation
{
    /// <summary>Insert items at an index.</summary>
    Insert,

    /// <summary>Remove items at an index.</summary>
    Remove,

    /// <summary>Replace items at an index.</summary>
    Replace,

    /// <summary>Reset the collection to the supplied complete item set.</summary>
    Reset,
}

/// <summary>An indexed collection range change.</summary>
public sealed record MvvmCollectionPatch : MvvmPatch
{
    /// <summary>Creates a collection range change.</summary>
    public MvvmCollectionPatch(
        int memberId,
        MvvmCollectionOperation operation,
        int index,
        IReadOnlyList<JsonElement> items)
        : base(MvvmPatchKind.Collection, memberId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= MvvmLimits.MaximumCollectionItems)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "A collection index cannot exceed 9,999 in protocol version 1.");
        }

        ArgumentNullException.ThrowIfNull(items);
        if (operation is < MvvmCollectionOperation.Insert or > MvvmCollectionOperation.Reset)
        {
            throw new ArgumentOutOfRangeException(nameof(operation), "The collection operation is not defined by protocol version 1.");
        }

        if (items.Count > MvvmLimits.MaximumCollectionItems)
        {
            throw new ArgumentException("A collection patch exceeds the protocol item ceiling.", nameof(items));
        }

        if (operation == MvvmCollectionOperation.Reset && index != 0)
        {
            throw new ArgumentException("A collection reset must use index zero.", nameof(index));
        }

        if (operation != MvvmCollectionOperation.Reset && items.Count == 0)
        {
            throw new ArgumentException("Insert, remove, and replace operations require at least one item.", nameof(items));
        }

        Operation = operation;
        Index = index;
        if (items.Any(static item => item.ValueKind == JsonValueKind.Undefined))
        {
            throw new ArgumentException("Collection patch items must be valid JSON.", nameof(items));
        }

        Items = Array.AsReadOnly(items.Select(static item => item.Clone()).ToArray());
    }

    /// <summary>Gets the closed range operation.</summary>
    public MvvmCollectionOperation Operation { get; }

    /// <summary>Gets the zero-based operation index.</summary>
    public int Index { get; }

    /// <summary>Gets the inserted, removed, replacement, or reset item values.</summary>
    public IReadOnlyList<JsonElement> Items { get; }
}

/// <summary>A contiguous collection range move.</summary>
public sealed record MvvmCollectionMovePatch : MvvmPatch
{
    /// <summary>Creates a collection move.</summary>
    public MvvmCollectionMovePatch(int memberId, int from, int to, int count)
        : base(MvvmPatchKind.CollectionMove, memberId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfNegative(to);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (from >= MvvmLimits.MaximumCollectionItems || to >= MvvmLimits.MaximumCollectionItems)
        {
            throw new ArgumentOutOfRangeException(nameof(from), "Collection move indices cannot exceed 9,999 in protocol version 1.");
        }

        if (count > MvvmLimits.MaximumCollectionItems)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "A collection move exceeds the protocol item ceiling.");
        }

        From = from;
        To = to;
        Count = count;
    }

    /// <summary>Gets the pre-move starting index.</summary>
    public int From { get; }

    /// <summary>Gets the post-removal insertion index.</summary>
    public int To { get; }

    /// <summary>Gets the positive number of moved items.</summary>
    public int Count { get; }
}

/// <summary>A command state replacement.</summary>
public sealed record MvvmCommandPatch : MvvmPatch
{
    /// <summary>Creates a command state change.</summary>
    public MvvmCommandPatch(int memberId, bool canExecute, bool isExecuting)
        : base(MvvmPatchKind.Command, memberId)
    {
        CanExecute = canExecute;
        IsExecuting = isExecuting;
    }

    /// <summary>Gets whether the command is currently available.</summary>
    public bool CanExecute { get; }

    /// <summary>Gets whether the command is currently executing.</summary>
    public bool IsExecuting { get; }
}

/// <summary>A validation error-set replacement.</summary>
public sealed record MvvmValidationPatch : MvvmPatch
{
    /// <summary>Creates a validation state change.</summary>
    public MvvmValidationPatch(int memberId, IReadOnlyList<string> errors)
        : base(MvvmPatchKind.Validation, memberId)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count > 32)
        {
            throw new ArgumentException("A validation patch cannot contain more than 32 errors.", nameof(errors));
        }

        Errors = Array.AsReadOnly(errors.Select(MvvmFault.SanitizeProtocolMessage).ToArray());
    }

    /// <summary>Gets the safe, bounded validation errors.</summary>
    public IReadOnlyList<string> Errors { get; }
}

/// <summary>A sanitized protocol fault.</summary>
public sealed record MvvmFault
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>Creates a bounded fault safe to cross the local protocol boundary.</summary>
    public MvvmFault(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = MvvmFaultCodes.IsDefined(code)
            ? code
            : throw new ArgumentException("The fault code is not defined by protocol version 1.", nameof(code));
        Message = SanitizeProtocolMessage(message);
    }

    /// <summary>Gets the stable machine-readable code.</summary>
    public string Code { get; }

    /// <summary>Gets the bounded, single-line safe message.</summary>
    public string Message { get; }

    internal static string SanitizeProtocolMessage(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        char[] characters = message.ToCharArray();
        for (int index = 0; index < characters.Length; index++)
        {
            if (char.IsControl(characters[index]))
            {
                characters[index] = ' ';
            }
        }

        string sanitized = new string(characters).Trim();
        if (sanitized.Length == 0)
        {
            throw new ArgumentException("A fault message cannot become empty after sanitization.", nameof(message));
        }

        int encodedByteCount;
        try
        {
            encodedByteCount = StrictUtf8.GetByteCount(sanitized);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("A protocol message must contain valid Unicode.", nameof(message), exception);
        }

        if (encodedByteCount <= 256)
        {
            return sanitized;
        }

        var builder = new StringBuilder(sanitized.Length);
        int byteCount = 0;
        foreach (Rune rune in sanitized.EnumerateRunes())
        {
            if (byteCount + rune.Utf8SequenceLength > 256)
            {
                break;
            }

            builder.Append(rune);
            byteCount += rune.Utf8SequenceLength;
        }

        return builder.ToString();
    }
}

/// <summary>The result returned by a generated binding adapter.</summary>
public sealed record MvvmBindingResult
{
    private MvvmBindingResult(
        bool succeeded,
        bool committed,
        JsonElement? payload,
        IReadOnlyList<MvvmPatch> patches,
        MvvmFault? fault)
    {
        Succeeded = succeeded;
        Committed = committed;
        Payload = payload?.Clone();
        Patches = Array.AsReadOnly(patches.ToArray());
        Fault = fault;
    }

    /// <summary>Gets whether the adapter accepted and committed the mutation.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets whether consumer state committed and therefore advances the revision.</summary>
    public bool Committed { get; }

    /// <summary>Gets an optional typed command result.</summary>
    public JsonElement? Payload { get; }

    /// <summary>Gets ordered state changes caused by the mutation.</summary>
    public IReadOnlyList<MvvmPatch> Patches { get; }

    /// <summary>Gets the safe rejection fault.</summary>
    public MvvmFault? Fault { get; }

    /// <summary>Creates a successful, committed mutation result.</summary>
    public static MvvmBindingResult Success(JsonElement? payload = null, IReadOnlyList<MvvmPatch>? patches = null) =>
        new(true, true, payload, patches ?? [], null);

    /// <summary>Creates a rejected mutation result. Rejections never advance the session revision.</summary>
    public static MvvmBindingResult Rejected(MvvmFault fault) =>
        new(false, false, null, [], fault ?? throw new ArgumentNullException(nameof(fault)));

    /// <summary>Creates a failed terminal result whose observable state changes still committed.</summary>
    public static MvvmBindingResult CommittedFailure(
        MvvmFault fault,
        IReadOnlyList<MvvmPatch> patches) =>
        new(false, true, null, patches ?? throw new ArgumentNullException(nameof(patches)), fault ?? throw new ArgumentNullException(nameof(fault)));
}

/// <summary>An authoritative adapter snapshot at a session-owned revision.</summary>
public sealed record MvvmSnapshot(JsonElement State)
{
    /// <summary>Gets a detached snapshot suitable for transport.</summary>
    public JsonElement State { get; } = State.Clone();
}

/// <summary>A response emitted by a session after applying runtime semantics.</summary>
public sealed record MvvmResponse
{
    private MvvmResponse(
        MvvmRequestId requestId,
        long revision,
        bool succeeded,
        JsonElement? payload,
        IReadOnlyList<MvvmPatch> patches,
        MvvmFault? fault,
        bool? cancellationAccepted)
    {
        RequestId = requestId;
        Revision = revision;
        Succeeded = succeeded;
        Payload = payload?.Clone();
        Patches = Array.AsReadOnly(patches.ToArray());
        Fault = fault;
        CancellationAccepted = cancellationAccepted;
    }

    /// <summary>Gets the correlation identifier.</summary>
    public MvvmRequestId RequestId { get; }

    /// <summary>Gets the authoritative revision after processing.</summary>
    public long Revision { get; }

    /// <summary>Gets whether the request succeeded.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets an optional result or snapshot payload.</summary>
    public JsonElement? Payload { get; }

    /// <summary>Gets ordered committed patches, including changes preceding a winning fault.</summary>
    public IReadOnlyList<MvvmPatch> Patches { get; }

    /// <summary>Gets the stable safe fault for a rejected request.</summary>
    public MvvmFault? Fault { get; }

    /// <summary>Gets whether a cancellation signal won its target request, for cancel results only.</summary>
    public bool? CancellationAccepted { get; }

    internal static MvvmResponse Success(
        MvvmRequestId requestId,
        long revision,
        JsonElement? payload = null,
        IReadOnlyList<MvvmPatch>? patches = null,
        bool? cancellationAccepted = null) =>
        new(requestId, revision, true, payload, patches ?? [], null, cancellationAccepted);

    internal static MvvmResponse Rejected(MvvmRequestId requestId, long revision, string code, string message) =>
        new(requestId, revision, false, null, [], new MvvmFault(code, message), null);

    internal static MvvmResponse Rejected(MvvmRequestId requestId, long revision, MvvmFault fault) =>
        new(requestId, revision, false, null, [], fault, null);

    internal static MvvmResponse Rejected(
        MvvmRequestId requestId,
        long revision,
        MvvmFault fault,
        JsonElement? payload,
        IReadOnlyList<MvvmPatch> patches) =>
        new(requestId, revision, false, payload, patches, fault, null);
}

/// <summary>Identifies which closed protocol schema applies to a wire message.</summary>
public enum MvvmMessageDirection
{
    /// <summary>A message sent by a client to a host.</summary>
    ClientToHost,

    /// <summary>A message sent by a host to a client.</summary>
    HostToClient,
}

/// <summary>A fully validated protocol version 1 wire message.</summary>
/// <remarks>
/// Instances can only be produced by <see cref="MvvmMessageCodec"/>. The owned JSON values remain
/// valid for the lifetime of the instance and are detached from the caller's input buffer.
/// </remarks>
public sealed class MvvmWireMessage
{
    private readonly JsonElement _document;

    internal MvvmWireMessage(MvvmMessageDirection direction, string kind, JsonElement document)
    {
        Direction = direction;
        Kind = kind;
        _document = document.Clone();
    }

    /// <summary>Gets the schema direction used to validate this message.</summary>
    public MvvmMessageDirection Direction { get; }

    /// <summary>Gets the closed protocol message discriminator.</summary>
    public string Kind { get; }

    /// <summary>Gets the validated version, which is always <see cref="MvvmProtocol.MajorVersion"/>.</summary>
    public int Version => _document.GetProperty("v").GetInt32();

    /// <summary>Gets the complete validated envelope as an owned JSON value.</summary>
    public JsonElement Document => _document;

    /// <summary>Gets the validated typed payload.</summary>
    public JsonElement Payload => _document.GetProperty("payload");

    /// <summary>Attempts to get an optional envelope property.</summary>
    public bool TryGetProperty(string propertyName, out JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        return _document.TryGetProperty(propertyName, out value);
    }
}

/// <summary>A stable validation failure produced before a wire message is dispatched.</summary>
public sealed class MvvmProtocolException : FormatException
{
    internal MvvmProtocolException(string code, string message, string path = "$")
        : base(message)
    {
        Code = code;
        Path = path;
    }

    /// <summary>Gets a stable machine-readable validation error code.</summary>
    public string Code { get; }

    /// <summary>Gets a bounded schema path containing no attacker-controlled property names.</summary>
    public string Path { get; }
}

/// <summary>Stable validation error codes returned by the protocol version 1 codec.</summary>
public static class MvvmValidationErrorCodes
{
    /// <summary>The frame exceeds an effective or hard byte limit.</summary>
    public const string FrameLimitExceeded = "frame.limitExceeded";

    /// <summary>The frame starts with a forbidden UTF-8 byte-order mark.</summary>
    public const string ByteOrderMarkForbidden = "frame.byteOrderMarkForbidden";

    /// <summary>The frame is not strictly encoded UTF-8.</summary>
    public const string InvalidUtf8 = "frame.invalidUtf8";

    /// <summary>The frame is not exactly one strict JSON document.</summary>
    public const string InvalidJson = "json.invalid";

    /// <summary>An object contains a duplicate decoded property name.</summary>
    public const string DuplicateProperty = "json.duplicateProperty";

    /// <summary>A parsed JSON value exceeds an effective or hard structural limit.</summary>
    public const string JsonLimitExceeded = "json.limitExceeded";

    /// <summary>The envelope is not a member of the applicable closed schema union.</summary>
    public const string SchemaInvalid = "schema.invalid";

    /// <summary>A schema-valid shape violates an additional protocol semantic invariant.</summary>
    public const string SemanticInvalid = "message.semanticInvalid";
}
