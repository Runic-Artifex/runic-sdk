using System.Text.Json;
using System.Text.Json.Nodes;

namespace Runic.Application.Testing;

/// <summary>
/// Follows one route's state the way the generated client does: it starts from a snapshot,
/// replaces it with newer full states, applies collection deltas against their base
/// revision while checking row keys, and records failure notices. A frame the client could
/// not apply throws, so a test fails where the browser would have had to recover.
/// </summary>
/// <remarks>
/// A tracker takes its route's frames from the host, so do not also call
/// <see cref="RunicViewDriver{TModel}.TakePublications"/> for the same route.
/// </remarks>
public sealed class RunicStateTracker<TModel> where TModel : class
{
    private readonly RunicViewDriver<TModel> _driver;
    private readonly RunicPublicationLog _publications;
    private readonly IReadOnlyDictionary<string, string> _keys = RunicMembers.CollectionKeys(typeof(TModel));
    private readonly List<RunicViewPublication> _failures = [];
    private readonly List<RunicCollectionChange> _changes = [];
    private JsonObject _state;

    internal RunicStateTracker(RunicViewDriver<TModel> driver, RunicPublicationLog publications)
    {
        _driver = driver;
        _publications = publications;
        // Frames queued before the snapshot are older than it.
        _ = publications.Take(driver.Route);
        _state = JsonNode.Parse(driver.Snapshot().Json.GetRawText())!.AsObject();
        State = new(Element(_state));
    }

    /// <summary>The tracked state.</summary>
    public RunicViewState<TModel> State { get; private set; }

    /// <summary>The revision of the tracked state.</summary>
    public long Revision => State.Revision;

    /// <summary>How many full states replaced the tracked state.</summary>
    public int FullStates { get; private set; }

    /// <summary>How many delta frames were applied.</summary>
    public int Deltas { get; private set; }

    /// <summary>The collection changes applied so far, in order.</summary>
    public IReadOnlyList<RunicCollectionChange> Changes => _changes;

    /// <summary>The failure notices received so far.</summary>
    public IReadOnlyList<RunicViewPublication> Failures => _failures;

    /// <summary>Applies the frames pushed since the last call and returns how many arrived.</summary>
    /// <exception cref="InvalidOperationException">A delta does not continue the tracked revision or its rows.</exception>
    public int Apply()
    {
        var frames = _publications.Take(_driver.Route);
        foreach (var frame in frames) Apply(frame);
        return frames.Count;
    }

    /// <summary>Applies frames as they arrive until <paramref name="predicate"/> holds for the tracked state.</summary>
    /// <param name="predicate">The condition to wait for.</param>
    /// <param name="timeout">Real time to wait; defaults to the host's publication timeout.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <exception cref="TimeoutException">The condition did not hold in time.</exception>
    public async ValueTask<RunicViewState<TModel>> WaitUntilAsync(Func<RunicViewState<TModel>, bool> predicate,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var limit = timeout ?? _publications.Timeout;
        var deadline = DateTime.UtcNow + limit;
        while (true)
        {
            Apply();
            if (predicate(State)) return State;
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Route '{_driver.Route}' did not reach the expected state within {limit.TotalSeconds:0.###} s. Last state: {State}");
            try { Apply(await _publications.NextAsync(_driver.Route, remaining, cancellationToken).ConfigureAwait(false)); }
            catch (TimeoutException) { }
            if (predicate(State)) return State;
        }
    }

    /// <summary>
    /// Checks that the tracked state equals a fresh snapshot, apart from revision and field
    /// versions: the frames converged on the state .NET holds.
    /// </summary>
    /// <exception cref="InvalidOperationException">They differ.</exception>
    public void Verify()
    {
        Apply();
        var expected = Comparable(JsonNode.Parse(_driver.Snapshot().Json.GetRawText())!.AsObject());
        var actual = Comparable(_state.DeepClone().AsObject());
        if (!JsonNode.DeepEquals(expected, actual))
            throw new InvalidOperationException(
                $"The frames of '{_driver.Route}' did not converge on its snapshot.{Environment.NewLine}" +
                $"Tracked:  {actual.ToJsonString()}{Environment.NewLine}Snapshot: {expected.ToJsonString()}");
    }

    private static JsonElement Element(JsonObject state)
    {
        using var document = JsonDocument.Parse(state.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonObject Comparable(JsonObject state)
    {
        state.Remove("revision");
        state.Remove("__runicFields");
        return state;
    }

    private void Apply(RunicViewPublication frame)
    {
        switch (frame.Kind)
        {
            case RunicPublicationKind.Failure:
                _failures.Add(frame);
                return;
            case RunicPublicationKind.State:
            {
                // Like the client: older states are stale, and a repeated state is ignored.
                if (frame.Revision < Revision) return;
                var state = JsonNode.Parse(frame.Json)!.AsObject();
                if (frame.Revision == Revision && JsonNode.DeepEquals(state, _state)) return;
                foreach (var field in _keys.Keys) RequireUniqueKeys(state, field, frame);
                _state = state;
                FullStates++;
                break;
            }
            case RunicPublicationKind.Delta:
            {
                if (frame.Revision <= Revision) return;
                if (frame.BaseRevision != Revision)
                    throw Invalid(frame, $"it applies to revision {frame.BaseRevision}, but the tracked state is at {Revision}; a frame was lost or reordered");
                if (frame.Changes.Count is 0 or > 4096)
                    throw Invalid(frame, $"it has {frame.Changes.Count} changes; a frame has 1 to 4096");
                var next = _state.DeepClone().AsObject();
                foreach (var change in frame.Changes) ApplyChange(next, change, frame);
                foreach (var field in frame.Changes.Select(change => change.Field).Distinct()) RequireUniqueKeys(next, field, frame);
                next["revision"] = frame.Revision;
                _state = next;
                _changes.AddRange(frame.Changes);
                Deltas++;
                break;
            }
        }
        State = new(Element(_state));
    }

    // The checks of the generated client's applyCollectionDelta, with messages that name the problem.
    private void ApplyChange(JsonObject state, RunicCollectionChange change, RunicViewPublication frame)
    {
        if (!_keys.TryGetValue(change.Field, out var key))
            throw Invalid(frame, $"it changes '{change.Field}', which is not a [RunicCollection] of {typeof(TModel).Name}");
        if (state[change.Field] is not JsonArray rows)
            throw Invalid(frame, $"the tracked state has no collection '{change.Field}'");
        var count = change.Keys.Count;
        if (count == 0 || change.Keys.Any(string.IsNullOrEmpty))
            throw Invalid(frame, $"a {change.Kind} of {change.Field} names no keys or an empty key");
        void RequireIndex(int index, int limit, string what)
        {
            if (index < 0 || index > limit)
                throw Invalid(frame, $"a {change.Kind} of {change.Field} has {what} {index}, outside 0 to {limit} for {rows.Count} rows");
        }
        void RequireItems(int expected)
        {
            if (change.Items.Count != expected)
                throw Invalid(frame, $"a {change.Kind} of {change.Field} has {change.Items.Count} items for {count} keys; it needs {expected}");
        }
        void RequireKeys(int start)
        {
            RequireIndex(start, rows.Count - count, "index");
            for (var offset = 0; offset < count; offset++)
            {
                var actual = KeyOf(rows[start + offset], key);
                if (actual != change.Keys[offset])
                    throw Invalid(frame, $"a {change.Kind} of {change.Field} expects key '{change.Keys[offset]}' at row {start + offset}, but the tracked row has '{actual}'");
            }
        }
        var items = change.Items.Select(item => JsonNode.Parse(item.GetRawText())).ToArray();
        switch (change.Kind)
        {
            case "add":
                RequireIndex(change.Index, rows.Count, "index");
                RequireItems(count);
                for (var offset = 0; offset < count; offset++)
                    if (KeyOf(items[offset], key) != change.Keys[offset])
                        throw Invalid(frame, $"an added {change.Field} row has key '{KeyOf(items[offset], key)}', but the change names '{change.Keys[offset]}'");
                for (var offset = 0; offset < count; offset++) rows.Insert(change.Index + offset, items[offset]);
                break;
            case "remove":
                RequireItems(0);
                RequireKeys(change.Index);
                for (var removed = 0; removed < count; removed++) rows.RemoveAt(change.Index);
                break;
            case "replace":
                RequireItems(count);
                if (change.OldIndex != change.Index)
                    throw Invalid(frame, $"a replace of {change.Field} has index {change.Index} but oldIndex {change.OldIndex}");
                RequireKeys(change.Index);
                for (var offset = 0; offset < count; offset++) rows[change.Index + offset] = items[offset];
                break;
            case "move":
                RequireItems(0);
                RequireKeys(change.OldIndex);
                var moved = Enumerable.Range(0, count).Select(_ =>
                {
                    var row = rows[change.OldIndex];
                    rows.RemoveAt(change.OldIndex);
                    return row;
                }).ToArray();
                RequireIndex(change.Index, rows.Count, "destination");
                for (var offset = 0; offset < count; offset++) rows.Insert(change.Index + offset, moved[offset]);
                break;
            default:
                throw Invalid(frame, $"'{change.Kind}' is not a collection change");
        }
    }

    private void RequireUniqueKeys(JsonObject state, string field, RunicViewPublication frame)
    {
        if (state[field] is not JsonArray rows) throw Invalid(frame, $"'{field}' is not an array");
        var key = _keys[field];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < rows.Count; index++)
        {
            var value = KeyOf(rows[index], key);
            if (string.IsNullOrEmpty(value)) throw Invalid(frame, $"{field} row {index} has no key");
            if (!seen.Add(value)) throw Invalid(frame, $"{field} rows have the duplicate key '{value}'");
        }
    }

    private static string? KeyOf(JsonNode? row, string key) =>
        row is JsonObject value && value[key] is { } field
            ? field.GetValueKind() == JsonValueKind.String ? field.GetValue<string>() : field.ToJsonString()
            : null;

    private InvalidOperationException Invalid(RunicViewPublication frame, string problem) =>
        new($"The client cannot apply a frame of '{_driver.Route}': {problem}. Frame: {(frame.Json.Length <= 2048 ? frame.Json : frame.Json[..2048] + "...")}");
}
