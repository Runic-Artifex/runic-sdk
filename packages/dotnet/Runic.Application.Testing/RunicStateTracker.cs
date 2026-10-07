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
                if (frame.Revision <= Revision) return;
                _state = JsonNode.Parse(frame.Json)!.AsObject();
                FullStates++;
                break;
            case RunicPublicationKind.Delta:
                if (frame.Revision <= Revision) return;
                if (frame.BaseRevision != Revision)
                    throw new InvalidOperationException(
                        $"A delta for '{_driver.Route}' applies to revision {frame.BaseRevision}, but the tracked state is at {Revision}: {frame.Json}");
                var next = _state.DeepClone().AsObject();
                foreach (var change in frame.Changes) ApplyChange(next, change, frame);
                next["revision"] = frame.Revision;
                _state = next;
                _changes.AddRange(frame.Changes);
                Deltas++;
                break;
        }
        State = new(Element(_state));
    }

    private void ApplyChange(JsonObject state, RunicCollectionChange change, RunicViewPublication frame)
    {
        if (!_keys.TryGetValue(change.Field, out var key))
            throw new InvalidOperationException($"{typeof(TModel).Name}.{change.Field} is not a [RunicCollection], but a delta changed it: {frame.Json}");
        if (state[change.Field] is not JsonArray rows)
            throw new InvalidOperationException($"The tracked state has no collection '{change.Field}'.");
        void RequireKeys(int start)
        {
            for (var offset = 0; offset < change.Keys.Count; offset++)
            {
                var at = start + offset;
                var actual = at < rows.Count && rows[at] is JsonObject row && row[key] is { } value
                    ? value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString()
                    : null;
                if (actual != change.Keys[offset])
                    throw new InvalidOperationException(
                        $"A {change.Kind} of {change.Field} expects key '{change.Keys[offset]}' at row {at}, but the tracked row has '{actual}': {frame.Json}");
            }
        }
        var items = change.Items.Select(item => JsonNode.Parse(item.GetRawText())).ToArray();
        switch (change.Kind)
        {
            case "add":
                for (var offset = 0; offset < items.Length; offset++) rows.Insert(change.Index + offset, items[offset]);
                break;
            case "remove":
                RequireKeys(change.Index);
                for (var count = 0; count < change.Keys.Count; count++) rows.RemoveAt(change.Index);
                break;
            case "replace":
                RequireKeys(change.Index);
                for (var offset = 0; offset < items.Length; offset++) rows[change.Index + offset] = items[offset];
                break;
            case "move":
                RequireKeys(change.OldIndex);
                var moved = Enumerable.Range(0, change.Keys.Count).Select(_ =>
                {
                    var row = rows[change.OldIndex];
                    rows.RemoveAt(change.OldIndex);
                    return row;
                }).ToArray();
                for (var offset = 0; offset < moved.Length; offset++) rows.Insert(change.Index + offset, moved[offset]);
                break;
            default:
                throw new InvalidOperationException($"Unknown collection change '{change.Kind}': {frame.Json}");
        }
    }
}
