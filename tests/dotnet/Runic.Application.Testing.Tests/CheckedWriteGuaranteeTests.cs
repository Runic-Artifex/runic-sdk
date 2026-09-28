using System.Globalization;
using System.Text;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// These tests cover protocol properties of the registry itself. Runtime JSON
// parsing and generated-client receipt decoding are intentionally exercised by
// the bridge and generated-client suites.
internal static class CheckedWriteGuaranteeTests
{
    internal static void Run()
    {
        SameIdReplaysOnlyAnIdenticalRequest();
        CanonicalIdentityDoesNotRetainMutableRequestValues();
        ReceiptRetentionIsBoundedByBytesAsWellAsCount();
        OversizedReceiptLeavesAnExpiryTombstone();
    }

    private static void SameIdReplaysOnlyAnIdenticalRequest()
    {
        var value = 0;
        var applies = 0;
        using var registry = new BridgeFieldWriteRegistry<int>("owner", "count", () => value, next =>
        {
            applies++;
            value = next;
        }, canonicalize: Canonical, retainedValueByteCount: static _ => 2);

        var original = new BridgeFieldWriteRequest<int>("same", 0, 0, 1);
        var first = registry.Apply(original);
        var replay = registry.Apply(original);
        Require(first.Kind == BridgeFieldWriteReceiptKind.Applied
            && replay.Kind == BridgeFieldWriteReceiptKind.Applied
            && replay.Current == first.Current
            && applies == 1,
            "An identical checked-write retry did not replay its original receipt.");

        RequireConflict(registry.Apply(original with { ExpectedVersion = 1 }), "expectedVersion");
        RequireConflict(registry.Apply(original with { ExpectedValue = 1 }), "expectedValue");
        RequireConflict(registry.Apply(original with { Value = 2 }), "value");
        Require(applies == 1 && value == 1,
            "A request-ID reuse with changed checked-write input invoked the setter.");
    }

    private static void CanonicalIdentityDoesNotRetainMutableRequestValues()
    {
        var value = new MutableNumber(0);
        var applies = 0;
        using var registry = new BridgeFieldWriteRegistry<MutableNumber>("owner", "value", () => value, next =>
        {
            applies++;
            value = next;
        }, snapshot: static item => new(item.Value), equalityComparer: MutableNumberComparer.Instance,
            canonicalize: static item => item.Value.ToString(CultureInfo.InvariantCulture),
            retainedValueByteCount: static _ => 2);

        var expected = new MutableNumber(0);
        var next = new MutableNumber(1);
        var original = new BridgeFieldWriteRequest<MutableNumber>("mutable", 0, expected, next);
        _ = registry.Apply(original);
        expected.Value = 99;
        next.Value = 99;

        var replay = registry.Apply(new("mutable", 0, new MutableNumber(0), new MutableNumber(1)));
        Require(replay.Kind == BridgeFieldWriteReceiptKind.Applied && applies == 1,
            "Canonical checked-write identity did not replay after callers mutated their request objects.");
    }

    private static void ReceiptRetentionIsBoundedByBytesAsWellAsCount()
    {
        var value = "0000000000";
        using var registry = new BridgeFieldWriteRegistry<string>("owner", "text", () => value, next => value = next,
            maximumRetainedWrites: 16, maximumRetainedReceiptBytes: 400,
            canonicalize: static text => text, retainedValueByteCount: static text => Encoding.UTF8.GetByteCount(text));

        var first = registry.Current;
        _ = registry.Apply(new("one", first.Version, first.Value, "1111111111"));
        var second = registry.Current;
        _ = registry.Apply(new("two", second.Version, second.Value, "2222222222"));
        var third = registry.Current;
        _ = registry.Apply(new("three", third.Version, third.Value, "3333333333"));

        Require(registry.Lookup("one").Kind == BridgeFieldWriteStatusKind.Unknown,
            "Receipt-byte retention did not evict the oldest receipt.");
        Require(registry.Lookup("three").Kind == BridgeFieldWriteStatusKind.Retained,
            "Receipt-byte retention evicted the newest receipt.");
    }

    private static void OversizedReceiptLeavesAnExpiryTombstone()
    {
        var value = new string('a', 128);
        var applies = 0;
        using var registry = new BridgeFieldWriteRegistry<string>("owner", "text", () => value, next =>
        {
            applies++;
            value = next;
        }, maximumRetainedReceiptBytes: 64, canonicalize: static text => text,
            retainedValueByteCount: static text => Encoding.UTF8.GetByteCount(text));
        var current = registry.Current;
        _ = registry.Apply(new("large", current.Version, current.Value, new string('b', 128)));

        var retry = registry.Apply(new("large", current.Version, current.Value, new string('b', 128)));
        Require(retry.Kind == BridgeFieldWriteReceiptKind.Conflict && applies == 1,
            "An oversized receipt was neither bounded nor protected by an expiry tombstone.");
    }

    private static string Canonical(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void RequireConflict(BridgeFieldWriteReceipt<int> receipt, string changedMember) => Require(
        receipt.Kind == BridgeFieldWriteReceiptKind.Conflict
            && receipt.Message?.Contains("different checked-write payload", StringComparison.Ordinal) == true,
        $"Reusing a request ID with a changed {changedMember} did not conflict.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class MutableNumber(int value)
    {
        internal int Value { get; set; } = value;
    }

    private sealed class MutableNumberComparer : IEqualityComparer<MutableNumber>
    {
        internal static MutableNumberComparer Instance { get; } = new();
        public bool Equals(MutableNumber? left, MutableNumber? right) => left?.Value == right?.Value;
        public int GetHashCode(MutableNumber value) => value.Value;
    }
}
