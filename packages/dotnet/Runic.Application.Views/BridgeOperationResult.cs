using System.Text;
using System.Text.Json;

namespace Runic.Application.Views;

// The result is encoded before it enters the operation registry.  Keeping the
// wire value immutable means a later mutation of a command result cannot
// change what a reconnecting client observes.
/// <summary>The retained outcome of a bridge operation.</summary>
/// <param name="Kind">Whether the operation produced no value, a value or a stream.</param>
/// <param name="EncodedJson">The encoded result value, when <paramref name="Kind"/> is <see cref="BridgeOperationResultKind.Value"/>.</param>
/// <param name="DeliveryFailure">Why a completed result could not be retained or delivered, if it could not.</param>
/// <param name="OperationStream">The result stream, when <paramref name="Kind"/> is <see cref="BridgeOperationResultKind.Stream"/>.</param>
public sealed record BridgeOperationResult(
    BridgeOperationResultKind Kind,
    string? EncodedJson = null,
    BridgeOperationDeliveryFailure? DeliveryFailure = null,
    BridgeOperationStream? OperationStream = null)
{
    /// <summary>A completed operation without a result value.</summary>
    public static BridgeOperationResult None { get; } = new(BridgeOperationResultKind.None);
    /// <summary>The same as <see cref="None"/>.</summary>
    public static BridgeOperationResult Empty => None;

    /// <summary>Creates a value result from already encoded JSON.</summary>
    /// <exception cref="JsonException"><paramref name="encodedJson"/> is not valid JSON.</exception>
    public static BridgeOperationResult Succeeded(string encodedJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedJson);
        using var _ = JsonDocument.Parse(encodedJson);
        return new(BridgeOperationResultKind.Value, encodedJson);
    }

    // Result serialization happens after the command has produced its value.
    // Treat a codec failure as a retention/delivery outcome so callers never
    // retry a completed side effect merely because its reply was unavailable.
    /// <summary>Encodes a value result, reporting an encoding failure as a delivery failure.</summary>
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

    /// <summary>Creates a result for a completed operation whose value could not be delivered.</summary>
    public static BridgeOperationResult DeliveryFailed(BridgeOperationDeliveryFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(BridgeOperationResultKind.None, DeliveryFailure: failure);
    }

    /// <summary>Creates a result whose values are read through <paramref name="stream"/>.</summary>
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

/// <summary>The shape of a <see cref="BridgeOperationResult"/>.</summary>
public enum BridgeOperationResultKind
{
    /// <summary>The operation produced no value.</summary>
    None,
    /// <summary>The operation produced one encoded value.</summary>
    Value,
    /// <summary>The operation produces values through a stream.</summary>
    Stream,
}

// Delivery/retention is deliberately distinct from command execution. A
// command can have completed its side effect even when its encoded answer was
// too large to retain for reconnect/recovery.
/// <summary>Why a completed operation's result could not be retained or delivered.</summary>
public enum BridgeOperationDeliveryFailureKind
{
    /// <summary>The encoded result exceeded the retention limit.</summary>
    ResultTooLarge,
    /// <summary>The result could not be encoded.</summary>
    ResultEncodingFailed,
    /// <summary>The stream exceeded its item or byte limit.</summary>
    StreamOverflow,
    /// <summary>The window-wide stream retention budget was exhausted.</summary>
    StreamRetentionTooLarge,
}

/// <summary>Describes a result delivery failure.</summary>
/// <param name="Kind">The failure category.</param>
/// <param name="Message">A diagnostic message.</param>
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
/// <summary>A bounded, cursor-addressed sequence of encoded operation results.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Published name for the operation result stream protocol; it is not a System.IO.Stream.")]
public sealed class BridgeOperationStream
{
    /// <summary>
    /// The default per-stream byte bound. A window reserves each running stream's bound
    /// against a 256 KiB running-stream budget, so by default four streams can run at once.
    /// </summary>
    public const int DefaultMaximumBytes = 65_536;

    private readonly object _gate = new();
    private readonly List<BridgeOperationStreamItem> _items = [];
    private readonly int _maximumItems;
    private readonly int _maximumBytes;
    private int _retainedBytes;
    private long _nextSequence = 1;
    private bool _completed;
    private BridgeOperationDeliveryFailure? _failure;

    /// <summary>Creates a bounded stream. <see cref="TryPublish"/> returns false once it overflows.</summary>
    public BridgeOperationStream(int maximumItems = 128, int maximumBytes = DefaultMaximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumItems, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        _maximumItems = maximumItems;
        _maximumBytes = maximumBytes;
    }

    /// <summary>Appends an encoded value.</summary>
    /// <returns><see langword="false"/> when the stream is complete, failed or has overflowed.</returns>
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

    /// <summary>Marks the stream as complete; later values are rejected.</summary>
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

    /// <summary>Reads the retained values whose sequence is greater than <paramref name="cursor"/>.</summary>
    public BridgeOperationStreamRead ReadAfter(long cursor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        lock (_gate)
        {
            var items = _items.Where(item => item.Sequence > cursor).ToArray();
            return new(items, _nextSequence - 1, _completed, _failure);
        }
    }

    /// <summary>The terminal delivery failure, if the stream failed.</summary>
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

/// <summary>One encoded stream value.</summary>
/// <param name="Sequence">The value's one-based position in the stream.</param>
/// <param name="EncodedJson">The encoded value.</param>
public sealed record BridgeOperationStreamItem(long Sequence, string EncodedJson);
/// <summary>The result of <see cref="BridgeOperationStream.ReadAfter(long)"/>.</summary>
/// <param name="Items">Values after the requested cursor.</param>
/// <param name="NextCursor">The cursor to pass to the next read.</param>
/// <param name="Completed">Whether the stream is complete.</param>
/// <param name="Failure">The terminal delivery failure, if any.</param>
public sealed record BridgeOperationStreamRead(
    IReadOnlyList<BridgeOperationStreamItem> Items,
    long NextCursor,
    bool Completed,
    BridgeOperationDeliveryFailure? Failure);
