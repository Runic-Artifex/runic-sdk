using System.Text.Json;

namespace Runic.Application.Testing;

/// <summary>What a pushed frame carries.</summary>
public enum RunicPublicationKind
{
    /// <summary>A full state.</summary>
    State,
    /// <summary>Keyed collection changes against the previous revision.</summary>
    Delta,
    /// <summary>A notice that .NET cannot publish the route's state for now.</summary>
    Failure,
}

/// <summary>One keyed collection edit of a delta frame.</summary>
/// <param name="Field">The collection's wire name.</param>
/// <param name="Kind"><c>add</c>, <c>remove</c>, <c>replace</c> or <c>move</c>.</param>
/// <param name="Index">Where the rows are added, removed, replaced or moved to.</param>
/// <param name="OldIndex">Where moved or replaced rows were.</param>
/// <param name="Keys">The keys of the added rows, or of the rows before the edit.</param>
/// <param name="Items">The added or replacement rows.</param>
public sealed record RunicCollectionChange(string Field, string Kind, int Index, int OldIndex,
    IReadOnlyList<string> Keys, IReadOnlyList<JsonElement> Items);

/// <summary>A frame that .NET pushed to a View route, in the order the test transport observed it.</summary>
public sealed class RunicViewPublication
{
    internal RunicViewPublication(string route, string json)
    {
        Route = route;
        Json = json;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Revision = root.TryGetProperty("revision", out var revision) && revision.ValueKind == JsonValueKind.Number ? revision.GetInt64() : 0;
        if (root.TryGetProperty("__runicFailure", out _))
        {
            Kind = RunicPublicationKind.Failure;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                ErrorKind = error.TryGetProperty("kind", out var kind) ? kind.GetString() : null;
                ErrorMessage = error.TryGetProperty("message", out var message) ? message.GetString() : null;
            }
        }
        else if (root.TryGetProperty("__runicDelta", out _))
        {
            Kind = RunicPublicationKind.Delta;
            BaseRevision = root.GetProperty("baseRevision").GetInt64();
            Changes = root.GetProperty("changes").EnumerateArray().Select(change => new RunicCollectionChange(
                change.GetProperty("field").GetString()!, change.GetProperty("kind").GetString()!,
                change.GetProperty("index").GetInt32(), change.GetProperty("oldIndex").GetInt32(),
                change.GetProperty("keys").EnumerateArray().Select(key => key.GetString()!).ToArray(),
                change.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray())).ToArray();
        }
        else State = root.Clone();
    }

    /// <summary>The route the frame was pushed to, such as <c>shell</c> or <c>content1</c>.</summary>
    public string Route { get; }
    /// <summary>The frame as .NET serialized it.</summary>
    public string Json { get; }
    /// <summary>What the frame carries.</summary>
    public RunicPublicationKind Kind { get; }
    /// <summary>The revision of the state after this frame.</summary>
    public long Revision { get; }
    /// <summary>For a delta, the revision the changes apply to.</summary>
    public long? BaseRevision { get; }
    /// <summary>For a full state, the state.</summary>
    public JsonElement? State { get; }
    /// <summary>For a delta, its changes in order; otherwise empty.</summary>
    public IReadOnlyList<RunicCollectionChange> Changes { get; } = [];
    /// <summary>For a failure notice, its BridgeError kind.</summary>
    public string? ErrorKind { get; }
    /// <summary>For a failure notice, its message.</summary>
    public string? ErrorMessage { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Route} {Kind} r{Revision}: {Json}";
}

// Sorts transport publications by route, so each driver and tracker sees its
// own frames in order no matter which one drains the transport.
internal sealed class RunicPublicationLog(InMemoryViewTransport transport, TimeSpan timeout)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<RunicViewPublication>> _routes = new(StringComparer.Ordinal);

    internal TimeSpan Timeout { get; } = timeout;

    internal IReadOnlyList<RunicViewPublication> Take(string route)
    {
        lock (_gate)
        {
            Drain();
            if (!_routes.TryGetValue(route, out var queue) || queue.Count == 0) return [];
            var result = queue.ToArray();
            queue.Clear();
            return result;
        }
    }

    internal async ValueTask<RunicViewPublication> NextAsync(string route, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? Timeout);
        while (true)
        {
            Task next;
            lock (_gate)
            {
                next = transport.NextPublication;
                Drain();
                if (_routes.TryGetValue(route, out var queue) && queue.Count > 0) return queue.Dequeue();
            }
            try { await next.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Route '{route}' published nothing within {(timeout ?? Timeout).TotalSeconds:0.###} s.");
            }
        }
    }

    private void Drain()
    {
        foreach (var publication in transport.DrainPublications())
        {
            if (!_routes.TryGetValue(publication.Route, out var queue)) _routes.Add(publication.Route, queue = new());
            queue.Enqueue(new(publication.Route, publication.StateJson));
        }
    }
}
