using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Runic.Application.Views;

// field-paths/1 (specs/application/README.md, "Field paths"): the producer side of checked writes below a member.
//
// A container is the value a path write rebuilds: a settable member's object (key "address") or a keyed collection's
// row (key "lines" + "\0" + row key). A path's version is the revision of the route's last publication that changed
// the value at the path. The tracker keeps, per container, the encoding it had in the last publication and the
// revision at which each of its field paths last changed, so a write based on an older state of the path is a
// conflict even when the value changed back. Containers seen in the route's first publication start at version 0.
internal sealed class BridgeFieldPathTracker
{
    private readonly Dictionary<string, Container> _containers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _scanned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RetainedReceipt> _receipts = new(StringComparer.Ordinal);
    private readonly Queue<string> _receiptOrder = new();
    private readonly HashSet<string> _expired = new(StringComparer.Ordinal);
    private readonly Queue<string> _expiredOrder = new();
    private int _retainedBytes;

    internal const int MaximumRetainedReceipts = 64;
    internal const int MaximumRetainedBytes = 262_144;

    internal static string RowContainer(string member, string key) => member + "\0" + key;

    // Records the containers of one member after a publication at revision. rows is every container of the member
    // with its encoding; a container missing from it was removed. The member's first scan sets no versions.
    internal void Observe(string member, IEnumerable<(string Container, byte[] Encoded)> containers,
        IReadOnlyList<string[]> paths, long revision)
    {
        var initial = _scanned.Add(member);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, encoded) in containers)
        {
            seen.Add(key);
            if (!_containers.TryGetValue(key, out var container))
            {
                _containers[key] = new(member, encoded, initial ? 0 : revision);
                continue;
            }
            if (container.Encoded.AsSpan().SequenceEqual(encoded)) continue;
            try
            {
                using var before = JsonDocument.Parse(container.Encoded);
                using var after = JsonDocument.Parse(encoded);
                foreach (var path in paths)
                    if (!string.Equals(LeafText(before.RootElement, path), LeafText(after.RootElement, path), StringComparison.Ordinal))
                        container.Changed[PathKey(path)] = revision;
            }
            catch (JsonException) { container.Added = revision; container.Changed.Clear(); }
            container.Encoded = encoded;
        }
        // A removed container is forgotten; a row added again later under its key starts at its new revision.
        List<string>? removed = null;
        foreach (var (key, container) in _containers)
            if (container.Member == member && !seen.Contains(key)) (removed ??= []).Add(key);
        if (removed is not null) foreach (var key in removed) _containers.Remove(key);
    }

    // The path's version: the revision of the last publication that changed it, 0 when none did since the route's
    // first publication.
    internal long VersionOf(string container, string[] path)
    {
        if (!_containers.TryGetValue(container, out var entry)) return 0;
        return Math.Max(entry.Added, entry.Changed.GetValueOrDefault(PathKey(path)));
    }

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

    // The container with the value at path replaced by value; null when the path does not reach a member of an
    // object.
    internal static byte[]? Rebuild(byte[] container, string[] path, JsonElement value)
    {
        if (JsonNode.Parse(container) is not JsonObject root) return null;
        var owner = root;
        for (var index = 0; index < path.Length - 1; index++)
        {
            if (!owner.TryGetPropertyValue(path[index], out var next) || next is not JsonObject nested) return null;
            owner = nested;
        }
        if (!owner.ContainsKey(path[^1])) return null;
        owner[path[^1]] = JsonNode.Parse(value.GetRawText());
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    internal static string PathKey(string[] path) => string.Join('\0', path);

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class Container(string member, byte[] encoded, long added)
    {
        public string Member { get; } = member;
        public byte[] Encoded { get; set; } = encoded;
        public long Added { get; set; } = added;
        public Dictionary<string, long> Changed { get; } = new(StringComparer.Ordinal);
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
