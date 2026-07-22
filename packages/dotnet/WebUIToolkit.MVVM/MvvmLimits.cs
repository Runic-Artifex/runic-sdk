namespace WebUIToolkit.MVVM;

/// <summary>Hard bounds applied by the protocol and session runtime.</summary>
public sealed record MvvmLimits
{
    /// <summary>The protocol version 1 hard frame ceiling.</summary>
    public const int MaximumPayloadBytes = 1_048_576;

    /// <summary>The protocol version 1 hard JSON nesting ceiling.</summary>
    public const int MaximumJsonDepth = 32;

    /// <summary>The protocol version 1 hard string ceiling in UTF-8 bytes.</summary>
    public const int MaximumStringBytes = 65_536;

    /// <summary>The protocol version 1 hard JSON property-name ceiling in UTF-8 bytes.</summary>
    public const int MaximumPropertyNameBytes = 128;

    /// <summary>The protocol version 1 hard JSON object property ceiling.</summary>
    public const int MaximumObjectProperties = 4_096;

    /// <summary>The protocol version 1 hard general-array ceiling.</summary>
    public const int MaximumArrayItems = 10_000;

    /// <summary>The protocol version 1 hard snapshot-member ceiling.</summary>
    public const int MaximumSnapshotMembers = 4_096;

    /// <summary>The protocol version 1 hard patch-change ceiling.</summary>
    public const int MaximumPatchOperations = 1_024;

    /// <summary>The protocol version 1 hard pending-request ceiling.</summary>
    public const int MaximumPendingRequests = 64;

    /// <summary>The fixed version 1 lifetime ceiling for distinct admitted request identifiers.</summary>
    /// <remarks>This lifecycle safety cap is fixed and is not an advertised effective limit.</remarks>
    public const int MaximumRequestLedgerEntries = 65_536;

    /// <summary>The protocol version 1 hard session ceiling.</summary>
    public const int MaximumSessions = 16;

    /// <summary>The protocol version 1 hard projected-collection ceiling.</summary>
    public const int MaximumCollectionItems = 10_000;

    /// <summary>The protocol version 1 hard command timeout.</summary>
    public static TimeSpan MaximumCommandDuration { get; } = TimeSpan.FromMinutes(5);

    /// <summary>The runtime hard ceiling for cooperative activation and teardown grace.</summary>
    public static TimeSpan MaximumShutdownDuration { get; } = TimeSpan.FromMinutes(5);

    /// <summary>The recommended bounded defaults for protocol version 1.</summary>
    public static MvvmLimits Default { get; } = new();

    /// <summary>Gets the maximum UTF-8 request or response payload size. Defaults to 1 MiB.</summary>
    public int MaxPayloadBytes { get; init; } = MaximumPayloadBytes;

    /// <summary>Gets the maximum parsed JSON nesting depth.</summary>
    public int MaxJsonDepth { get; init; } = MaximumJsonDepth;

    /// <summary>Gets the maximum general string size in UTF-8 bytes.</summary>
    public int MaxStringBytes { get; init; } = MaximumStringBytes;

    /// <summary>Gets the maximum JSON property-name size in UTF-8 bytes.</summary>
    public int MaxPropertyNameBytes { get; init; } = MaximumPropertyNameBytes;

    /// <summary>Gets the maximum properties in one JSON object.</summary>
    public int MaxObjectProperties { get; init; } = MaximumObjectProperties;

    /// <summary>Gets the maximum items in one general JSON array.</summary>
    public int MaxArrayItems { get; init; } = MaximumArrayItems;

    /// <summary>Gets the maximum members in an authoritative snapshot.</summary>
    public int MaxSnapshotMembers { get; init; } = MaximumSnapshotMembers;

    /// <summary>Gets the maximum patch operations produced by one mutation.</summary>
    public int MaxPatchOperations { get; init; } = MaximumPatchOperations;

    /// <summary>Gets the maximum concurrent requests admitted by one session.</summary>
    public int MaxPendingRequests { get; init; } = MaximumPendingRequests;

    /// <summary>Gets the maximum simultaneously open sessions owned by one factory.</summary>
    public int MaxSessions { get; init; } = MaximumSessions;

    /// <summary>Gets the maximum items in one projected collection.</summary>
    public int MaxCollectionItems { get; init; } = MaximumCollectionItems;

    /// <summary>Gets the maximum duration of an adapter operation.</summary>
    public TimeSpan MaxCommandDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the cooperative grace allowed for activation cancellation and teardown.</summary>
    public TimeSpan MaxShutdownDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Checks that every configured bound is positive and finite.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit is not usable.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxJsonDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxStringBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPropertyNameBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxObjectProperties);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxArrayItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSnapshotMembers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPatchOperations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPendingRequests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSessions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxCollectionItems);

        if (MaxPayloadBytes > MaximumPayloadBytes ||
            MaxJsonDepth > MaximumJsonDepth ||
            MaxStringBytes > MaximumStringBytes ||
            MaxPropertyNameBytes > MaximumPropertyNameBytes ||
            MaxObjectProperties > MaximumObjectProperties ||
            MaxArrayItems > MaximumArrayItems ||
            MaxSnapshotMembers > MaximumSnapshotMembers ||
            MaxPatchOperations > MaximumPatchOperations ||
            MaxPendingRequests > MaximumPendingRequests ||
            MaxSessions > MaximumSessions ||
            MaxCollectionItems > MaximumCollectionItems)
        {
            throw new ArgumentOutOfRangeException(nameof(MvvmLimits), "A configured limit exceeds the protocol version 1 hard ceiling.");
        }

        if (MaxCommandDuration <= TimeSpan.Zero ||
            MaxCommandDuration == Timeout.InfiniteTimeSpan ||
            MaxCommandDuration > MaximumCommandDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCommandDuration), "The command duration must be positive and no greater than the protocol ceiling.");
        }

        if (MaxShutdownDuration <= TimeSpan.Zero ||
            MaxShutdownDuration == Timeout.InfiniteTimeSpan ||
            MaxShutdownDuration > MaximumShutdownDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxShutdownDuration), "The shutdown duration must be positive and no greater than the runtime ceiling.");
        }
    }
}
