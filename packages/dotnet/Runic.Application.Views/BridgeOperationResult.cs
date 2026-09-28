using System.Text;
using System.Text.Json;

namespace Runic.Application.Views;

// The result is encoded before it enters the operation registry.  Keeping the
// wire value immutable means a later mutation of a command result cannot
// change what a reconnecting client observes.
public sealed record BridgeOperationResult(
    BridgeOperationResultKind Kind,
    string? EncodedJson = null,
    BridgeOperationDeliveryFailure? DeliveryFailure = null,
    BridgeOperationStream? OperationStream = null)
{
    public static BridgeOperationResult None { get; } = new(BridgeOperationResultKind.None);
    public static BridgeOperationResult Empty => None;

    public static BridgeOperationResult Succeeded(string encodedJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedJson);
        using var _ = JsonDocument.Parse(encodedJson);
        return new(BridgeOperationResultKind.Value, encodedJson);
    }

    // Result serialization happens after the command has produced its value.
    // Treat a codec failure as a retention/delivery outcome so callers never
    // retry a completed side effect merely because its reply was unavailable.
    public static BridgeOperationResult Encode(Func<string> encode)
    {
        ArgumentNullException.ThrowIfNull(encode);
        try
        {
            return Succeeded(encode());
        }
        catch (Exception)
        {
            return DeliveryFailed(BridgeOperationDeliveryFailure.ResultEncodingFailed());
        }
    }

    public static BridgeOperationResult DeliveryFailed(BridgeOperationDeliveryFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(BridgeOperationResultKind.None, DeliveryFailure: failure);
    }

    public static BridgeOperationResult Stream(BridgeOperationStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new(BridgeOperationResultKind.Stream, OperationStream: stream);
    }

    internal int EncodedByteCount => EncodedJson is null ? 0 : Encoding.UTF8.GetByteCount(EncodedJson);

    internal BridgeOperationResult WithoutValue(BridgeOperationDeliveryFailure failure) =>
        new(Kind is BridgeOperationResultKind.Stream ? BridgeOperationResultKind.Stream : BridgeOperationResultKind.None,
            null, failure, OperationStream);
}

public enum BridgeOperationResultKind { None, Value, Stream }

// Delivery/retention is deliberately distinct from command execution. A
// command can have completed its side effect even when its encoded answer was
// too large to retain for reconnect/recovery.
public enum BridgeOperationDeliveryFailureKind
{
    ResultTooLarge,
    ResultEncodingFailed,
    StreamOverflow,
    StreamRetentionTooLarge,
}

public sealed record BridgeOperationDeliveryFailure(BridgeOperationDeliveryFailureKind Kind, string Message)
{
    internal static BridgeOperationDeliveryFailure ResultTooLarge(int maximumBytes) =>
        new(BridgeOperationDeliveryFailureKind.ResultTooLarge,
            $"The operation result exceeds the {maximumBytes} byte retention limit.");

    internal static BridgeOperationDeliveryFailure ResultEncodingFailed() =>
        new(BridgeOperationDeliveryFailureKind.ResultEncodingFailed,
            "The operation completed, but its result could not be encoded.");

    internal static BridgeOperationDeliveryFailure StreamOverflow(int maximumItems, int maximumBytes) =>
        new(BridgeOperationDeliveryFailureKind.StreamOverflow,
            $"The operation stream exceeded its {maximumItems} item or {maximumBytes} byte retention limit.");

    internal static BridgeOperationDeliveryFailure StreamRetentionTooLarge(int maximumBytes) =>
        new(BridgeOperationDeliveryFailureKind.StreamRetentionTooLarge,
            $"The operation stream exceeded the {maximumBytes} byte aggregate retention limit.");
}

// A stream is opt-in and has a cursor protocol. It never silently discards an
// earlier result and calls it a last value: overflow is terminal and visible.
public sealed class BridgeOperationStream
{
    private readonly object _gate = new();
    private readonly List<BridgeOperationStreamItem> _items = [];
    private readonly int _maximumItems;
    private readonly int _maximumBytes;
    private int _retainedBytes;
    private long _nextSequence = 1;
    private bool _completed;
    private BridgeOperationDeliveryFailure? _failure;

    public BridgeOperationStream(int maximumItems = 128, int maximumBytes = 262_144)
    {
        if (maximumItems < 1) throw new ArgumentOutOfRangeException(nameof(maximumItems));
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumItems = maximumItems;
        _maximumBytes = maximumBytes;
    }

    public bool TryPublish(string encodedJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedJson);
        using var _ = JsonDocument.Parse(encodedJson);
        var bytes = Encoding.UTF8.GetByteCount(encodedJson);
        lock (_gate)
        {
            if (_completed || _failure is not null) return false;
            if (_items.Count == _maximumItems || bytes > _maximumBytes || _retainedBytes > _maximumBytes - bytes)
            {
                _failure = BridgeOperationDeliveryFailure.StreamOverflow(_maximumItems, _maximumBytes);
                return false;
            }
            _items.Add(new(_nextSequence++, encodedJson));
            _retainedBytes += bytes;
            return true;
        }
    }

    public void Complete()
    {
        lock (_gate) _completed = true;
    }

    // Registry retention is separate from the producer's per-stream limit.
    // Once the window-wide budget is exhausted, retaining partial history
    // would make later recovery depend on admission order. Clear it and make
    // that delivery outcome explicit instead.
    internal void DiscardRetention(BridgeOperationDeliveryFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            _items.Clear();
            _retainedBytes = 0;
            _completed = true;
            _failure = failure;
        }
    }

    /// <summary>Stops accepting values and exposes a delivery failure to stream readers.</summary>
    public void Fail(BridgeOperationDeliveryFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            if (_completed || _failure is not null) return;
            _failure = failure;
        }
    }

    public BridgeOperationStreamRead ReadAfter(long cursor)
    {
        if (cursor < 0) throw new ArgumentOutOfRangeException(nameof(cursor));
        lock (_gate)
        {
            var items = _items.Where(item => item.Sequence > cursor).ToArray();
            return new(items, _nextSequence - 1, _completed, _failure);
        }
    }

    public BridgeOperationDeliveryFailure? Failure
    {
        get { lock (_gate) return _failure; }
    }

    internal int RetainedByteCount
    {
        get { lock (_gate) return _retainedBytes; }
    }

    internal int MaximumBytes => _maximumBytes;
}

public sealed record BridgeOperationStreamItem(long Sequence, string EncodedJson);
public sealed record BridgeOperationStreamRead(
    IReadOnlyList<BridgeOperationStreamItem> Items,
    long NextCursor,
    bool Completed,
    BridgeOperationDeliveryFailure? Failure);
