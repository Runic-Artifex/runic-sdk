using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

[assembly: InternalsVisibleTo("Runic.Application.Testing.Tests")]

namespace Runic.Application.Views;

// Kept beside the registry because some focused hosts compile this source
// directly while the production Bridge runtime supplies the implementation.
// It remains internal until host and generator integration agree on its shape.
internal interface IBridgeModelTurn
{
    T Run<T>(Func<T> work);
    void Run(Action work);
}

// A window-owner primitive for one scalar model field. It is intentionally
// internal: generator metadata, model execution ownership, and the frontend
// wire contract have not yet agreed on a public author-facing shape.
//
// The owner supplies access to the actual ViewModel property. This registry
// does not keep a detached model copy. Writes routed through this registry are
// serialized by its gate and compare their expected field version and value
// before the setter runs. Other writers must call ObserveExternal after their
// authoritative mutation, preferably from the same model execution context.
internal sealed class BridgeFieldWriteRegistry<T> : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<T> _read;
    private readonly Action<T> _apply;
    private readonly Func<T, string?>? _validate;
    private readonly IBridgeModelTurn? _modelTurn;
    // State retained by this registry is a baseline for a later compare, not
    // merely a convenient copy of the model field.  In particular a mutable
    // DTO or collection must not be able to mutate that baseline through a
    // previously returned snapshot or receipt.  Generated complex codecs pass
    // a structural comparer and a codec-backed snapshot function here.
    private readonly Func<T, T> _snapshot;
    private readonly IEqualityComparer<T> _equals;
    private readonly Dictionary<string, RetainedReceipt> _receipts = new(StringComparer.Ordinal);
    private readonly Queue<string> _receiptOrder = new();
    private readonly HashSet<string> _expiredRequestIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _expiredRequestOrder = new();
    private readonly int _maximumRetainedWrites;
    private readonly int _maximumRetainedReceiptBytes;
    private readonly Func<T, string>? _canonicalize;
    private readonly Func<T, int>? _retainedValueByteCount;
    private int _retainedReceiptBytes;
    private BridgeFieldSnapshot<T> _current;
    private bool _applying;
    private bool _disposed;

    internal BridgeFieldWriteRegistry(
        string ownerId,
        string fieldName,
        Func<T> read,
        Action<T> apply,
        int maximumRetainedWrites = 64,
        Func<T, string?>? validate = null,
        IBridgeModelTurn? modelTurn = null,
        Func<T, T>? snapshot = null,
        IEqualityComparer<T>? equalityComparer = null,
        int maximumRetainedReceiptBytes = 262_144,
        Func<T, string>? canonicalize = null,
        Func<T, int>? retainedValueByteCount = null)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("An owner identity is required.", nameof(ownerId));
        if (string.IsNullOrWhiteSpace(fieldName)) throw new ArgumentException("A field name is required.", nameof(fieldName));
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRetainedWrites, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetainedReceiptBytes);

        OwnerId = ownerId;
        FieldName = fieldName;
        _read = read;
        _apply = apply;
        _validate = validate;
        _modelTurn = modelTurn;
        _snapshot = snapshot ?? (static value => value);
        _equals = equalityComparer ?? EqualityComparer<T>.Default;
        _maximumRetainedWrites = maximumRetainedWrites;
        _maximumRetainedReceiptBytes = maximumRetainedReceiptBytes;
        _canonicalize = canonicalize;
        _retainedValueByteCount = retainedValueByteCount;
        _current = new(Capture(InTurn(_read)), Version: 0);
    }

    internal string OwnerId { get; }
    internal string FieldName { get; }

    internal BridgeFieldSnapshot<T> Current
    {
        get
        {
            return InTurn(() =>
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    return CopySnapshot(_current);
                }
            });
        }
    }

    // A retry with the same stable request identity and payload receives the
    // original receipt and never invokes the setter a second time. Reusing a
    // request ID for a different baseline or value is a conflict: replaying a
    // prior receipt there would make a caller mistake look like success.
    internal BridgeFieldWriteReceipt<T> Apply(BridgeFieldWriteRequest<T> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);

        return InTurn(() => ApplyCore(request));
    }

    private BridgeFieldWriteReceipt<T> ApplyCore(BridgeFieldWriteRequest<T> request)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_receipts.TryGetValue(request.RequestId, out var existing))
            {
                if (existing.Identity.Matches(request, _equals, _canonicalize))
                    return CopyReceipt(existing.Receipt);
                return new(request.RequestId, BridgeFieldWriteReceiptKind.Conflict, CopySnapshot(_current),
                    "The request identity was already used with a different checked-write payload.", null);
            }
            if (_expiredRequestIds.Contains(RequestIdDigest(request.RequestId)))
                return new(request.RequestId, BridgeFieldWriteReceiptKind.Conflict, CopySnapshot(_current),
                    "The request identity expired. Reconcile authoritative field state before issuing a new write.", null);

            // Capture the identity before a setter can mutate authoritative
            // state. A generated canonicalizer failure must reject the input,
            // never turn an already-applied write into an unrecorded result.
            var identity = RequestIdentity.Capture(request, _snapshot, _canonicalize);

            // A writer bypassed ObserveExternal. Do not use a stale cached
            // value to overwrite it; advance the field version and report a
            // conflict. The owner should still arrange explicit observation
            // for normal background updates and cross-thread coordination.
            var actual = Capture(_read());
            if (!_equals.Equals(actual, _current.Value))
            {
                _current = Advance(actual);
                return Retain(identity, new(request.RequestId, BridgeFieldWriteReceiptKind.Conflict, _current,
                    "The authoritative value changed without an observed field update.", null));
            }

            if (request.ExpectedVersion != _current.Version || !_equals.Equals(request.ExpectedValue, _current.Value))
                return Retain(identity, new(request.RequestId, BridgeFieldWriteReceiptKind.Conflict, _current,
                    "The write baseline no longer matches the authoritative field.", null));

            try
            {
                _applying = true;
                _apply(request.Value);
                var applied = Capture(_read()); // Preserve setter normalization in the receipt.
                _current = Advance(applied);
            }
            catch (Exception error)
            {
                // A setter may validate before or after changing its backing
                // field. Re-read it so a receipt never leaves the owner with a
                // stale observed snapshot.
                var afterFailure = Capture(_read());
                if (!_equals.Equals(afterFailure, _current.Value))
                {
                    _current = Advance(afterFailure);
                    return Retain(identity, new(request.RequestId, BridgeFieldWriteReceiptKind.PostApplyValidationFailed, _current,
                        error.Message, null));
                }
                return Retain(identity, new(request.RequestId, BridgeFieldWriteReceiptKind.Rejected, _current,
                    error.Message, null));
            }
            finally
            {
                _applying = false;
            }

            // Validation may inspect other application state and fail after
            // the setter has already committed. That is not a rejected write:
            // retain the post-setter snapshot and make the failure explicit so
            // a dependent action cannot proceed under a false assumption.
            try
            {
                return Retain(identity, new(request.RequestId, BridgeFieldWriteReceiptKind.Applied, _current,
                    null, _validate?.Invoke(Capture(_current.Value))));
            }
            catch (Exception error)
            {
                return Retain(identity, new(request.RequestId, BridgeFieldWriteReceiptKind.PostApplyValidationFailed, _current,
                    error.Message, null));
            }
        }
    }

    // Call this after a mutation from another authoritative path, such as a
    // background model refresh. It advances the per-field version even when
    // the scalar value happens to compare equal: a stale expected version must
    // not silently overwrite an independently accepted write.
    internal BridgeFieldSnapshot<T> ObserveExternal(T value)
    {
        return InTurn(() => ObserveExternalCore(value));
    }

    private BridgeFieldSnapshot<T> ObserveExternalCore(T value)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var actual = Capture(_read());
            if (!_equals.Equals(actual, value))
                throw new ArgumentException("The observed value does not match the authoritative field.", nameof(value));

            // A synchronous property-changed observer can re-enter while the
            // registry itself applies a write. That is acknowledgement of the
            // same write, not a second external version advance.
            if (_applying) return CopySnapshot(_current);
            _current = Advance(actual);
            return CopySnapshot(_current);
        }
    }

    internal BridgeFieldWriteStatus<T> Lookup(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _receipts.TryGetValue(requestId, out var receipt)
                ? new(requestId, BridgeFieldWriteStatusKind.Retained, CopyReceipt(receipt.Receipt))
                : new(requestId, BridgeFieldWriteStatusKind.Unknown, null);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _receipts.Clear();
            _receiptOrder.Clear();
            _expiredRequestIds.Clear();
            _expiredRequestOrder.Clear();
            _retainedReceiptBytes = 0;
        }
    }

    private BridgeFieldSnapshot<T> Advance(T value) => new(Capture(value), checked(_current.Version + 1));

    private T Capture(T value) => _snapshot(value);

    private BridgeFieldSnapshot<T> CopySnapshot(BridgeFieldSnapshot<T> snapshot) =>
        new(Capture(snapshot.Value), snapshot.Version);

    private BridgeFieldWriteReceipt<T> CopyReceipt(BridgeFieldWriteReceipt<T> receipt) =>
        new(receipt.RequestId, receipt.Kind, CopySnapshot(receipt.Current), receipt.Message, receipt.Validation);

    private TReturn InTurn<TReturn>(Func<TReturn> work) => _modelTurn is null ? work() : _modelTurn.Run(work);

    private BridgeFieldWriteReceipt<T> Retain(RequestIdentity identity, BridgeFieldWriteReceipt<T> receipt)
    {
        var retained = CopyReceipt(receipt);
        int bytes;
        try
        {
            bytes = checked(GetRetainedReceiptBytes(retained) + identity.RetainedBytes(_retainedValueByteCount));
        }
        catch (Exception)
        {
            // A value-size codec is accounting only. If a pathological
            // normalized value cannot be measured, preserve the completed
            // write's truth and make its request ID a bounded tombstone.
            RetainExpiredRequestId(retained.RequestId);
            return CopyReceipt(retained);
        }
        // If one receipt does not fit, retain a tombstone rather than an
        // unbounded value graph. The write has already occurred, so retrying
        // it must reconcile instead of being treated as a new write.
        if (bytes > _maximumRetainedReceiptBytes)
        {
            RetainExpiredRequestId(retained.RequestId);
            return CopyReceipt(retained);
        }

        _receipts.Add(retained.RequestId, new(
            identity, retained, bytes));
        _receiptOrder.Enqueue(retained.RequestId);
        _retainedReceiptBytes = checked(_retainedReceiptBytes + bytes);
        while (_receipts.Count > _maximumRetainedWrites
            || _retainedReceiptBytes > _maximumRetainedReceiptBytes)
        {
            var expired = _receiptOrder.Dequeue();
            var evicted = _receipts[expired];
            _receipts.Remove(expired);
            _retainedReceiptBytes -= evicted.Bytes;
            RetainExpiredRequestId(expired);
        }
        return CopyReceipt(retained);
    }

    private int GetRetainedReceiptBytes(BridgeFieldWriteReceipt<T> receipt)
    {
        var valueBytes = _retainedValueByteCount?.Invoke(receipt.Current.Value)
            ?? EstimateRetainedValueBytes(receipt.Current.Value);
        if (valueBytes < 0) throw new InvalidOperationException("A retained checked-field value byte count cannot be negative.");
        return checked(valueBytes
            + Encoding.UTF8.GetByteCount(receipt.RequestId)
            + (receipt.Message is null ? 0 : Encoding.UTF8.GetByteCount(receipt.Message))
            + (receipt.Validation is null ? 0 : Encoding.UTF8.GetByteCount(receipt.Validation))
            + sizeof(long) + 32);
    }

    // Reflection is intentionally not used for an arbitrary application
    // object. Generated checked properties provide their canonical wire byte
    // count; this conservative fallback keeps direct/internal callers bounded.
    private static int EstimateRetainedValueBytes(T value) => value switch
    {
        null => 4,
        string text => Encoding.UTF8.GetByteCount(text),
        byte[] bytes => bytes.Length,
        bool => 5,
        char => 3,
        sbyte or byte => 4,
        short or ushort => 6,
        int or uint or float => 16,
        long or ulong or double or decimal => 32,
        Guid => 36,
        DateTime or DateTimeOffset or TimeSpan => 40,
        _ => 65_536,
    };

    // Retaining a finite tombstone prevents an immediate late retry from
    // replaying a rejected or otherwise evicted stable request ID. Once this
    // bounded history expires too, Lookup is unknown and the caller must
    // reconcile before treating a retry as new; this is not exactly-once.
    private void RetainExpiredRequestId(string requestId)
    {
        var digest = RequestIdDigest(requestId);
        if (!_expiredRequestIds.Add(digest)) return;
        _expiredRequestOrder.Enqueue(digest);
        while (_expiredRequestIds.Count > _maximumRetainedWrites)
            _expiredRequestIds.Remove(_expiredRequestOrder.Dequeue());
    }

    private static string RequestIdDigest(string requestId) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(requestId)));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record RetainedReceipt(RequestIdentity Identity, BridgeFieldWriteReceipt<T> Receipt, int Bytes);

    // The digest is derived from generated canonical wire values and retains
    // no incoming value graph. Direct callers without a canonicalizer retain
    // copied values only as a compatibility fallback.
    private sealed class RequestIdentity
    {
        private readonly long _expectedVersion;
        private readonly string? _expectedDigest;
        private readonly string? _valueDigest;
        private readonly T? _expectedValue;
        private readonly T? _value;

        private RequestIdentity(long expectedVersion, string? expectedDigest, string? valueDigest,
            T? expectedValue, T? value)
        {
            _expectedVersion = expectedVersion;
            _expectedDigest = expectedDigest;
            _valueDigest = valueDigest;
            _expectedValue = expectedValue;
            _value = value;
        }

        internal static RequestIdentity Capture(BridgeFieldWriteRequest<T> request, Func<T, T> snapshot,
            Func<T, string>? canonicalize) => canonicalize is null
            ? new(request.ExpectedVersion, null, null, snapshot(request.ExpectedValue), snapshot(request.Value))
            : new(request.ExpectedVersion, Digest(canonicalize(request.ExpectedValue)), Digest(canonicalize(request.Value)), default, default);

        internal bool Matches(BridgeFieldWriteRequest<T> request, IEqualityComparer<T> equals,
            Func<T, string>? canonicalize)
        {
            if (_expectedVersion != request.ExpectedVersion) return false;
            if (_expectedDigest is not null || _valueDigest is not null)
                return canonicalize is not null
                    && _expectedDigest == Digest(canonicalize(request.ExpectedValue))
                    && _valueDigest == Digest(canonicalize(request.Value));
            return equals.Equals(_expectedValue!, request.ExpectedValue)
                && equals.Equals(_value!, request.Value);
        }

        internal int RetainedBytes(Func<T, int>? count) => checked(sizeof(long)
            + (_expectedDigest is not null ? _expectedDigest.Length + _valueDigest!.Length
                : (count?.Invoke(_expectedValue!) ?? EstimateRetainedValueBytes(_expectedValue!))
                    + (count?.Invoke(_value!) ?? EstimateRetainedValueBytes(_value!))));

        private static string Digest(string canonicalValue) => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalValue)));
    }
}

internal sealed record BridgeFieldSnapshot<T>(T Value, long Version);

internal sealed record BridgeFieldWriteRequest<T>(
    string RequestId,
    long ExpectedVersion,
    T ExpectedValue,
    T Value);

internal enum BridgeFieldWriteReceiptKind { Applied, Rejected, Conflict, PostApplyValidationFailed }

// Applied carries optional business validation. PostApplyValidationFailed means
// the authoritative field changed, but the setter or post-setter validation
// did not complete cleanly; callers must reconcile rather than retry blindly.
internal sealed record BridgeFieldWriteReceipt<T>(
    string RequestId,
    BridgeFieldWriteReceiptKind Kind,
    BridgeFieldSnapshot<T> Current,
    string? Message,
    string? Validation);

internal enum BridgeFieldWriteStatusKind { Unknown, Retained }

internal sealed record BridgeFieldWriteStatus<T>(
    string RequestId,
    BridgeFieldWriteStatusKind Kind,
    BridgeFieldWriteReceipt<T>? Receipt);
