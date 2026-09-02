using System.Text.Json;

namespace Runic.Application.Bridge;

/// <summary>Dispatches one closed generated application contract.</summary>
public interface IApplicationBridgeDispatcher
{
    /// <summary>Gets the exact protocol identity.</summary>
    string ProtocolIdentity { get; }

    /// <summary>Gets the exact protocol version.</summary>
    int ProtocolVersion { get; }

    /// <summary>Gets the generated contract SHA-256 fingerprint used by the handshake.</summary>
    string ManifestFingerprint { get; }

    /// <summary>Dispatches one already framed command payload.</summary>
    ValueTask<BridgeDispatchResult> DispatchAsync(
        JsonElement command,
        BridgeCommandContext context,
        CancellationToken cancellationToken);

    /// <summary>Validates and canonically re-encodes one declared bridge error.</summary>
    JsonElement ValidateError(JsonElement payload) =>
        throw new JsonException("This dispatcher does not declare application errors.");
}

/// <summary>A typed application failure created by generated contract helpers.</summary>
public sealed class BridgeCommandFailureException : Exception
{
    /// <summary>Creates a failure carrying an encoded application error.</summary>
    public BridgeCommandFailureException(JsonElement error)
        : base("The application command returned a declared bridge error.") => Error = error.Clone();

    /// <summary>Gets the encoded error. The generated dispatcher validates it before transport.</summary>
    public JsonElement Error { get; }
}

/// <summary>Generated dispatch result containing a typed receipt encoded as JSON.</summary>
public sealed record BridgeDispatchResult(
    JsonElement Receipt,
    bool AdvancesRevision = false,
    BridgeOperationId? OperationId = null,
    bool Cancellable = false);

/// <summary>Safe event output from a handler or owned operation.</summary>
public sealed record BridgeEventPayload(
    JsonElement Payload,
    bool AdvancesRevision = false,
    BridgeOperationId? OperationId = null);

/// <summary>Publishes schema-validated domain events without exposing a transport.</summary>
public interface IBridgeEventPublisher
{
    /// <summary>Publishes one encoded domain event.</summary>
    ValueTask PublishAsync(BridgeEventPayload eventPayload, CancellationToken cancellationToken = default);
}

/// <summary>Starts backend-owned operations with explicit cancellation ownership.</summary>
public interface IBridgeOperationFactory
{
    /// <summary>Starts one operation and returns its opaque identifier immediately.</summary>
    BridgeOperationId Start(
        Func<BridgeOperationId, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default);
}

/// <summary>Safe infrastructure available to generated handler methods.</summary>
public sealed class BridgeCommandContext
{
    internal BridgeCommandContext(
        BridgeSessionId sessionId,
        BridgeCommandId commandId,
        bool isInitialization,
        long? expectedRevision,
        long currentRevision,
        IBridgeEventPublisher events,
        IBridgeOperationFactory operations)
    {
        SessionId = sessionId;
        CommandId = commandId;
        IsInitialization = isInitialization;
        ExpectedRevision = expectedRevision;
        CurrentRevision = currentRevision;
        Events = events;
        Operations = operations;
    }

    /// <summary>Gets the active logical session.</summary>
    public BridgeSessionId SessionId { get; }

    /// <summary>Gets the admitted command.</summary>
    public BridgeCommandId CommandId { get; }

    /// <summary>Gets whether the command was admitted through the initialization envelope.</summary>
    public bool IsInitialization { get; }

    /// <summary>Gets the client revision, when supplied.</summary>
    public long? ExpectedRevision { get; }

    /// <summary>Gets the authoritative revision at admission.</summary>
    public long CurrentRevision { get; }

    /// <summary>Gets the safe event publisher.</summary>
    public IBridgeEventPublisher Events { get; }

    /// <summary>Gets the owned-operation factory.</summary>
    public IBridgeOperationFactory Operations { get; }
}
