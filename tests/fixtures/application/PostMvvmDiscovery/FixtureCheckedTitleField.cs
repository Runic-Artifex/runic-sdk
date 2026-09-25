using System;
using System.Collections.Generic;

namespace Runic.Application.Bridge.PostMvvmFixture;

// Fixture-only one-string field owner. It keeps an acknowledged version and
// a bounded request ledger beside one generated attachment, without committing
// a general SDK field or form API. Retiring the attachment retires this owner:
// a later attachment starts from its then-current value with a fresh baseline.
internal sealed class FixtureCheckedTitleField
{
    private readonly object _gate = new();
    private readonly Func<string> _read;
    private readonly Action<string> _write;
    private readonly Dictionary<string, Entry> _receipts = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _receiptOrder = [];
    private readonly HashSet<string> _expired = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _expiredOrder = [];
    private readonly int _maximumReceipts;
    private readonly int _maximumExpired;
    private readonly string _generation = Guid.NewGuid().ToString("N");
    private string _observed;
    private long _version;

    internal FixtureCheckedTitleField(Func<string> read, Action<string> write,
        int maximumReceipts = 64, int maximumExpired = 128)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumReceipts);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumExpired);
        _maximumReceipts = maximumReceipts;
        _maximumExpired = maximumExpired;
        _observed = _read();
    }

    internal FixtureTitleSnapshot Snapshot()
    {
        lock (_gate) { Synchronize(); return Current(); }
    }

    // The ordinary generated setter remains request-free, but uses the same
    // owner gate as checked writes so it cannot interleave a compare-and-write.
    internal void SetDirect(string value)
    {
        lock (_gate)
        {
            Synchronize();
            _write(value);
            Synchronize();
        }
    }

    internal FixtureTitleReceipt Write(string? requestId, FixtureTitleBaseline? expected, string? value)
    {
        lock (_gate)
        {
            Synchronize();
            FixtureTitleSnapshot current = Current();
            if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128 || value is null || expected is null
                || !Guid.TryParseExact(expected.Generation, "N", out _))
                return FixtureTitleReceipt.Rejected(requestId ?? string.Empty, current, "The checked title request is invalid.");
            Shape shape = new(expected.Value, expected.Version, expected.Generation, value);
            if (_receipts.TryGetValue(requestId, out Entry? duplicate))
                return duplicate.Shape == shape ? duplicate.Receipt
                    : FixtureTitleReceipt.Rejected(requestId, current, "The title request identifier was already used.");
            if (_expired.Contains(requestId)) return FixtureTitleReceipt.Expired(requestId, current);
            if (expected.Generation != current.Generation || expected.Value != current.Value || expected.Version != current.Version)
                return Remember(requestId, shape, FixtureTitleReceipt.Conflict(requestId, current));
            try { _write(value); }
            catch (Exception error)
            {
                Synchronize();
                FixtureTitleSnapshot after = Current();
                return Remember(requestId, shape, after.Value != current.Value
                    ? FixtureTitleReceipt.CommittedWithError(requestId, after, error.Message)
                    : FixtureTitleReceipt.Rejected(requestId, after, error.Message));
            }
            Synchronize();
            return Remember(requestId, shape, FixtureTitleReceipt.Applied(requestId, Current()));
        }
    }

    private void Synchronize()
    {
        string actual = _read();
        if (actual == _observed) return;
        _observed = actual;
        _version++;
    }

    private FixtureTitleSnapshot Current() => new(_observed, _version, _generation);

    private FixtureTitleReceipt Remember(string requestId, Shape shape, FixtureTitleReceipt receipt)
    {
        _receipts.Add(requestId, new(shape, receipt));
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

    private sealed record Shape(string ExpectedValue, long ExpectedVersion, string ExpectedGeneration, string Value);
    private sealed record Entry(Shape Shape, FixtureTitleReceipt Receipt);
}

internal sealed record FixtureTitleSnapshot(string Value, long Version, string Generation);
internal sealed record FixtureTitleBaseline(string Value, long Version, string Generation)
{
    internal static FixtureTitleBaseline From(FixtureTitleSnapshot snapshot) =>
        new(snapshot.Value, snapshot.Version, snapshot.Generation);
}
internal enum FixtureTitleWriteKind { Applied, Conflict, Rejected, Expired, CommittedWithError }
internal sealed record FixtureTitleReceipt(string RequestId, FixtureTitleWriteKind Kind, FixtureTitleSnapshot Current, string? Error)
{
    internal static FixtureTitleReceipt Applied(string requestId, FixtureTitleSnapshot current) => new(requestId, FixtureTitleWriteKind.Applied, current, null);
    internal static FixtureTitleReceipt Conflict(string requestId, FixtureTitleSnapshot current) => new(requestId, FixtureTitleWriteKind.Conflict, current, null);
    internal static FixtureTitleReceipt Rejected(string requestId, FixtureTitleSnapshot current, string error) => new(requestId, FixtureTitleWriteKind.Rejected, current, error);
    internal static FixtureTitleReceipt Expired(string requestId, FixtureTitleSnapshot current) => new(requestId, FixtureTitleWriteKind.Expired, current, null);
    internal static FixtureTitleReceipt CommittedWithError(string requestId, FixtureTitleSnapshot current, string error) => new(requestId, FixtureTitleWriteKind.CommittedWithError, current, error);
}
