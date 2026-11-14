using System.ComponentModel;
using System.Text.Json;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// #887: a PropertyChanged reads the getters of the member it names, not of every bridged member; the unit it requests
// reads each member once. A replaced collection or observed sub-object is still picked up before the unit publishes.
internal sealed class NotificationCostTests
{
    private const string Route = "cost";
    private const int Burst = 20;

    // A burst of single-member notifications reads only the named members' getters, once per notification; the unit
    // reads each getter a constant number of times however long the burst was. The frame is the members' sets.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ABurstReadsOnlyTheNamedMembersAsync(bool reactive)
    {
        var model = Create(reactive);
        using var transport = new InMemoryBridgeTransport();
        using var bridge = Attach(transport, model);
        await SettleAsync(transport);

        // One notification per member, then a burst of Burst per member: the unit's reads must not grow with it.
        var single = await MeasureAsync(transport, model, notifications: 1);
        var burst = await MeasureAsync(transport, model, notifications: Burst);

        foreach (var member in model.Scalars)
        {
            // One read by the Bridge's refresh of the member, one by the data graph's (BridgeDataSubscriptions).
            Require(burst.During.GetValueOrDefault(member) <= Burst * 2,
                $"{member} was read {burst.During.GetValueOrDefault(member)} times by {Burst} notifications that named it.");
            Require(burst.Unit.GetValueOrDefault(member) == single.Unit.GetValueOrDefault(member),
                $"The unit after {Burst} notifications read {member} {burst.Unit.GetValueOrDefault(member)} times, after one {single.Unit.GetValueOrDefault(member)}.");
        }
        // A sub-object is read once more than a collection: by the Bridge's own collection check, by the data graph's
        // check for a replaced instance (#891) and when the state is written.
        foreach (var (member, reads) in (ValueTuple<string, int>[])[(nameof(model.Rows), 2), (nameof(model.Tags), 2), (nameof(model.SourceRows), 2),
                     (nameof(model.Detail), 3)])
        {
            Require(burst.During.GetValueOrDefault(member) == 0,
                $"{model.Scalars.Count * Burst} notifications that did not name {member} read it {burst.During.GetValueOrDefault(member)} times.");
            Require(burst.Unit.GetValueOrDefault(member) == single.Unit.GetValueOrDefault(member) && burst.Unit.GetValueOrDefault(member) <= reads,
                $"The unit after {Burst} notifications read {member} {burst.Unit.GetValueOrDefault(member)} times, after one {single.Unit.GetValueOrDefault(member)}.");
        }

        // The frame sets each changed member to its last value, in state order, and nothing else.
        var sets = Changes(burst.Frame).Select(change => (Field: change.GetProperty("field").GetString(), Kind: change.GetProperty("kind").GetString(),
            Value: change.GetProperty("value").GetString())).ToArray();
        var expected = model.Scalars.Select(member => ((string?)Camel(member), (string?)"set", (string?)$"{member}{Burst - 1}")).ToArray();
        Require(sets.SequenceEqual(expected), $"The burst's frame differs: {burst.Frame.RootElement.GetRawText()}");
        burst.Frame.Dispose();
        single.Frame.Dispose();
    }

    // A replaced keyed collection, list or collection read through another object is published whether its own member,
    // every member, another bridged member or a member the Bridge does not bridge was notified. Changes of the new
    // instance follow in each case: the unit that publishes it observes it too (#891).
    [Test]
    [Arguments(false, "self")]
    [Arguments(false, null)]
    [Arguments(false, "")]
    [Arguments(false, "Alpha")]
    [Arguments(false, "Unbridged")]
    [Arguments(true, "self")]
    [Arguments(true, "")]
    [Arguments(true, "Alpha")]
    [Arguments(true, "Unbridged")]
    public async Task AReplacedCollectionIsPickedUpAsync(bool reactive, string? notify)
    {
        var model = Create(reactive);
        using var transport = new InMemoryBridgeTransport();
        using var bridge = Attach(transport, model);
        await SettleAsync(transport);
        string? Name(string member) => notify == "self" ? member : notify;

        model.ReplaceRows([new("b", "two")], Name(nameof(model.Rows)));
        using (var replaced = await NextAsync(transport))
            Require(SetOf(replaced, "rows") is { } rows && rows.GetArrayLength() == 1 && rows[0].GetProperty("key").GetString() == "b",
                $"The replaced rows were not set: {replaced.RootElement.GetRawText()}");
        await RequireRowAddedAsync(transport, model.Rows, "rows");

        model.ReplaceTags(["x"], Name(nameof(model.Tags)));
        using (var replaced = await NextAsync(transport))
            Require(SetOf(replaced, "tags") is { } tags && tags.GetArrayLength() == 1 && tags[0].GetString() == "x",
                $"The replaced tags were not set: {replaced.RootElement.GetRawText()}");
        model.Tags.Add("y");
        using (var added = await NextAsync(transport))
            Require(SetOf(added, "tags") is { } tags && tags.GetArrayLength() == 2 && tags[1].GetString() == "y",
                $"A tag added to the new tags was not published: {added.RootElement.GetRawText()}");

        // SourceRows reads the rows of Source, which is not bridged: its own notification is never raised.
        model.ReplaceSource([new("t", "new")], notify == "self" ? "Source" : notify);
        using (var replaced = await NextAsync(transport))
            Require(SetOf(replaced, "sourceRows") is { } rows && rows.GetArrayLength() == 1 && rows[0].GetProperty("key").GetString() == "t",
                $"The replaced source rows were not set: {replaced.RootElement.GetRawText()}");
        await RequireRowAddedAsync(transport, model.SourceRows, "sourceRows");

        // The page's state equals the model's.
        using var state = transport.SnapshotState(Route);
        var published = state.RootElement.GetProperty("state");
        Require(Keys(published.GetProperty("rows")).SequenceEqual(Keys(model.Rows))
            && published.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).SequenceEqual(model.Tags)
            && Keys(published.GetProperty("sourceRows")).SequenceEqual(Keys(model.SourceRows)),
            $"The state differs from the model: {published.GetRawText()}");
    }

    // #891: a keyed collection, a list, a keyed collection read through an unbridged object and a sub-object that the
    // data graph observes, each replaced under another member's notification (or an unbridged one), are observed from
    // the unit that publishes them: every later change of the new instance is published, as the change it is.
    [Test]
    [Arguments(false, "self")]
    [Arguments(false, "Alpha")]
    [Arguments(false, "Unbridged")]
    [Arguments(true, "self")]
    [Arguments(true, "Alpha")]
    [Arguments(true, "Unbridged")]
    public async Task ChangesOfAnInstanceReplacedUnderAnyNotificationArePublishedAsync(bool reactive, string notify)
    {
        var model = Create(reactive);
        using var transport = new InMemoryBridgeTransport();
        using var bridge = Attach(transport, model);
        await SettleAsync(transport);
        string Name(string member) => notify == "self" ? member : notify;

        // A keyed collection: each change of the new rows is published as an indexed change, a reset as a set.
        var previousRows = model.Rows;
        model.ReplaceRows([new("a", "one"), new("b", "two"), new("c", "three")], Name(nameof(model.Rows)));
        using (var replaced = await NextAsync(transport))
            Require(SetOf(replaced, "rows") is { } set && Keys(set).SequenceEqual(["a", "b", "c"]),
                $"The replaced rows were not set: {replaced.RootElement.GetRawText()}");
        var rows = model.Rows;
        await RequireRowChangeAsync(transport, () => rows.Add(new("d", "four")), "rows", "add", 3);
        await RequireRowChangeAsync(transport, () => rows.RemoveAt(0), "rows", "remove", 0);
        await RequireRowChangeAsync(transport, () => rows[1] = new("e", "five"), "rows", "replace", 1);
        await RequireRowChangeAsync(transport, () => rows.Move(0, 2), "rows", "move", 2);
        rows.Clear();
        using (var reset = await NextAsync(transport))
            Require(SetOf(reset, "rows") is { } set && set.GetArrayLength() == 0, $"The reset rows were not set: {reset.RootElement.GetRawText()}");
        await RequireRowChangeAsync(transport, () => rows.Add(new("f", "six")), "rows", "add", 0);
        // The previous rows are no longer observed.
        previousRows.Add(new("stale", "stale"));
        await RequireNoPushAsync(transport, "A change of the replaced rows");

        // A list without keys: each change of the new list is a set of the field.
        model.ReplaceTags(["x", "y", "z"], Name(nameof(model.Tags)));
        await RequireTagsAsync(transport, model);
        var tags = model.Tags;
        foreach (var change in (Action[])[() => tags.Add("w"), () => tags.RemoveAt(0), () => tags[0] = "v", () => tags.Move(0, 2), tags.Clear,
                     () => tags.Add("u")])
        {
            change();
            await RequireTagsAsync(transport, model);
        }

        // A keyed collection read through Source, which is not bridged.
        model.ReplaceSource([new("t", "new"), new("u", "other")], notify == "self" ? "Source" : notify);
        using (var replaced = await NextAsync(transport))
            Require(SetOf(replaced, "sourceRows") is { } set && Keys(set).SequenceEqual(["t", "u"]),
                $"The replaced source rows were not set: {replaced.RootElement.GetRawText()}");
        var sourceRows = model.SourceRows;
        await RequireRowChangeAsync(transport, () => sourceRows.Add(new("v", "added")), "sourceRows", "add", 2);
        await RequireRowChangeAsync(transport, () => sourceRows.RemoveAt(0), "sourceRows", "remove", 0);

        // A sub-object the data graph observes.
        var previousDetail = model.Detail;
        model.ReplaceDetail(new() { Label = "replaced" }, Name(nameof(model.Detail)));
        await RequireDetailAsync(transport, "replaced");
        model.Detail.Label = "changed";
        await RequireDetailAsync(transport, "changed");
        previousDetail.Label = "stale";
        await RequireNoPushAsync(transport, "A change of the replaced sub-object");

        // The page's state equals the model's.
        using var state = transport.SnapshotState(Route);
        var published = state.RootElement.GetProperty("state");
        Require(Keys(published.GetProperty("rows")).SequenceEqual(Keys(model.Rows))
            && published.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).SequenceEqual(model.Tags)
            && Keys(published.GetProperty("sourceRows")).SequenceEqual(Keys(model.SourceRows))
            && published.GetProperty("detail").GetProperty("label").GetString() == model.Detail.Label,
            $"The state differs from the model: {published.GetRawText()}");
    }

    // #909: a row's own change is a replace at the row's index after a notification of another member, in the same unit
    // or an earlier one, after a collection change moved it, after its collection was replaced under another member's
    // notification, and after a notification of every member.
    [Test]
    public async Task ARowEditFindsItsRowAfterAnyNotificationAsync()
    {
        var model = new RowIndexViewModel();
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new RowIndexBridge(transport, model, Route);
        await SettleAsync(transport);

        await RequireRowChangeAsync(transport, () => model.Rows[2].Label = "edited", "rows", "replace", 2);
        model.Selected = 1;
        using (await NextAsync(transport)) { }
        await RequireRowChangeAsync(transport, () => model.Rows[1].Label = "after a scalar", "rows", "replace", 1);
        using (BridgeSnapshotBatch.Begin(model))
        {
            model.Rows[0].Label = "before a scalar";
            model.Selected = 2;
            model.Rows[2].Label = "after a scalar in the unit";
        }
        using (var unit = await NextAsync(transport))
            Require(Changes(unit).Where(change => change.GetProperty("field").GetString() == "rows")
                    .Select(change => (change.GetProperty("kind").GetString(), change.GetProperty("index").GetInt32())).SequenceEqual([("replace", 0), ("replace", 2)]),
                $"Expected replaces of rows 0 and 2: {unit.RootElement.GetRawText()}");
        model.Rows.Insert(0, new("z", "inserted"));
        using (await NextAsync(transport)) { }
        await RequireRowChangeAsync(transport, () => model.Rows[3].Label = "moved", "rows", "replace", 3);
        model.ReplaceRows([new("p", "one"), new("q", "two")], nameof(model.Selected));
        using (var replaced = await NextAsync(transport))
            Require(SetOf(replaced, "rows") is { } set && Keys(set).SequenceEqual(["p", "q"]),
                $"The replaced rows were not set: {replaced.RootElement.GetRawText()}");
        await RequireRowChangeAsync(transport, () => model.Rows[1].Label = "in the new rows", "rows", "replace", 1);
        await RequireRowChangeAsync(transport, () =>
        {
            model.Notify(null);
            model.Rows[0].Label = "after every member";
        }, "rows", "replace", 0);

        // The page's state equals the model's.
        using var state = transport.SnapshotState(Route);
        var rows = state.RootElement.GetProperty("state").GetProperty("rows");
        Require(rows.EnumerateArray().Select(row => (row.GetProperty("key").GetString(), row.GetProperty("label").GetString()))
                .SequenceEqual(model.Rows.Select(row => ((string?)row.Key, (string?)row.Label))),
            $"The state differs from the model: {rows.GetRawText()}");
    }

    // #909: the row index of a collection that is replaced does not keep the previous rows alive.
    [Test]
    public async Task AReplacedCollectionIsNotKeptByItsRowIndexAsync()
    {
        var model = new RowIndexViewModel();
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new RowIndexBridge(transport, model, Route);
        await SettleAsync(transport);
        var previous = EditThenReplaceRows(model);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Require(!previous.TryGetTarget(out _), "The Bridge kept the replaced rows alive.");
    }

    // Edits a row, which builds the row index, then replaces the rows under another member's notification; each in a
    // unit that its batch publishes when it ends. Returns the replaced rows.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<System.Collections.ObjectModel.ObservableCollection<EditableRow>> EditThenReplaceRows(RowIndexViewModel model)
    {
        var previous = new WeakReference<System.Collections.ObjectModel.ObservableCollection<EditableRow>>(model.Rows);
        using (BridgeSnapshotBatch.Begin(model)) model.Rows[0].Label = "edited";
        using (BridgeSnapshotBatch.Begin(model)) model.ReplaceRows([new("p", "one")], nameof(model.Selected));
        return previous;
    }

    // #911: a unit compares a scalar member by its value instead of encoding it, and still publishes every member whose
    // encoding changed: members changed without their own notification, values that are equal but encode differently,
    // and a member computed from another. After each unit, the state the frames produce is the route's state.
    [Test]
    public async Task ScalarMembersComparedByValuePublishEveryChangedEncodingAsync()
    {
        var model = new ScalarMembersViewModel();
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new ScalarMembersBridge(transport, model, Route);
        await SettleAsync(transport);
        Dictionary<string, string> client;
        using (var first = transport.SnapshotState(Route))
            client = first.RootElement.GetProperty("state").EnumerateObject().ToDictionary(member => member.Name, member => member.Value.GetRawText());

        foreach (var (step, change) in (ValueTuple<string, Action>[])[
                     ("count", () => model.Count = 1),
                     ("negative zero", () => { model.SetSilently(ratio: -0.0); model.Count = 2; }),
                     ("decimal scale", () => { model.SetSilently(amount: 1.00m); model.Count = 3; }),
                     ("date kind", () => { model.SetSilently(at: DateTime.SpecifyKind(model.At, DateTimeKind.Utc)); model.Count = 4; }),
                     ("unannounced", () => { model.SetSilently(ratio: 0.5, amount: 2m); model.Notify("Unbridged"); })])
        {
            change();
            using (var frame = await NextAsync(transport))
                foreach (var set in Changes(frame).Where(entry => entry.GetProperty("kind").GetString() == "set"))
                    client[set.GetProperty("field").GetString()!] = set.GetProperty("value").GetRawText();
            using var state = transport.SnapshotState(Route);
            foreach (var member in state.RootElement.GetProperty("state").EnumerateObject())
                Require(client.GetValueOrDefault(member.Name) == member.Value.GetRawText(),
                    $"After {step}, {member.Name} is {client.GetValueOrDefault(member.Name)} on the page and {member.Value.GetRawText()} in the state.");
        }
        Require(client["doubled"] == "8" && client["ratio"] == "0.5" && client["amount"] == "\"2\"", $"The page's state is not the model's: {string.Join(", ", client)}");
    }

    // A change of a keyed collection is published as one change of that kind at that index.
    private static async Task RequireRowChangeAsync(InMemoryBridgeTransport transport, Action change, string field, string kind, int index)
    {
        change();
        using var frame = await NextAsync(transport);
        var changes = Changes(frame).Where(entry => entry.GetProperty("field").GetString() == field).ToArray();
        Require(changes.Length == 1 && changes[0].GetProperty("kind").GetString() == kind && changes[0].GetProperty("index").GetInt32() == index,
            $"Expected one {kind} of {field} at {index}: {frame.RootElement.GetRawText()}");
    }

    private static async Task RequireTagsAsync(InMemoryBridgeTransport transport, INotificationCostViewModel model)
    {
        using var frame = await NextAsync(transport);
        Require(SetOf(frame, "tags") is { } tags && tags.EnumerateArray().Select(tag => tag.GetString()).SequenceEqual(model.Tags),
            $"The tags [{string.Join(", ", model.Tags)}] were not set: {frame.RootElement.GetRawText()}");
    }

    private static async Task RequireDetailAsync(InMemoryBridgeTransport transport, string label)
    {
        using var frame = await NextAsync(transport);
        Require(SetOf(frame, "detail")?.GetProperty("label").GetString() == label,
            $"The detail {label} was not set: {frame.RootElement.GetRawText()}");
    }

    private static async Task RequireNoPushAsync(InMemoryBridgeTransport transport, string what)
    {
        await Task.Delay(100);
        var pushes = transport.TakePushes(Route);
        Require(pushes.Count == 0, $"{what} published: {string.Join(", ", pushes.Select(push => push.RootElement.GetRawText()))}");
    }

    private static IEnumerable<string?> Keys(JsonElement rows) => rows.EnumerateArray().Select(row => row.GetProperty("key").GetString());

    private static IEnumerable<string?> Keys(IEnumerable<KeyedRow> rows) => rows.Select(row => (string?)row.Key);

    // A row added to a replaced collection is published as an add at its index or as a set of the field.
    private static async Task RequireRowAddedAsync(InMemoryBridgeTransport transport, System.Collections.ObjectModel.ObservableCollection<KeyedRow> rows,
        string field)
    {
        rows.Add(new($"added{rows.Count}", "added"));
        using var added = await NextAsync(transport);
        Require(Changes(added).Any(change => change.GetProperty("field").GetString() == field
                && (change.GetProperty("kind").GetString() == "add" && change.GetProperty("index").GetInt32() == rows.Count - 1
                    || change.GetProperty("kind").GetString() == "set" && change.GetProperty("value").GetArrayLength() == rows.Count)),
            $"A row added to the new {field} was not published: {added.RootElement.GetRawText()}");
    }

    // A change of a replaced keyed collection that arrives before any notification reported the replacement is a set
    // of the field, never an indexed change against the previous rows.
    [Test]
    public async Task AChangeOfAnUnreportedInstanceIsASetAsync()
    {
        var model = new ToolkitCostViewModel();
        using var transport = new InMemoryBridgeTransport();
        using var bridge = Attach(transport, model);
        await SettleAsync(transport);
        using (BridgeSnapshotBatch.Begin(model))
        {
            // An unrelated notification: the Bridge does not read Rows until the unit.
            model.ReplaceRows([new("b", "two"), new("c", "three")], "Unbridged");
            model.Rows.RemoveAt(0);
        }
        using var frame = await NextAsync(transport);
        Require(SetOf(frame, "rows") is { } rows && rows.EnumerateArray().Select(row => row.GetProperty("key").GetString()).SequenceEqual(["c"])
            && !Changes(frame).Any(change => change.GetProperty("field").GetString() == "rows" && change.GetProperty("kind").GetString() != "set"),
            $"The replaced rows were not set: {frame.RootElement.GetRawText()}");
    }

    // #910: a collection change finds its keyed collection by the instance the Bridge last read. A notification by the
    // wire name makes the data graph observe the replaced rows before the Bridge reads them; their change is then a set of
    // that field only, not of every keyed collection.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AChangeOfRowsTheBridgeHasNotReadIsASetOfTheirFieldAsync(bool reactive)
    {
        var model = Create(reactive);
        using var transport = new InMemoryBridgeTransport();
        using var bridge = Attach(transport, model);
        await SettleAsync(transport);
        using (BridgeSnapshotBatch.Begin(model))
        {
            model.ReplaceRows([new("b", "two")], "rows");
            model.Rows.Add(new("c", "three"));
        }
        using var frame = await NextAsync(transport);
        var changes = Changes(frame).Select(change => (Field: change.GetProperty("field").GetString(), Kind: change.GetProperty("kind").GetString())).ToArray();
        Require(changes.SequenceEqual([("rows", "set")]) && SetOf(frame, "rows") is { } rows && Keys(rows).SequenceEqual(["b", "c"]),
            $"Expected a set of rows alone: {frame.RootElement.GetRawText()}");
    }

    // An observed sub-object replaced under a named, an unnamed, another member's or an unbridged notification is
    // observed before the unit publishes; a burst that does not name it does not run its accessor.
    [Test]
    [Arguments("Sub")]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("Other")]
    [Arguments("Unbridged")]
    public async Task AReplacedSubObjectIsObservedAsync(string? notify)
    {
        var model = new ObservedModel();
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new ObservedBridge(transport, model);
        await SettleAsync(transport);

        var observed = model.ObserveCalls;
        using (BridgeSnapshotBatch.Begin(model))
        {
            for (var index = 0; index < Burst; index++) model.Other = index;
            Require(model.ObserveCalls == observed, $"{Burst} notifications of another member ran the observed accessor {model.ObserveCalls - observed} times.");
        }
        using (await NextAsync(transport)) { }

        var previous = model.Sub;
        model.Replace(new() { Value = "replaced" }, notify);
        using (var replaced = await NextAsync(transport))
            Require(SetOf(replaced, "value")?.GetString() == "replaced", $"The replaced sub-object was not published: {replaced.RootElement.GetRawText()}");
        model.Sub.Value = "changed";
        using (var changed = await NextAsync(transport))
            Require(SetOf(changed, "value")?.GetString() == "changed", $"A change of the new sub-object was not published: {changed.RootElement.GetRawText()}");
        // The previous sub-object is no longer observed: its change publishes nothing.
        previous.Value = "stale";
        await Task.Delay(100);
        Require(transport.TakePushes(Route).Count == 0, "A change of the replaced sub-object published a frame.");
    }

    private static INotificationCostViewModel Create(bool reactive) => reactive ? new ReactiveCostViewModel() : new ToolkitCostViewModel();

    private static IDisposable Attach(InMemoryBridgeTransport transport, INotificationCostViewModel model) => model switch
    {
        ReactiveCostViewModel reactive => new ReactiveCostBridge(transport, reactive, Route),
        ToolkitCostViewModel toolkit => new ToolkitCostBridge(transport, toolkit, Route),
        _ => throw new ArgumentOutOfRangeException(nameof(model)),
    };

    // Reads the route's state, the baseline of its frames, and drops the pushes the attach produced.
    private static async Task SettleAsync(InMemoryBridgeTransport transport)
    {
        using (transport.SnapshotState(Route)) { }
        await Task.Delay(50);
        foreach (var push in transport.TakePushes(Route)) push.Dispose();
    }

    private sealed record Measurement(Dictionary<string, int> During, Dictionary<string, int> Unit, JsonDocument Frame);

    // Sets every scalar member notifications times in one batch: the getter calls during the burst and in its unit.
    private static async Task<Measurement> MeasureAsync(InMemoryBridgeTransport transport, INotificationCostViewModel model, int notifications)
    {
        var before = model.Counts.Snapshot();
        Dictionary<string, int> during;
        using (BridgeSnapshotBatch.Begin(model))
        {
            for (var index = 0; index < notifications; index++)
                foreach (var member in model.Scalars) model.SetScalar(member, $"{member}{index}");
            during = Delta(model.Counts.Snapshot(), before);
        }
        var frame = await NextAsync(transport);
        var unit = Delta(model.Counts.Snapshot(), Sum(before, during));
        return new(during, unit, frame);
    }

    private static Dictionary<string, int> Sum(Dictionary<string, int> first, Dictionary<string, int> second)
    {
        var sum = new Dictionary<string, int>(first, StringComparer.Ordinal);
        foreach (var (member, count) in second) sum[member] = sum.GetValueOrDefault(member) + count;
        return sum;
    }

    private static Dictionary<string, int> Delta(Dictionary<string, int> after, Dictionary<string, int> before) =>
        after.ToDictionary(entry => entry.Key, entry => entry.Value - before.GetValueOrDefault(entry.Key), StringComparer.Ordinal);

    private static string Camel(string member) => char.ToLowerInvariant(member[0]) + member[1..];

    private static async Task<JsonDocument> NextAsync(InMemoryBridgeTransport transport)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < timeout)
        {
            var pushes = transport.TakePushes(Route);
            if (pushes.Count > 0)
            {
                Require(pushes.Count == 1, $"A unit emitted more than one push: {string.Join(", ", pushes.Select(push => push.RootElement.GetRawText()))}");
                return pushes[0];
            }
            await Task.Delay(5);
        }
        throw new InvalidOperationException("The bridge did not publish a frame.");
    }

    private static JsonElement[] Changes(JsonDocument frame)
    {
        Require(frame.RootElement.GetProperty("kind").GetString() == "frame", $"Expected a frame: {frame.RootElement.GetRawText()}");
        return [.. frame.RootElement.GetProperty("changes").EnumerateArray()];
    }

    private static JsonElement? SetOf(JsonDocument frame, string field) =>
        Changes(frame).Where(change => change.GetProperty("field").GetString() == field && change.GetProperty("kind").GetString() == "set")
            .Select(change => (JsonElement?)change.GetProperty("value")).FirstOrDefault();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SubObject : INotifyPropertyChanged
    {
        private string _value = "";

        public string Value
        {
            get => _value;
            set { _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // A ViewModel with a sub-object the Bridge observes through PropertyDescriptor.Observe.
    private sealed class ObservedModel : INotifyPropertyChanged
    {
        private int _other;
        private int _observeCalls;

        public SubObject Sub { get; private set; } = new() { Value = "initial" };

        public int Other
        {
            get => _other;
            set { _other = value; PropertyChanged?.Invoke(this, new(nameof(Other))); }
        }

        public int ObserveCalls => Volatile.Read(ref _observeCalls);

        public SubObject Observe()
        {
            Interlocked.Increment(ref _observeCalls);
            return Sub;
        }

        public void Replace(SubObject sub, string? notify)
        {
            Sub = sub;
            PropertyChanged?.Invoke(this, new(notify));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class ObservedBridge(IBridgeTransport transport, ObservedModel model) : ViewModelBridge(
        transport, model, typeof(ObservedModel), Route,
        [
            new BridgeStateMember("other", static (writer, vm) => writer.WriteNumberValue(((ObservedModel)vm).Other)),
            new BridgeStateMember("value", static (writer, vm) => writer.WriteStringValue(((ObservedModel)vm).Sub.Value)),
        ],
        [
            new("Other", "other", static vm => ((ObservedModel)vm).Other, null),
            new("Sub", "value", static vm => ((ObservedModel)vm).Sub.Value, null, static vm => ((ObservedModel)vm).Observe()),
        ], [], []);
}
