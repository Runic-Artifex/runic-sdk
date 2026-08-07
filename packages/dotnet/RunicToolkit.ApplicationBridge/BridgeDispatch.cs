using System.Text.Json;

namespace RunicToolkit.ApplicationBridge;

/// <summary>Dispatches one closed generated application contract.</summary>
public interface IApplicationBridgeDispatcher
{
    /// <summary>Gets the exact protocol identity.</summary>
    string ProtocolIdentity { get; }

    /// <summary>Gets the exact protocol version.</summary>
    int ProtocolVersion { get; }

    /// <summary>Gets the committed manifest SHA-256 fingerprint.</summary>
    string ManifestFingerprint { get; }

    /// <summary>Dispatches one already framed command payload.</summary>
    ValueTask<BridgeDispatchResult> DispatchAsync(
        JsonElement command,
        BridgeCommandContext context,
        CancellationToken cancellationToken);
}

/// <summary>Generated dispatch result containing a typed receipt encoded as JSON.</summary>
public sealed record BridgeDispatchResult(
    JsonElement Receipt,
    bool AdvancesRevision = false,
    BridgeOperationId? OperationId = null);

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
        long? expectedRevision,
        long currentRevision,
        IBridgeEventPublisher events,
        IBridgeOperationFactory operations)
    {
        SessionId = sessionId;
        CommandId = commandId;
        ExpectedRevision = expectedRevision;
        CurrentRevision = currentRevision;
        Events = events;
        Operations = operations;
    }

    /// <summary>Gets the active logical session.</summary>
    public BridgeSessionId SessionId { get; }

    /// <summary>Gets the admitted command.</summary>
    public BridgeCommandId CommandId { get; }

    /// <summary>Gets the client revision, when supplied.</summary>
    public long? ExpectedRevision { get; }

    /// <summary>Gets the authoritative revision at admission.</summary>
    public long CurrentRevision { get; }

    /// <summary>Gets the safe event publisher.</summary>
    public IBridgeEventPublisher Events { get; }

    /// <summary>Gets the owned-operation factory.</summary>
    public IBridgeOperationFactory Operations { get; }
}
