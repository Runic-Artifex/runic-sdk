using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("EditScopeHostProbe")]
[assembly: InternalsVisibleTo("OperationAcceptanceProbe")]
[assembly: InternalsVisibleTo("SourceBackedIndependentDraftProbe")]

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
    private readonly EqualityComparer<T> _equals = EqualityComparer<T>.Default;
    private readonly Dictionary<string, BridgeFieldWriteReceipt<T>> _receipts = new(StringComparer.Ordinal);
    private readonly Queue<string> _receiptOrder = new();
    private readonly HashSet<string> _expiredRequestIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _expiredRequestOrder = new();
    private readonly int _maximumRetainedWrites;
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
        IBridgeModelTurn? modelTurn = null)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("An owner identity is required.", nameof(ownerId));
        if (string.IsNullOrWhiteSpace(fieldName)) throw new ArgumentException("A field name is required.", nameof(fieldName));
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(apply);
        if (maximumRetainedWrites < 1) throw new ArgumentOutOfRangeException(nameof(maximumRetainedWrites));

        OwnerId = ownerId;
        FieldName = fieldName;
        _read = read;
        _apply = apply;
        _validate = validate;
        _modelTurn = modelTurn;
        _maximumRetainedWrites = maximumRetainedWrites;
        _current = new(InTurn(_read), Version: 0);
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
                    return _current;
                }
            });
        }
    }

    // A retry with the same stable request identity receives the original
    // receipt and never invokes the setter a second time.
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
            if (_receipts.TryGetValue(request.RequestId, out var existing)) return existing;
            if (_expiredRequestIds.Contains(request.RequestId))
                return new(request.RequestId, BridgeFieldWriteReceiptKind.Conflict, _current,
                    "The request identity expired. Reconcile authoritative field state before issuing a new write.", null);

            // A writer bypassed ObserveExternal. Do not use a stale cached
            // value to overwrite it; advance the field version and report a
            // conflict. The owner should still arrange explicit observation
            // for normal background updates and cross-thread coordination.
            var actual = _read();
            if (!_equals.Equals(actual, _current.Value))
            {
                _current = Advance(actual);
                return Retain(new(request.RequestId, BridgeFieldWriteReceiptKind.Conflict, _current,
                    "The authoritative value changed without an observed field update.", null));
            }

            if (request.ExpectedVersion != _current.Version || !_equals.Equals(request.ExpectedValue, _current.Value))
                return Retain(new(request.RequestId, BridgeFieldWriteReceiptKind.Conflict, _current,
                    "The write baseline no longer matches the authoritative field.", null));

            try
            {
                _applying = true;
                _apply(request.Value);
                var applied = _read(); // Preserve setter normalization in the receipt.
                _current = Advance(applied);
            }
            catch (Exception error)
            {
                // A setter may validate before or after changing its backing
                // field. Re-read it so a receipt never leaves the owner with a
                // stale observed snapshot.
                var afterFailure = _read();
                if (!_equals.Equals(afterFailure, _current.Value))
                {
                    _current = Advance(afterFailure);
                    return Retain(new(request.RequestId, BridgeFieldWriteReceiptKind.PostApplyValidationFailed, _current,
                        error.Message, null));
                }
                return Retain(new(request.RequestId, BridgeFieldWriteReceiptKind.Rejected, _current,
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
                return Retain(new(request.RequestId, BridgeFieldWriteReceiptKind.Applied, _current,
                    null, _validate?.Invoke(_current.Value)));
            }
            catch (Exception error)
            {
                return Retain(new(request.RequestId, BridgeFieldWriteReceiptKind.PostApplyValidationFailed, _current,
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
            var actual = _read();
            if (!_equals.Equals(actual, value))
                throw new ArgumentException("The observed value does not match the authoritative field.", nameof(value));

            // A synchronous property-changed observer can re-enter while the
            // registry itself applies a write. That is acknowledgement of the
            // same write, not a second external version advance.
            if (_applying) return _current;
            _current = Advance(actual);
            return _current;
        }
    }

    internal BridgeFieldWriteStatus<T> Lookup(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _receipts.TryGetValue(requestId, out var receipt)
                ? new(requestId, BridgeFieldWriteStatusKind.Retained, receipt)
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
        }
    }

    private BridgeFieldSnapshot<T> Advance(T value) => new(value, checked(_current.Version + 1));

    private TReturn InTurn<TReturn>(Func<TReturn> work) => _modelTurn is null ? work() : _modelTurn.Run(work);

    private BridgeFieldWriteReceipt<T> Retain(BridgeFieldWriteReceipt<T> receipt)
    {
        _receipts.Add(receipt.RequestId, receipt);
        _receiptOrder.Enqueue(receipt.RequestId);
        while (_receipts.Count > _maximumRetainedWrites)
        {
            var expired = _receiptOrder.Dequeue();
            _receipts.Remove(expired);
            RetainExpiredRequestId(expired);
        }
        return receipt;
    }

    // Retaining a finite tombstone prevents an immediate late retry from
    // replaying a rejected or otherwise evicted stable request ID. Once this
    // bounded history expires too, Lookup is unknown and the caller must
    // reconcile before treating a retry as new; this is not exactly-once.
    private void RetainExpiredRequestId(string requestId)
    {
        if (!_expiredRequestIds.Add(requestId)) return;
        _expiredRequestOrder.Enqueue(requestId);
        while (_expiredRequestIds.Count > _maximumRetainedWrites)
            _expiredRequestIds.Remove(_expiredRequestOrder.Dequeue());
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BridgeFieldWriteRegistry<T>));
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
