using System;
using System.Collections.Generic;

namespace Runic.Application.Bridge;

/// <summary>
/// Test-only owner for the fixed Notes title field used by the first window Bridge
/// fixture. One instance belongs to one shared model, even when that model is
/// exposed through several logical routes or presentations. All title mutation
/// must go through this owner: external model mutation does not advance its version.
/// The supplied delegates run synchronously under this field's gate. A setter that
/// changes the field and then throws is reported as a committed failure so callers
/// reconcile instead of replaying it. General MVVM setter reentrancy, external
/// model observation, business validation, and post-await model transactions
/// remain outside this fixed first slice.
/// </summary>
internal sealed class WindowBridgeCheckedTitleField
{
    private readonly object _gate = new();
    private readonly Func<string> _read;
    private readonly Action<string> _write;
    private readonly Dictionary<string, ReceiptEntry> _receipts = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _receiptOrder = [];
    private readonly HashSet<string> _expired = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _expiredOrder = [];
    private readonly int _maximumReceipts;
    private readonly int _maximumExpired;
    private readonly int _maximumRequestIdLength;
    private readonly int _maximumTitleLength;
    private long _version;

    internal WindowBridgeCheckedTitleField(Func<string> read, Action<string> write,
        int maximumReceipts = 64, int maximumExpired = 128, int maximumRequestIdLength = 128,
        int maximumTitleLength = 4 * 1024)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumReceipts);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumExpired);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRequestIdLength);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumTitleLength);
        _maximumReceipts = maximumReceipts;
        _maximumExpired = maximumExpired;
        _maximumRequestIdLength = maximumRequestIdLength;
        _maximumTitleLength = maximumTitleLength;
    }

    internal WindowBridgeTitleSnapshot Snapshot()
    {
        lock (_gate) return Current();
    }

    /// <summary>Applies an acknowledged direct title set in the same model turn as checked writes.</summary>
    internal WindowBridgeTitleReceipt Set(string? requestId, string? value) =>
        Apply(requestId, checkedWrite: false, expected: null, value);

    /// <summary>Applies a title only when its typed baseline still matches this owner's current version and value.</summary>
    internal WindowBridgeTitleReceipt WriteChecked(string? requestId, WindowBridgeTitleBaseline? expected, string? value) =>
        Apply(requestId, checkedWrite: true, expected, value);

    private WindowBridgeTitleReceipt Apply(string? requestId, bool checkedWrite, WindowBridgeTitleBaseline? expected, string? value)
    {
        lock (_gate)
        {
            WindowBridgeTitleSnapshot current = Current();
            if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > _maximumRequestIdLength)
                return WindowBridgeTitleReceipt.Rejected(requestId ?? string.Empty, current, "The field request identifier is invalid.");
            RequestShape shape = new(checkedWrite ? "checked" : "set", expected?.Version, expected?.Value, value);
            if (_receipts.TryGetValue(requestId, out ReceiptEntry? duplicate))
            {
                return duplicate.Shape == shape
                    ? duplicate.Receipt
                    : WindowBridgeTitleReceipt.Rejected(requestId, current, "The field request identifier was already used.");
            }
            if (_expired.Contains(requestId)) return WindowBridgeTitleReceipt.Expired(requestId, current);
            if (checkedWrite && expected is null)
                return WindowBridgeTitleReceipt.Rejected(requestId, current, "The checked title baseline is required.");
            if (value is null || value.Length > _maximumTitleLength || (expected is not null && (expected.Value is null || expected.Value.Length > _maximumTitleLength)))
                return WindowBridgeTitleReceipt.Rejected(requestId, current, "The title value is invalid.");
            if (expected is not null && (expected.Version != current.Version || expected.Value != current.Value))
                return Remember(requestId, shape, WindowBridgeTitleReceipt.Conflict(requestId, current));

            try
            {
                _write(value);
            }
            catch (Exception error)
            {
                // Setters may update their backing field before a later validation
                // or notification step throws. Re-read under the owner gate so the
                // receipt and version describe the authoritative field, then retain
                // that terminal outcome so a retry cannot invoke the setter again.
                WindowBridgeTitleSnapshot afterFailure = Current();
                if (afterFailure.Value != current.Value)
                {
                    _version++;
                    var committed = new WindowBridgeTitleSnapshot(afterFailure.Value, _version);
                    return Remember(requestId, shape,
                        WindowBridgeTitleReceipt.CommittedWithError(requestId, committed, error.Message));
                }

                return Remember(requestId, shape,
                    WindowBridgeTitleReceipt.Rejected(requestId, current, error.Message));
            }
            _version++;
            return Remember(requestId, shape, WindowBridgeTitleReceipt.Applied(requestId, Current()));
        }
    }

    private WindowBridgeTitleSnapshot Current() => new(_read(), _version);

    private WindowBridgeTitleReceipt Remember(string requestId, RequestShape shape, WindowBridgeTitleReceipt receipt)
    {
        _receipts.Add(requestId, new ReceiptEntry(shape, receipt));
        _receiptOrder.AddLast(requestId);
        while (_receiptOrder.Count > _maximumReceipts)
        {
            string expired = _receiptOrder.First!.Value;
            _receiptOrder.RemoveFirst();
            _receipts.Remove(expired);
            if (_maximumExpired == 0) continue;
            _expired.Add(expired);
            _expiredOrder.AddLast(expired);
            while (_expiredOrder.Count > _maximumExpired)
            {
                _expired.Remove(_expiredOrder.First!.Value);
                _expiredOrder.RemoveFirst();
            }
        }
        return receipt;
    }

    private sealed record RequestShape(string Operation, long? ExpectedVersion, string? ExpectedValue, string? Value);
    private sealed record ReceiptEntry(RequestShape Shape, WindowBridgeTitleReceipt Receipt);
}

internal sealed record WindowBridgeTitleSnapshot(string Value, long Version);

internal sealed record WindowBridgeTitleBaseline(long Version, string Value)
{
    internal static WindowBridgeTitleBaseline From(WindowBridgeTitleSnapshot snapshot) => new(snapshot.Version, snapshot.Value);
}

internal enum WindowBridgeTitleWriteKind { Applied, Conflict, Rejected, Expired, CommittedWithError }

internal sealed record WindowBridgeTitleReceipt(string RequestId, WindowBridgeTitleWriteKind Kind,
    WindowBridgeTitleSnapshot Current, string? Error)
{
    internal static WindowBridgeTitleReceipt Applied(string requestId, WindowBridgeTitleSnapshot current) => new(requestId, WindowBridgeTitleWriteKind.Applied, current, null);
    internal static WindowBridgeTitleReceipt Conflict(string requestId, WindowBridgeTitleSnapshot current) => new(requestId, WindowBridgeTitleWriteKind.Conflict, current, null);
    internal static WindowBridgeTitleReceipt Rejected(string requestId, WindowBridgeTitleSnapshot current, string error) => new(requestId, WindowBridgeTitleWriteKind.Rejected, current, error);
    internal static WindowBridgeTitleReceipt Expired(string requestId, WindowBridgeTitleSnapshot current) => new(requestId, WindowBridgeTitleWriteKind.Expired, current, null);
    internal static WindowBridgeTitleReceipt CommittedWithError(string requestId, WindowBridgeTitleSnapshot current, string error) => new(requestId, WindowBridgeTitleWriteKind.CommittedWithError, current, error);
}
