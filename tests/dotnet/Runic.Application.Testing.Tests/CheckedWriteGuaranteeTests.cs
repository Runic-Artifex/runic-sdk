using System.Globalization;
using System.Text;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// These tests cover protocol properties of the registry itself. Runtime JSON
// parsing and generated-client receipt decoding are intentionally exercised by
// the bridge and generated-client suites.
internal sealed class CheckedWriteGuaranteeTests
{
    [Test]
    public void SameIdReplaysOnlyAnIdenticalRequest()
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

    [Test]
    public void CanonicalIdentityDoesNotRetainMutableRequestValues()
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

    [Test]
    public void ReceiptRetentionIsBoundedByBytesAsWellAsCount()
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

    [Test]
    public void OversizedReceiptLeavesAnExpiryTombstone()
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

    // text-splice/1: a splice write retains its expectedVersion and splice, and a retry must repeat that form.
    [Test]
    public void ASpliceWriteReplaysOnlyTheSameSplice()
    {
        var value = new string('a', 100);
        var applies = 0;
        using var registry = new BridgeFieldWriteRegistry<string>("owner", "text", () => value, next =>
        {
            applies++;
            value = next;
        }, canonicalize: static text => text, retainedValueByteCount: static text => Encoding.UTF8.GetByteCount(text));
        static (bool, string) Append(string current) => (true, current + "!");

        var first = registry.ApplySplice("splice", 0, "100:100:0:!", Append);
        Require(first is { Kind: BridgeFieldWriteReceiptKind.Applied } && first.Current.Value == new string('a', 100) + "!" && applies == 1,
            "A splice write was not applied as the full write of its rebuilt value.");
        var replay = registry.ApplySplice("splice", 0, "100:100:0:!", Append);
        Require(replay is { Kind: BridgeFieldWriteReceiptKind.Applied } && replay.Current == first.Current && applies == 1,
            "A splice-write retry did not replay its original receipt.");
        Require(registry.ApplySplice("splice", 0, "100:100:0:?", Append)?.Kind == BridgeFieldWriteReceiptKind.Conflict
            && registry.ApplySplice("splice", 1, "100:100:0:!", Append)?.Kind == BridgeFieldWriteReceiptKind.Conflict,
            "Reusing a splice write's request ID with another splice or version did not conflict.");
        RequireDifferentPayload(registry.Apply(new("splice", 0, new string('a', 100), new string('a', 100) + "!")),
            "A full write repeated a splice write's request ID.");

        // A full write's request ID does not replay for a splice write of the same change.
        _ = registry.Apply(new("full", 1, value, value + "!"));
        RequireDifferentPayload(registry.ApplySplice("full", 1, "101:101:0:!", Append)!, "A splice write repeated a full write's request ID.");
        Require(applies == 2, "A reused request ID wrote.");
    }

    [Test]
    public void ASpliceThatDoesNotFitItsBaseRetainsNothing()
    {
        var value = "base";
        var applies = 0;
        using var registry = new BridgeFieldWriteRegistry<string>("owner", "text", () => value, next =>
        {
            applies++;
            value = next;
        }, canonicalize: static text => text, retainedValueByteCount: static text => Encoding.UTF8.GetByteCount(text));

        // The version is checked before the base: a stale splice write is a retained conflict.
        Require(registry.ApplySplice("stale", 3, "9:0:0:x", static current => (false, current))?.Kind == BridgeFieldWriteReceiptKind.Conflict
            && registry.Lookup("stale").Kind == BridgeFieldWriteStatusKind.Retained,
            "A splice write of another version was not a retained conflict.");
        Require(registry.ApplySplice("misfit", 0, "9:0:0:x", static current => (false, current)) is null,
            "A splice that does not fit its base returned a receipt.");
        Require(registry.Lookup("misfit").Kind == BridgeFieldWriteStatusKind.Unknown && applies == 0,
            "A splice that does not fit its base was retained or written.");
        Require(registry.Apply(new("misfit", 0, "base", "based")).Kind == BridgeFieldWriteReceiptKind.Applied && value == "based",
            "The request ID of a splice that did not fit was not free for the full write.");
    }

    private static void RequireDifferentPayload<T>(BridgeFieldWriteReceipt<T> receipt, string message) => Require(
        receipt.Kind == BridgeFieldWriteReceiptKind.Conflict
            && receipt.Message?.Contains("different checked-write payload", StringComparison.Ordinal) == true, message);

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
