using System.Text.Json;

namespace Runic.Application.Testing;

/// <summary>What a pushed frame carries. The set is closed: it gains values only in a major release.</summary>
public enum RunicPublicationKind
{
    /// <summary>A full state.</summary>
    State,
    /// <summary>Changes against the previous revision: member sets, keyed collection changes and appended text.</summary>
    Frame,
    /// <summary>A notice that .NET cannot publish the route's state for now.</summary>
    Failure,
}

/// <summary>
/// The edit a <see cref="RunicCollectionChange"/> makes, named as in
/// <see cref="System.Collections.Specialized.NotifyCollectionChangedAction"/>. The set is closed: it gains values
/// only in a major release.
/// </summary>
public enum RunicCollectionChangeKind
{
    /// <summary>Rows are inserted at <see cref="RunicCollectionChange.Index"/> (wire <c>add</c>).</summary>
    Add,
    /// <summary>Rows are removed at <see cref="RunicCollectionChange.Index"/> (wire <c>remove</c>).</summary>
    Remove,
    /// <summary>Rows at <see cref="RunicCollectionChange.Index"/> are replaced (wire <c>replace</c>).</summary>
    Replace,
    /// <summary>Rows move from <see cref="RunicCollectionChange.OldIndex"/> to <see cref="RunicCollectionChange.Index"/> (wire <c>move</c>).</summary>
    Move,
}

/// <summary>One keyed collection edit of a delta frame.</summary>
/// <param name="Field">The collection's wire name.</param>
/// <param name="Kind">The edit.</param>
/// <param name="Index">Where the rows are added, removed, replaced or moved to.</param>
/// <param name="OldIndex">Where moved or replaced rows were.</param>
/// <param name="Keys">The keys of the added rows, or of the rows before the edit.</param>
/// <param name="Items">The added or replacement rows.</param>
public sealed record RunicCollectionChange(string Field, RunicCollectionChangeKind Kind, int Index, int OldIndex,
    IReadOnlyList<string> Keys, IReadOnlyList<JsonElement> Items)
{
    internal static RunicCollectionChangeKind ParseKind(string? kind, string route) => kind switch
    {
        "add" => RunicCollectionChangeKind.Add,
        "remove" => RunicCollectionChangeKind.Remove,
        "replace" => RunicCollectionChangeKind.Replace,
        "move" => RunicCollectionChangeKind.Move,
        _ => throw new InvalidOperationException(
            $"The client cannot apply a frame of '{route}': '{kind}' is not a collection change."),
    };
}

/// <summary>A member's new value in a frame: a <c>set</c> change.</summary>
/// <param name="Field">The member's wire name.</param>
/// <param name="Value">The member's whole value, encoded as in a full state.</param>
/// <param name="Version">The checked field's version after the change, or <see langword="null"/> for another member.</param>
public sealed record RunicMemberSet(string Field, JsonElement Value, long? Version);

/// <summary>Text appended to an append-only text field in a frame: a <c>text</c> change.</summary>
/// <param name="Field">The text field's wire name.</param>
/// <param name="Length">The length, in UTF-16 code units, of the value the change applies to.</param>
/// <param name="Trim">The number of UTF-16 code units dropped from the start.</param>
/// <param name="Text">The text appended after them.</param>
public sealed record RunicTextChange(string Field, int Length, int Trim, string Text);

/// <summary>One push of a route's state: a full state, a frame of changes, or a failure notice.</summary>
public sealed class RunicViewPublication
{
    internal RunicViewPublication(string route, string json)
    {
        Route = route;
        Json = json;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Revision = root.TryGetProperty("revision", out var revision) && revision.ValueKind == JsonValueKind.Number ? revision.GetInt64() : 0;
        switch (root.GetProperty("kind").GetString())
        {
            case "failure":
                Kind = RunicPublicationKind.Failure;
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    ErrorKind = error.TryGetProperty("kind", out var kind) ? kind.GetString() : null;
                    ErrorMessage = error.TryGetProperty("message", out var message) ? message.GetString() : null;
                }
                break;
            case "frame":
                Kind = RunicPublicationKind.Frame;
                BaseRevision = root.GetProperty("baseRevision").GetInt64();
                List<RunicCollectionChange> changes = [];
                List<RunicMemberSet> sets = [];
                List<RunicTextChange> texts = [];
                foreach (var change in root.GetProperty("changes").EnumerateArray())
                {
                    var field = change.GetProperty("field").GetString()!;
                    switch (change.GetProperty("kind").GetString())
                    {
                        case "set":
                            sets.Add(new(field, change.GetProperty("value").Clone(),
                                change.TryGetProperty("version", out var version) ? version.GetInt64() : null));
                            break;
                        case "text":
                            texts.Add(new(field, change.GetProperty("length").GetInt32(), change.GetProperty("trim").GetInt32(),
                                change.GetProperty("text").GetString()!));
                            break;
                        default:
                            changes.Add(new(field, RunicCollectionChange.ParseKind(change.GetProperty("kind").GetString(), route),
                                change.GetProperty("index").GetInt32(), change.GetProperty("oldIndex").GetInt32(),
                                change.GetProperty("keys").EnumerateArray().Select(key => key.GetString()!).ToArray(),
                                change.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray()));
                            break;
                    }
                }
                Changes = changes;
                Sets = sets;
                Texts = texts;
                break;
            default:
                Kind = RunicPublicationKind.State;
                State = root.GetProperty("state").Clone();
                if (root.TryGetProperty("fields", out var fields)) Fields = fields.Clone();
                break;
        }
    }

    // The route of a state, frame or failure push, or null for another message.
    internal static string? RouteOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return root.TryGetProperty("kind", out var kind) && kind.GetString() is "state" or "frame" or "failure"
            && root.TryGetProperty("route", out var route) ? route.GetString() : null;
    }

    /// <summary>The route.</summary>
    public string Route { get; }
    /// <summary>The push as JSON.</summary>
    public string Json { get; }
    /// <summary>A full state, a frame or a failure notice.</summary>
    public RunicPublicationKind Kind { get; }
    /// <summary>The revision of the state or frame; for a failure, the producer's current revision.</summary>
    public long Revision { get; }
    /// <summary>The revision a frame applies to.</summary>
    public long? BaseRevision { get; }
    /// <summary>A full state's members.</summary>
    public JsonElement? State { get; }
    /// <summary>A full state's checked field versions, <c>{member: {version}}</c>, when the View has checked fields.</summary>
    public JsonElement? Fields { get; }
    /// <summary>A frame's keyed collection changes, in order.</summary>
    public IReadOnlyList<RunicCollectionChange> Changes { get; } = [];
    /// <summary>A frame's member <c>set</c> changes.</summary>
    public IReadOnlyList<RunicMemberSet> Sets { get; } = [];
    /// <summary>A frame's text changes.</summary>
    public IReadOnlyList<RunicTextChange> Texts { get; } = [];
    /// <summary>A failure notice's error kind.</summary>
    public string? ErrorKind { get; }
    /// <summary>A failure notice's message.</summary>
    public string? ErrorMessage { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Route} {Kind} r{Revision}: {Json}";
}

// Sorts transport publications by route, so each driver and tracker sees its
// own frames in order no matter which one takes the transport's frames.
internal sealed class RunicPublicationLog(InMemoryBridgeTransport transport, TimeSpan timeout)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<string>> _routes = new(StringComparer.Ordinal);

    internal TimeSpan Timeout { get; } = timeout;

    internal IReadOnlyList<RunicViewPublication> Take(string route)
    {
        lock (_gate)
        {
            Collect();
            if (!_routes.TryGetValue(route, out var queue) || queue.Count == 0) return [];
            var frames = queue.ToArray();
            queue.Clear();
            return frames.Select(json => new RunicViewPublication(route, json)).ToArray();
        }
    }

    internal async Task<RunicViewPublication> NextAsync(string route, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? Timeout);
        while (true)
        {
            Task next;
            lock (_gate)
            {
                next = transport.NextPublication;
                Collect();
                if (_routes.TryGetValue(route, out var queue) && queue.Count > 0) return new(route, queue.Dequeue());
            }
            try { await next.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Route '{route}' published nothing within {(timeout ?? Timeout).TotalSeconds:0.###} s.");
            }
        }
    }

    private void Collect()
    {
        foreach (var (route, json) in transport.TakeFrames())
        {
            if (!_routes.TryGetValue(route, out var queue)) _routes.Add(route, queue = new());
            queue.Enqueue(json);
        }
    }
}
