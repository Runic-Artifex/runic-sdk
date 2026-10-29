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
    private JsonObject? _fields;
    private long _revision;

    internal RunicStateTracker(RunicViewDriver<TModel> driver, RunicPublicationLog publications)
    {
        _driver = driver;
        _publications = publications;
        // Frames queued before the snapshot are older than it.
        _ = publications.Take(driver.Route);
        var snapshot = driver.Snapshot();
        _state = JsonNode.Parse(snapshot.Json.GetRawText())!.AsObject();
        _fields = snapshot.Fields is { } fields ? JsonNode.Parse(fields.GetRawText())!.AsObject() : null;
        _revision = snapshot.Revision;
        State = new(Element(_state), snapshot.Revision, snapshot.Fields);
    }

    /// <summary>The tracked state.</summary>
    public RunicViewState<TModel> State { get; private set; }

    /// <summary>The revision of the tracked state.</summary>
    public long Revision => _revision;

    /// <summary>How many full states replaced the tracked state.</summary>
    public int FullStates { get; private set; }

    /// <summary>How many frames were applied.</summary>
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
    public async Task<RunicViewState<TModel>> WaitUntilAsync(Func<RunicViewState<TModel>, bool> predicate,
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
        var expected = JsonNode.Parse(_driver.Snapshot().Json.GetRawText())!.AsObject();
        var actual = _state.DeepClone().AsObject();
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

    private void Apply(RunicViewPublication frame)
    {
        switch (frame.Kind)
        {
            case RunicPublicationKind.Failure:
                _failures.Add(frame);
                return;
            case RunicPublicationKind.State:
            {
                // Like the client: a state at or below the current revision is stale or the same content.
                if (frame.Revision <= Revision) return;
                var state = JsonNode.Parse(frame.State!.Value.GetRawText())!.AsObject();
                foreach (var field in _keys.Keys) RequireUniqueKeys(state, field, frame);
                _state = state;
                _fields = frame.Fields is { } fields ? JsonNode.Parse(fields.GetRawText())!.AsObject() : null;
                _revision = frame.Revision;
                FullStates++;
                break;
            }
            case RunicPublicationKind.Frame:
            {
                if (frame.Revision <= Revision) return;
                if (frame.BaseRevision != Revision)
                    throw Invalid(frame, $"it applies to revision {frame.BaseRevision}, but the tracked state is at {Revision}; a frame was lost or reordered");
                var count = frame.Changes.Count + frame.Sets.Count + frame.Texts.Count + frame.Splices.Count;
                if (count is 0 or > 4096)
                    throw Invalid(frame, $"it has {count} changes; a frame has 1 to 4096");
                var fields = frame.Sets.Select(set => set.Field).Concat(frame.Changes.Select(change => change.Field))
                    .Concat(frame.Texts.Select(text => text.Field)).Concat(frame.Splices.Select(splice => splice.Field)).ToArray();
                foreach (var set in frame.Sets)
                    if (fields.Count(field => field == set.Field) != 1)
                        throw Invalid(frame, $"it has another change of '{set.Field}' beside its set");
                var next = _state.DeepClone().AsObject();
                var nextFields = _fields?.DeepClone().AsObject();
                foreach (var set in frame.Sets)
                {
                    if (!next.ContainsKey(set.Field)) throw Invalid(frame, $"it sets '{set.Field}', which is not a member of {typeof(TModel).Name}");
                    next[set.Field] = JsonNode.Parse(set.Value.GetRawText());
                    var isChecked = nextFields?.ContainsKey(set.Field) == true;
                    if (isChecked != set.Version.HasValue)
                        throw Invalid(frame, $"its set of '{set.Field}' {(isChecked ? "has no" : "has a")} version");
                    if (set.Version is { } version) nextFields![set.Field] = new JsonObject { ["version"] = version };
                    if (_keys.ContainsKey(set.Field)) RequireUniqueKeys(next, set.Field, frame);
                }
                foreach (var change in frame.Changes) ApplyChange(next, change, frame);
                foreach (var field in frame.Changes.Select(change => change.Field).Distinct()) RequireUniqueKeys(next, field, frame);
                foreach (var text in frame.Texts)
                {
                    if (next[text.Field] is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
                        throw Invalid(frame, $"its text change of '{text.Field}' applies to a value that is not a string");
                    var current = value.GetValue<string>();
                    if (current.Length != text.Length || text.Trim < 0 || text.Trim > text.Length)
                        throw Invalid(frame, $"its text change of '{text.Field}' applies to {text.Length} code units, but the tracked value has {current.Length}");
                    next[text.Field] = current[text.Trim..] + text.Text;
                }
                foreach (var splice in frame.Splices)
                {
                    if (next[splice.Field] is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
                        throw Invalid(frame, $"its splice of '{splice.Field}' applies to a value that is not a string");
                    var current = value.GetValue<string>();
                    if (!Runic.Application.Views.BridgeTextSplice.TryApply(current, splice.Length, splice.Index, splice.Delete, splice.Text, out var spliced))
                        throw Invalid(frame, $"its splice of '{splice.Field}' does not fit the tracked value of {current.Length} code units");
                    next[splice.Field] = spliced;
                    var isChecked = nextFields?.ContainsKey(splice.Field) == true;
                    if (isChecked != splice.Version.HasValue)
                        throw Invalid(frame, $"its splice of '{splice.Field}' {(isChecked ? "has no" : "has a")} version");
                    if (splice.Version is { } version) nextFields![splice.Field] = new JsonObject { ["version"] = version };
                }
                _state = next;
                _fields = nextFields;
                _revision = frame.Revision;
                _changes.AddRange(frame.Changes);
                Deltas++;
                break;
            }
        }
        State = new(Element(_state), _revision, _fields is null ? null : Element(_fields));
    }

    // The checks of the generated client's applyCollectionDelta, with messages that name the problem.
    private void ApplyChange(JsonObject state, RunicCollectionChange change, RunicViewPublication frame)
    {
        if (!_keys.TryGetValue(change.Field, out var key))
            throw Invalid(frame, $"it changes '{change.Field}', which is not a [RunicCollection] of {typeof(TModel).Name}");
        if (state[change.Field] is not JsonArray rows)
            throw Invalid(frame, $"the tracked state has no collection '{change.Field}'");
        var count = change.Keys.Count;
        var kind = change.Kind.ToString().ToLowerInvariant();
        if (count == 0 || change.Keys.Any(string.IsNullOrEmpty))
            throw Invalid(frame, $"a {kind} of {change.Field} names no keys or an empty key");
        void RequireIndex(int index, int limit, string what)
        {
            if (index < 0 || index > limit)
                throw Invalid(frame, $"a {kind} of {change.Field} has {what} {index}, outside 0 to {limit} for {rows.Count} rows");
        }
        void RequireItems(int expected)
        {
            if (change.Items.Count != expected)
                throw Invalid(frame, $"a {kind} of {change.Field} has {change.Items.Count} items for {count} keys; it needs {expected}");
        }
        void RequireKeys(int start)
        {
            RequireIndex(start, rows.Count - count, "index");
            for (var offset = 0; offset < count; offset++)
            {
                var actual = KeyOf(rows[start + offset], key);
                if (actual != change.Keys[offset])
                    throw Invalid(frame, $"a {kind} of {change.Field} expects key '{change.Keys[offset]}' at row {start + offset}, but the tracked row has '{actual}'");
            }
        }
        var items = change.Items.Select(item => JsonNode.Parse(item.GetRawText())).ToArray();
        switch (change.Kind)
        {
            case RunicCollectionChangeKind.Add:
                RequireIndex(change.Index, rows.Count, "index");
                RequireItems(count);
                for (var offset = 0; offset < count; offset++)
                    if (KeyOf(items[offset], key) != change.Keys[offset])
                        throw Invalid(frame, $"an added {change.Field} row has key '{KeyOf(items[offset], key)}', but the change names '{change.Keys[offset]}'");
                for (var offset = 0; offset < count; offset++) rows.Insert(change.Index + offset, items[offset]);
                break;
            case RunicCollectionChangeKind.Remove:
                RequireItems(0);
                RequireKeys(change.Index);
                for (var removed = 0; removed < count; removed++) rows.RemoveAt(change.Index);
                break;
            case RunicCollectionChangeKind.Replace:
                RequireItems(count);
                if (change.OldIndex != change.Index)
                    throw Invalid(frame, $"a replace of {change.Field} has index {change.Index} but oldIndex {change.OldIndex}");
                RequireKeys(change.Index);
                for (var offset = 0; offset < count; offset++) rows[change.Index + offset] = items[offset];
                break;
            case RunicCollectionChangeKind.Move:
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
