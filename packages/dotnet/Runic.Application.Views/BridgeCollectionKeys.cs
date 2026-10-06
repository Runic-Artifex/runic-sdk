using System.Collections;

namespace Runic.Application.Views;

// A [RunicCollection] row or key that generated clients reject. Message names
// the key for logs and development detail; ReplyMessage omits key values,
// because Bridge replies show it outside development.
internal sealed class BridgeCollectionKeyException : InvalidOperationException
{
    private const string Rule = ". [RunicCollection] keys must be nonempty and unique within the collection.";

    private BridgeCollectionKeyException(string model, string field, string? key, string problem, string replyProblem)
        : base($"{model}.{field}: {problem}{Rule}")
    {
        Field = field;
        Key = key;
        ReplyMessage = $"{model}.{field}: {replyProblem}{Rule}";
    }

    public string Field { get; }
    public string? Key { get; }
    public string ReplyMessage { get; }

    internal static BridgeCollectionKeyException Invalid(string model, string field, string problem) =>
        new(model, field, null, problem, problem);

    internal static BridgeCollectionKeyException Duplicate(string model, string field, string key, int first, int second) =>
        new(model, field, key, $"rows {first} and {second} have the duplicate key '{key}'", $"rows {first} and {second} have the same key");
}

// The keys of one collection instance as of the last full state and the
// notifications since. RowKeys remembers the key each row had when it was
// tracked, so a row whose key changed in place is detected and removed by the
// key it was added with.
internal sealed class BridgeCollectionKeyBaseline
{
    private readonly Dictionary<object, string> _rowKeys = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    private BridgeCollectionKeyBaseline(object rows) => Rows = rows;

    public object Rows { get; }

    // Checks every row of a collection. Throws BridgeCollectionKeyException.
    public static BridgeCollectionKeyBaseline Scan(string model, string field, IEnumerable rows, Func<object?, string> keyOf)
    {
        var baseline = new BridgeCollectionKeyBaseline(rows);
        var index = 0;
        foreach (var row in rows) baseline.Add(model, field, rows, keyOf, row, index++);
        return baseline;
    }

    public string? TrackedKey(object row) => _rowKeys.GetValueOrDefault(row);

    public void Remove(object? row, Func<object?, string> keyOf)
    {
        if (row is null) return;
        if (_rowKeys.Remove(row, out var key) || (key = keyOf(row)) is not null) _keys.Remove(key);
    }

    // Adds the row at index of rows. Throws BridgeCollectionKeyException.
    public void Add(string model, string field, IEnumerable rows, Func<object?, string> keyOf, object? row, int index)
    {
        var key = KeyOf(model, field, keyOf, row, index);
        if (_rowKeys.ContainsKey(row!) || !_keys.Add(key))
        {
            // Only a failure pays for finding the other row.
            var other = 0;
            foreach (var candidate in rows)
            {
                if (other != index && candidate is not null && keyOf(candidate) == key) break;
                other++;
            }
            throw BridgeCollectionKeyException.Duplicate(model, field, key, Math.Min(index, other), Math.Max(index, other));
        }
        _rowKeys[row!] = key;
    }

    // Returns the row's current key. Throws BridgeCollectionKeyException for a null row or a null or empty key.
    public static string KeyOf(string model, string field, Func<object?, string> keyOf, object? row, int index)
    {
        if (row is null) throw BridgeCollectionKeyException.Invalid(model, field, $"row {index} is null");
        var key = keyOf(row);
        if (key is null) throw BridgeCollectionKeyException.Invalid(model, field, $"row {index} has a null key");
        if (key.Length == 0) throw BridgeCollectionKeyException.Invalid(model, field, $"row {index} has an empty key");
        return key;
    }
}
