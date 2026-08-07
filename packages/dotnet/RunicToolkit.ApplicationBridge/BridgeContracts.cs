using System.Text.Json;
using System.Text.Json.Serialization;

namespace RunicToolkit.ApplicationBridge;

/// <summary>Identifies one logical frontend session.</summary>
public readonly record struct BridgeSessionId(Guid Value)
{
    /// <summary>Creates a cryptographically random session identifier.</summary>
    public static BridgeSessionId New() => new(Guid.NewGuid());
}

/// <summary>Identifies one client command.</summary>
public readonly record struct BridgeCommandId(Guid Value);

/// <summary>Identifies one backend-owned long-running operation.</summary>
public readonly record struct BridgeOperationId(Guid Value)
{
    /// <summary>Creates a new operation identifier.</summary>
    public static BridgeOperationId New() => new(Guid.NewGuid());
}

/// <summary>Bounds all untrusted bridge inputs and outstanding work.</summary>
public sealed record BridgeLimits
{
    /// <summary>Gets the default production limits.</summary>
    public static BridgeLimits Default { get; } = new();

    /// <summary>Maximum encoded frame size.</summary>
    public int MaxFrameBytes { get; init; } = 262_144;

    /// <summary>Maximum JSON nesting depth.</summary>
    public int MaxDepth { get; init; } = 32;

    /// <summary>Maximum UTF-8 bytes in one property name or string value.</summary>
    public int MaxStringBytes { get; init; } = 65_536;

    /// <summary>Maximum entries in one encoded object or array.</summary>
    public int MaxCollectionItems { get; init; } = 4_096;

    /// <summary>Maximum number of admitted commands.</summary>
    public int MaxPendingCommands { get; init; } = 64;

    /// <summary>Maximum command identifiers retained for duplicate rejection.</summary>
    public int MaxCommandLedgerEntries { get; init; } = 4096;

    /// <summary>Maximum deterministic shutdown duration.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (MaxFrameBytes is < 1024 or > 16_777_216 ||
            MaxDepth is < 4 or > 128 ||
            MaxStringBytes is < 64 or > 1_048_576 ||
            MaxCollectionItems is < 1 or > 65_536 ||
            MaxPendingCommands is < 1 or > 1024 ||
            MaxCommandLedgerEntries < MaxPendingCommands ||
            ShutdownTimeout <= TimeSpan.Zero ||
            ShutdownTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(BridgeLimits), "Application Bridge limits are invalid.");
        }
    }
}

/// <summary>A validated client-to-host envelope.</summary>
public sealed record BridgeClientEnvelope
{
    /// <summary>Contract identity.</summary>
    public required string Protocol { get; init; }

    /// <summary>Contract version.</summary>
    public required int Version { get; init; }

    /// <summary>Logical operation: initialize, dispatch, cancelOperation, uiReady, or uiRendered.</summary>
    public required string Kind { get; init; }

    /// <summary>Unique command identifier.</summary>
    public required Guid CommandId { get; init; }

    /// <summary>Active session after initialization.</summary>
    public Guid? SessionId { get; init; }

    /// <summary>Revision on which a mutation is based.</summary>
    public long? ExpectedRevision { get; init; }

    /// <summary>Schema-validated domain payload.</summary>
    public required JsonElement Payload { get; init; }
}

/// <summary>A host-to-client response or event envelope.</summary>
public sealed record BridgeHostEnvelope
{
    /// <summary>Contract identity.</summary>
    public required string Protocol { get; init; }

    /// <summary>Contract version.</summary>
    public required int Version { get; init; }

    /// <summary>Envelope kind: snapshot, receipt, event, or error.</summary>
    public required string Kind { get; init; }

    /// <summary>Owning session.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>Strictly increasing host sequence.</summary>
    public required long Sequence { get; init; }

    /// <summary>Authoritative application revision.</summary>
    public required long Revision { get; init; }

    /// <summary>Correlated command, when applicable.</summary>
    public Guid? CommandId { get; init; }

    /// <summary>Correlated operation, when applicable.</summary>
    public Guid? OperationId { get; init; }

    /// <summary>Schema-validated domain payload.</summary>
    public required JsonElement Payload { get; init; }
}

/// <summary>Sanitized public failure. Diagnostics remain on the host side.</summary>
public sealed record BridgePublicError(
    [property: JsonPropertyName("_tag")] string Tag,
    string Message,
    bool Retryable);

internal sealed record BridgeCancellationReceipt(
    [property: JsonPropertyName("_tag")] string Tag,
    Guid OperationId,
    bool Accepted,
    long Revision);

internal sealed record BridgeSignalReceipt(
    [property: JsonPropertyName("_tag")] string Tag);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BridgeClientEnvelope))]
[JsonSerializable(typeof(BridgeHostEnvelope))]
[JsonSerializable(typeof(BridgePublicError))]
[JsonSerializable(typeof(BridgeCancellationReceipt))]
[JsonSerializable(typeof(BridgeSignalReceipt))]
internal sealed partial class ApplicationBridgeJsonContext : JsonSerializerContext;
