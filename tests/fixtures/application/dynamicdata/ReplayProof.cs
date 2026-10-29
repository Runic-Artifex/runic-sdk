using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using ReactiveUI.Primitives.Signals;
using Runic.Application.Views;

namespace DynamicDataExample;

// A cache bound with SortAndBind whose comparer changes: comparer changes and value updates move rows.
public sealed class SortProofViewModel : INotifyPropertyChanged, IDisposable
{
    internal static readonly IComparer<Row> ById = Comparer<Row>.Create((left, right) => left.Id.CompareTo(right.Id));
    internal static readonly IComparer<Row> ByValueDescending = Comparer<Row>.Create((left, right) =>
        right.Value != left.Value ? right.Value.CompareTo(left.Value) : left.Id.CompareTo(right.Id));
    internal static readonly IComparer<Row> ByLabel = Comparer<Row>.Create((left, right) =>
        string.CompareOrdinal(left.Label, right.Label));
    // Ranks a row outside the row itself: a refresh after a rank change moves the row (SortAndBind's Move).
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> Ranks = new();
    internal static readonly IComparer<Row> ByRank = Comparer<Row>.Create((left, right) =>
        Ranks.GetValueOrDefault(left.Id) != Ranks.GetValueOrDefault(right.Id)
            ? Ranks.GetValueOrDefault(left.Id).CompareTo(Ranks.GetValueOrDefault(right.Id)) : left.Id.CompareTo(right.Id));

    private readonly ObservableCollection<Row> _rows = [];
    private readonly StateSignal<IComparer<Row>> _comparer = new(ById);
    private readonly IDisposable _binding;

    public SortProofViewModel(SourceCache<Row, int> source, ISequencer sequencer)
    {
        Rows = new(_rows);
        _binding = source.Connect().ObserveOn(sequencer)
            .SortAndBind(_rows, _comparer, new SortAndBindOptions { ResetThreshold = int.MaxValue }).Subscribe();
    }

    [RunicCollection(nameof(Row.Id))]
    public ReadOnlyObservableCollection<Row> Rows { get; }

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    internal void SortBy(IComparer<Row> comparer) => _comparer.OnNext(comparer);

    public void Dispose()
    {
        _binding.Dispose();
        _comparer.Dispose();
    }
}

public sealed partial class SortProofWindow : RunicWindow<SortProofViewModel>;

// Applies a route's frames to its state as a client does (specs/application/state-changes.md, "Applying frames"), so a
// proof can require that the frames since a snapshot give the next snapshot.
internal static class FrameReplay
{
    // Applies the frames after the state's revision, up to and including revision; returns the state, the revision and
    // the number of frames and of each indexed change kind applied.
    internal static (JsonObject State, long Revision, int Frames, Dictionary<string, int> Kinds) Apply(
        JsonObject state, long revision, IEnumerable<string> pushes, string route, long until)
    {
        var frames = 0;
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var json in pushes)
        {
            using var document = JsonDocument.Parse(json);
            var push = document.RootElement;
            if (push.GetProperty("route").GetString() != route) continue;
            var pushRevision = push.GetProperty("revision").GetInt64();
            if (pushRevision <= revision || pushRevision > until) continue;
            if (push.GetProperty("kind").GetString() != "frame")
                throw new InvalidOperationException($"Route {route} published a {push.GetProperty("kind").GetString()} instead of a frame: {json}");
            if (push.GetProperty("baseRevision").GetInt64() != revision)
                throw new InvalidOperationException($"A frame of {route} does not continue revision {revision}: {json}");
            var next = state.DeepClone().AsObject();
            foreach (var change in push.GetProperty("changes").EnumerateArray())
            {
                var field = change.GetProperty("field").GetString()!;
                var kind = change.GetProperty("kind").GetString()!;
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                if (kind == "set") { next[field] = JsonNode.Parse(change.GetProperty("value").GetRawText()); continue; }
                var rows = next[field]!.AsArray();
                var index = change.GetProperty("index").GetInt32();
                var oldIndex = change.GetProperty("oldIndex").GetInt32();
                var keys = change.GetProperty("keys").EnumerateArray().Select(key => key.GetString()!).ToArray();
                var items = change.GetProperty("items").EnumerateArray().Select(item => JsonNode.Parse(item.GetRawText())!).ToArray();
                switch (kind)
                {
                    case "add":
                        for (var offset = 0; offset < items.Length; offset++) rows.Insert(index + offset, items[offset]);
                        break;
                    case "remove":
                        RequireKeys(rows, index, keys, json);
                        for (var offset = 0; offset < keys.Length; offset++) rows.RemoveAt(index);
                        break;
                    case "replace":
                        RequireKeys(rows, index, keys, json);
                        for (var offset = 0; offset < items.Length; offset++) rows[index + offset] = items[offset];
                        break;
                    case "move":
                        RequireKeys(rows, oldIndex, keys, json);
                        var moved = new List<JsonNode>();
                        for (var offset = 0; offset < keys.Length; offset++)
                        {
                            moved.Add(rows[oldIndex]!.DeepClone());
                            rows.RemoveAt(oldIndex);
                        }
                        for (var offset = 0; offset < moved.Count; offset++) rows.Insert(index + offset, moved[offset]);
                        break;
                    default:
                        throw new InvalidOperationException($"A frame of {route} has the change kind {kind}: {json}");
                }
            }
            state = next;
            revision = pushRevision;
            frames++;
        }
        return (state, revision, frames, kinds);
    }

    // The rows at the change's position have its keys (every row of these proofs has a numeric id key).
    private static void RequireKeys(JsonArray rows, int index, string[] keys, string json)
    {
        for (var offset = 0; offset < keys.Length; offset++)
            if (rows[index + offset]?["id"]?.ToJsonString() != keys[offset])
                throw new InvalidOperationException($"A change does not name the rows at its position: {json}");
    }
}
