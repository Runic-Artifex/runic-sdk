using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Runic.Application.Views;

// field-paths/1 (specs/application/README.md, "Field paths"): the producer side of checked writes below a member.
//
// A container is the value a path write copies: a settable member's object (key "") or a keyed collection's row (its
// key). A path's version is the revision of the route's last publication that changed the value at the path. The
// tracker keeps, per member and container, a hash of each declared path's value and of the whole container as the
// last publication showed them, with the revision at which each last changed, so a write based on an older state of
// the path is a conflict even when the value changed back. Containers seen in the member's first scan start at
// version 0. A publication observes only the rows its indexed changes carry; sets and full states scan the member.
internal sealed class BridgeFieldPathTracker
{
    private readonly Dictionary<string, MemberState> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RetainedReceipt> _receipts = new(StringComparer.Ordinal);
    private readonly Queue<string> _receiptOrder = new();
    private readonly HashSet<string> _expired = new(StringComparer.Ordinal);
    private readonly Queue<string> _expiredOrder = new();
    private int _retainedBytes;

    internal const int MaximumRetainedReceipts = 64;
    internal const int MaximumRetainedBytes = 262_144;

    // Every container of a member after a set or a full state; a container missing from it was removed.
    internal void Scan(string member, IEnumerable<(string Key, byte[] Encoded)> containers, IReadOnlyList<string[]> paths, long revision)
    {
        var state = State(member);
        var initial = !state.Scanned;
        state.Scanned = true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, encoded) in containers)
        {
            seen.Add(key);
            Observe(state, key, encoded, paths, initial ? 0 : revision);
        }
        state.Containers.Keys.Where(key => !seen.Contains(key)).ToList().ForEach(key => state.Containers.Remove(key));
    }

    // One container that a publication added or replaced, from its published encoding.
    internal void Observe(string member, string key, byte[] encoded, IReadOnlyList<string[]> paths, long revision)
    {
        var state = State(member);
        Observe(state, key, encoded, paths, state.Scanned ? revision : 0);
    }

    // A row that a publication removed: a row added again later under its key starts at its new revision.
    internal void Remove(string member, string key)
    {
        if (_members.TryGetValue(member, out var state)) state.Containers.Remove(key);
    }

    private MemberState State(string member)
    {
        if (!_members.TryGetValue(member, out var state)) _members[member] = state = new();
        return state;
    }

    private static void Observe(MemberState state, string key, byte[] encoded, IReadOnlyList<string[]> paths, long revision)
    {
        var rowHash = Hash(encoded);
        if (state.Containers.TryGetValue(key, out var container) && container.RowHash == rowHash) return;
        var hashes = new ulong[paths.Count];
        try
        {
            using var document = JsonDocument.Parse(encoded);
            for (var index = 0; index < paths.Count; index++)
                hashes[index] = LeafText(document.RootElement, paths[index]) is { } text ? Hash(Encoding.UTF8.GetBytes(text)) : 0;
        }
        catch (JsonException) { container = null; }
        if (container is null)
        {
            state.Containers[key] = new(revision, rowHash, hashes);
            return;
        }
        for (var index = 0; index < paths.Count; index++)
            if (container.Hashes[index] != hashes[index])
            {
                container.Hashes[index] = hashes[index];
                container.Changed[index] = revision;
            }
        container.RowHash = rowHash;
        container.RowChanged = revision;
    }

    // The path's version: the revision of the last publication that changed it, 0 when none did since the member's
    // first scan.
    internal long VersionOf(string member, string key, int path)
    {
        if (!_members.TryGetValue(member, out var state) || !state.Containers.TryGetValue(key, out var container)) return 0;
        return Math.Max(container.Added, container.Changed[path]);
    }

    // The row's version (RunicRowConflicts.Row): the revision of the last publication that changed anything in it.
    internal long RowVersionOf(string member, string key)
    {
        if (!_members.TryGetValue(member, out var state) || !state.Containers.TryGetValue(key, out var container)) return 0;
        return Math.Max(container.Added, container.RowChanged);
    }

    // The number of containers the tracker keeps for a member; for tests.
    internal int Count(string member) => _members.TryGetValue(member, out var state) ? state.Containers.Count : 0;

    private static ulong Hash(ReadOnlySpan<byte> bytes) => BitConverter.ToUInt64(SHA256.HashData(bytes));

    // A retained receipt for requestId: the original receipt when identity matches, a conflict otherwise. False when
    // the request id is new.
    internal bool TryGetRetained(string requestId, string identity, out BridgeFieldPathReceipt? receipt, out bool matches)
    {
        var digest = Digest(requestId);
        if (_receipts.TryGetValue(digest, out var retained))
        {
            matches = retained.Identity == Digest(identity);
            receipt = matches ? retained.Receipt : null;
            return true;
        }
        matches = false;
        receipt = null;
        return _expired.Contains(digest);
    }

    internal void Retain(string requestId, string identity, BridgeFieldPathReceipt receipt)
    {
        var digest = Digest(requestId);
        if (_receipts.ContainsKey(digest)) return;
        var bytes = 160 + (receipt.Value?.Length ?? 0) + (receipt.Message?.Length ?? 0) * 2;
        if (bytes > MaximumRetainedBytes)
        {
            Expire(digest);
            return;
        }
        _receipts[digest] = new(Digest(identity), receipt, bytes);
        _receiptOrder.Enqueue(digest);
        _retainedBytes += bytes;
        while (_receipts.Count > MaximumRetainedReceipts || _retainedBytes > MaximumRetainedBytes)
        {
            var oldest = _receiptOrder.Dequeue();
            if (!_receipts.Remove(oldest, out var evicted)) continue;
            _retainedBytes -= evicted.Bytes;
            Expire(oldest);
        }
    }

    private void Expire(string digest)
    {
        if (!_expired.Add(digest)) return;
        _expiredOrder.Enqueue(digest);
        while (_expired.Count > MaximumRetainedReceipts) _expired.Remove(_expiredOrder.Dequeue());
    }

    // The canonical text of the value at path in an encoded container; null when the path does not reach a value
    // (a member is missing, or an object on the way is null or not an object).
    internal static string? LeafText(JsonElement container, string[] path)
    {
        var value = container;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out var next)) return null;
            value = next;
        }
        return BridgeCanonicalJson.ToString(Encoding.UTF8.GetBytes(value.GetRawText()));
    }

    internal static string Canonical(JsonElement value) =>
        BridgeCanonicalJson.ToString(Encoding.UTF8.GetBytes(value.GetRawText()));

    internal static string PathKey(string[] path) => string.Join('\0', path);

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class MemberState
    {
        public bool Scanned { get; set; }
        public Dictionary<string, Container> Containers { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Container(long added, ulong rowHash, ulong[] hashes)
    {
        public long Added { get; } = added;
        public ulong RowHash { get; set; } = rowHash;
        public long RowChanged { get; set; } = added;
        public ulong[] Hashes { get; } = hashes;
        public long[] Changed { get; } = new long[hashes.Length];
    }

    private sealed record RetainedReceipt(string Identity, BridgeFieldPathReceipt Receipt, int Bytes);
}

// The receipt of a path write. Value is the canonical JSON text of the value at the path (snapshot or incoming).
internal sealed record BridgeFieldPathReceipt(string Kind, string? Value, long Version, string? Message)
{
    internal void Write(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("receipt");
        writer.WriteStartObject();
        writer.WriteString("kind", Kind);
        if (Value is not null)
        {
            writer.WritePropertyName(Kind == "conflict" ? "incoming" : "snapshot");
            writer.WriteStartObject();
            writer.WritePropertyName("value");
            writer.WriteRawValue(Value, skipInputValidation: true);
            writer.WriteNumber("version", Version);
            writer.WriteEndObject();
        }
        if (Message is not null) writer.WriteString("message", Message);
        writer.WriteEndObject();
    }
}
