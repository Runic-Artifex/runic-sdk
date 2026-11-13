using Runic.Application.Testing;
using Runic.Application.Testing.Tests;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Scaling.Tests;

// The priority-1 bridge cases of the scaling suite (BM1, D063.2). Each case measures counts at the two sizes of its
// budget entries in tests/scaling/budgets.json and checks how they grow (ScalingBudgets). A case runs its mutations
// inside a BridgeSnapshotBatch: the ViewModels have no model context, so the notifications, and the unit the batch
// publishes when it ends, run inline on the test thread, and the counts read after it are the unit's.
//
// Cases whose model raises the notifications run for a CommunityToolkit.Mvvm and a ReactiveUI ViewModel; the metric
// names end in [toolkit] or [reactive]. Cases driven by ObservableCollection changes alone run once.
internal sealed class BridgeScalingTests
{
    private const string Route = "scaling";
    // The named burst: 60 notifications on the first 6 members, 10 each (#887, #890's NotificationBurstBenchmarks).
    private const int BurstNotifications = 60;
    private const int BurstMembers = 6;

    // The row a row edit changes: the same index at every size, so the frame's text does not change with the size.
    private const int EditedRow = 100;

    private static string Flavour(bool reactive) => reactive ? "reactive" : "toolkit";

    // #887: a named notification reads only the named member's getters, so a burst reads O(1) getters in the number of
    // members, other members not at all, and the unit reads each getter at most twice (#890). With #890 reverted, each
    // notification reads every member: the burst grows with the members.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task NamedBurstGetterReadsAsync(bool reactive) => ScalingBudgets.RunAsync(
        [$"bridge/named-burst/burst-getter-reads[{Flavour(reactive)}]", $"bridge/named-burst/burst-reads-of-others[{Flavour(reactive)}]",
            $"bridge/named-burst/unit-reads-per-member[{Flavour(reactive)}]"],
        async members =>
        {
            var model = Wide(members, reactive);
            using var transport = new InMemoryBridgeTransport();
            using var bridge = Attach(transport, model);
            await SettleAsync(transport);
            var before = model.Counts.Snapshot();
            long[] during;
            using (BridgeSnapshotBatch.Begin(model))
            {
                for (var index = 0; index < BurstNotifications; index++) model.SetScalar(index % BurstMembers, index + 1);
                during = Delta(model.Counts.Snapshot(), before);
            }
            var unit = Delta(model.Counts.Snapshot(), Sum(before, during));
            Drain(transport);
            return [during.Sum(), during.Skip(BurstMembers).DefaultIfEmpty().Max(), unit.Max()];
        });

    // A burst of named notifications runs no Observe accessor of another member; the unit runs each one once (#890,
    // #894).
    [Test]
    public Task NamedBurstObserveCallsAsync() => ScalingBudgets.RunAsync(
        ["bridge/named-burst/observe-calls-burst", "bridge/named-burst/observe-calls-unit"],
        async members =>
        {
            var model = new ObservedWideModel(members);
            using var transport = new InMemoryBridgeTransport();
            using var bridge = new ObservedWideBridge(transport, model, Route);
            await SettleAsync(transport);
            var before = model.ObserveCalls;
            long during;
            using (BridgeSnapshotBatch.Begin(model))
            {
                for (var index = 0; index < BurstNotifications; index++) model.Set(index % BurstMembers, index + 1);
                during = model.ObserveCalls - before;
            }
            var unit = model.ObserveCalls - before - during;
            Drain(transport);
            return [during, unit];
        });

    // A notification without a name refreshes every member once: O(members) getter reads in all.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task UnnamedNotificationGetterReadsAsync(bool reactive) => ScalingBudgets.RunAsync(
        [$"bridge/unnamed-notification/getter-reads[{Flavour(reactive)}]"],
        async members =>
        {
            var model = Wide(members, reactive);
            using var transport = new InMemoryBridgeTransport();
            using var bridge = Attach(transport, model);
            await SettleAsync(transport);
            var before = model.Counts.Total;
            using (BridgeSnapshotBatch.Begin(model)) model.Notify(null);
            var reads = model.Counts.Total - before;
            Drain(transport);
            return [reads];
        });

    // B6: a unit after one scalar change encodes every compared member to find the changed one, so its members and
    // bytes serialized grow with the state.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task ScalarUnitMembersSerializedAsync(bool reactive) => ScalingBudgets.RunAsync(
        [$"bridge/scalar-unit/members-serialized[{Flavour(reactive)}]", $"bridge/scalar-unit/bytes-serialized[{Flavour(reactive)}]"],
        async members =>
        {
            var model = Wide(members, reactive);
            using var transport = new InMemoryBridgeTransport();
            using var bridge = Attach(transport, model);
            await SettleAsync(transport);
            var before = Costs(bridge);
            using (BridgeSnapshotBatch.Begin(model)) model.SetScalar(1, 1);
            var after = Costs(bridge);
            Drain(transport);
            return [after.MembersSerialized - before.MembersSerialized, after.BytesSerialized - before.BytesSerialized];
        });

    // B3: each CollectionChanged of one keyed collection reads the getter of every keyed collection to find the one
    // that changed.
    [Test]
    public Task CollectionBurstGetterReadsAsync() => ScalingBudgets.RunAsync(
        ["bridge/collection-burst/getter-reads"],
        async collections =>
        {
            const int Adds = 256;
            var model = Collections(collections, reactive: false);
            using var transport = new InMemoryBridgeTransport();
            using var bridge = Attach(transport, model);
            await SettleAsync(transport);
            var before = CollectionReads(model);
            using (BridgeSnapshotBatch.Begin(model))
                for (var index = 0; index < Adds; index++) model.Rows(0).Add(Row(index));
            var reads = CollectionReads(model) - before;
            Drain(transport);
            return [reads];
        });

    // B3: each CollectionChanged counts the pending changes of its collection, so a burst of n adds in one turn visits
    // about n²/2 pending changes.
    [Test]
    public Task CollectionBurstPendingScansAsync() => ScalingBudgets.RunAsync(
        ["bridge/collection-burst/pending-scans"],
        async adds =>
        {
            var model = Collections(1, reactive: false);
            using var transport = new InMemoryBridgeTransport();
            using var bridge = Attach(transport, model);
            await SettleAsync(transport);
            var before = Costs(bridge);
            using (BridgeSnapshotBatch.Begin(model))
                for (var index = 0; index < adds; index++) model.Rows(0).Add(Row(index));
            var after = Costs(bridge);
            Drain(transport);
            return [after.PendingScans - before.PendingScans];
        });

    // B2 (#909): a row edit finds its row through the collection's row-to-index map, which a named notification of
    // another member keeps, also across units. A first edit and scalar notification in a unit of their own build the
    // map; the edits after them, each followed by a named notification of a scalar, index no row.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task RowEditWithScalarIndexRebuildRowsAsync(bool reactive) => ScalingBudgets.RunAsync(
        [$"bridge/row-edit-with-scalar/index-rebuild-rows[{Flavour(reactive)}]"],
        async rows =>
        {
            const int Edits = 16;
            var model = Collections(1, reactive, rows);
            using var transport = new InMemoryBridgeTransport();
            using var bridge = Attach(transport, model);
            await SettleAsync(transport);
            using (BridgeSnapshotBatch.Begin(model))
            {
                model.Rows(0)[rows - 1].Label = "first";
                model.Selected = -1;
            }
            Drain(transport);
            var before = Costs(bridge);
            using (BridgeSnapshotBatch.Begin(model))
                for (var edit = 0; edit < Edits; edit++)
                {
                    model.Rows(0)[edit * (rows / Edits)].Label = $"edited{edit}";
                    model.Selected = edit + 1;
                }
            var after = Costs(bridge);
            Drain(transport);
            return [after.IndexRebuildRows - before.IndexRebuildRows];
        });

    // One row property change is one replace change: the frame does not grow with the rows, and no key is scanned.
    [Test]
    public Task RowEditFrameBytesAsync() => ScalingBudgets.RunAsync(
        ["bridge/row-edit/frame-bytes", "bridge/row-edit/key-scans"],
        async rows =>
        {
            var model = Collections(1, reactive: false, rows);
            using var transport = new InMemoryBridgeTransport();
            using var bridge = Attach(transport, model);
            await SettleAsync(transport);
            var before = Costs(bridge);
            using (BridgeSnapshotBatch.Begin(model)) model.Rows(0)[EditedRow].Label = "edited";
            var after = Costs(bridge);
            Drain(transport);
            return [after.FrameBytes - before.FrameBytes, after.KeyScans - before.KeyScans];
        });

    // One insert at the start is one add change: the frame does not grow with the rows, and no key is scanned.
    [Test]
    public Task AddFrameBytesAsync() => ScalingBudgets.RunAsync(
        ["bridge/add/frame-bytes", "bridge/add/key-scans"],
        async rows =>
        {
            var model = Collections(1, reactive: false, rows);
            using var transport = new InMemoryBridgeTransport();
            using var bridge = Attach(transport, model);
            await SettleAsync(transport);
            var before = Costs(bridge);
            using (BridgeSnapshotBatch.Begin(model)) model.Rows(0).Insert(0, Row(rows));
            var after = Costs(bridge);
            Drain(transport);
            return [after.FrameBytes - before.FrameBytes, after.KeyScans - before.KeyScans];
        });

    // B11: removing a row releases its node without walking the data graph. A row that a second collection also holds is
    // a separate node of that collection, so removing it from one collection does not walk the graph either.
    [Test]
    public Task GraphRemoveWalksAsync() => ScalingBudgets.RunAsync(
        ["bridge/graph/remove-walks-unshared", "bridge/graph/remove-walks-shared"],
        async rows =>
        {
            var model = Collections(4, reactive: false, rows);
            var shared = model.Rows(0)[rows - 1];
            model.Rows(1).Add(shared);
            using var transport = new InMemoryBridgeTransport();
            using var bridge = Attach(transport, model);
            await SettleAsync(transport);
            var source = (IBridgeCostSource)bridge;
            var before = source.DataGraphWalks;
            using (BridgeSnapshotBatch.Begin(model)) model.Rows(0).RemoveAt(0);
            var unshared = source.DataGraphWalks - before;
            before = source.DataGraphWalks;
            using (BridgeSnapshotBatch.Begin(model)) model.Rows(0).Remove(shared);
            var sharedWalks = source.DataGraphWalks - before;
            Drain(transport);
            return [unshared, sharedWalks];
        });

    // Bytes allocated on the test thread by a burst of named notifications (O(1) in the members) and by its unit
    // (O(members): B6). Allocations vary a little between runs, so these budgets have a 25 % margin.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task NotificationBurstAllocationsAsync(bool reactive) => ScalingBudgets.RunAsync(
        [$"bridge/notification-burst/burst-bytes[{Flavour(reactive)}]", $"bridge/notification-burst/unit-bytes[{Flavour(reactive)}]"],
        async members =>
        {
            var model = Wide(members, reactive);
            using var transport = new InMemoryBridgeTransport();
            using var bridge = Attach(transport, model);
            await SettleAsync(transport);
            // A warm-up burst, so that first-call costs are not measured.
            Burst(model, 2_000);
            Drain(transport);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long burst, unit;
            var start = GC.GetAllocatedBytesForCurrentThread();
            var scope = BridgeSnapshotBatch.Begin(model);
            try
            {
                for (var index = 0; index < BurstNotifications; index++) model.SetScalar(index % BurstMembers, 1_000 + index % 10);
            }
            finally
            {
                burst = GC.GetAllocatedBytesForCurrentThread() - start;
                start = GC.GetAllocatedBytesForCurrentThread();
                scope.Dispose();
                unit = GC.GetAllocatedBytesForCurrentThread() - start;
            }
            Drain(transport);
            return [burst, unit];
        });

    private static void Burst(IWideViewModel model, int start)
    {
        using (BridgeSnapshotBatch.Begin(model))
            for (var index = 0; index < BurstNotifications; index++) model.SetScalar(index % BurstMembers, start + index % 10);
    }

    private static ScalingRow Row(int index) => new($"r{index:00000}", "row");

    private static IWideViewModel Wide(int members, bool reactive) => (members, reactive) switch
    {
        (20, false) => new Wide20ToolkitViewModel(),
        (20, true) => new Wide20ReactiveViewModel(),
        (80, false) => new Wide80ToolkitViewModel(),
        (80, true) => new Wide80ReactiveViewModel(),
        _ => throw new ArgumentOutOfRangeException(nameof(members), members, "SizedFixtures.cs has no wide ViewModel of this size (tests/scaling/generate-fixtures.mjs)."),
    };

    private static ICollectionsViewModel Collections(int collections, bool reactive, int rows = 0)
    {
        ICollectionsViewModel model = (collections, reactive) switch
        {
            (1, false) => new Collections1ToolkitViewModel(),
            (1, true) => new Collections1ReactiveViewModel(),
            (4, false) => new Collections4ToolkitViewModel(),
            (4, true) => new Collections4ReactiveViewModel(),
            _ => throw new ArgumentOutOfRangeException(nameof(collections), collections, "SizedFixtures.cs has no collections ViewModel of this size (tests/scaling/generate-fixtures.mjs)."),
        };
        for (var index = 0; index < rows; index++) model.Rows(0).Add(Row(index));
        return model;
    }

    private static IDisposable Attach(InMemoryBridgeTransport transport, object model) => model switch
    {
        Wide20ToolkitViewModel vm => new Wide20ToolkitBridge(transport, vm, Route),
        Wide20ReactiveViewModel vm => new Wide20ReactiveBridge(transport, vm, Route),
        Wide80ToolkitViewModel vm => new Wide80ToolkitBridge(transport, vm, Route),
        Wide80ReactiveViewModel vm => new Wide80ReactiveBridge(transport, vm, Route),
        Collections1ToolkitViewModel vm => new Collections1ToolkitBridge(transport, vm, Route),
        Collections1ReactiveViewModel vm => new Collections1ReactiveBridge(transport, vm, Route),
        Collections4ToolkitViewModel vm => new Collections4ToolkitBridge(transport, vm, Route),
        Collections4ReactiveViewModel vm => new Collections4ReactiveBridge(transport, vm, Route),
        _ => throw new ArgumentOutOfRangeException(nameof(model)),
    };

    // A copy of the Bridge's counters.
    private static BridgeCostCounters Costs(IDisposable bridge)
    {
        var costs = ((IBridgeCostSource)bridge).Costs;
        return new()
        {
            UnitsRun = costs.UnitsRun,
            MembersSerialized = costs.MembersSerialized,
            BytesSerialized = costs.BytesSerialized,
            KeyScans = costs.KeyScans,
            IndexRebuilds = costs.IndexRebuilds,
            IndexRebuildRows = costs.IndexRebuildRows,
            PendingScans = costs.PendingScans,
            FramesWritten = costs.FramesWritten,
            FrameBytes = costs.FrameBytes,
        };
    }

    // The getter calls of every keyed collection.
    private static long CollectionReads(ICollectionsViewModel model) => model.Counts.Snapshot().Take(model.Size).Sum();

    // Reads the route's state, the baseline of its frames, and drops the pushes the attach produced.
    private static async Task SettleAsync(InMemoryBridgeTransport transport)
    {
        using (transport.SnapshotState(Route)) { }
        await Task.Delay(50);
        Drain(transport);
    }

    private static void Drain(InMemoryBridgeTransport transport)
    {
        foreach (var push in transport.TakePushes(Route)) push.Dispose();
    }

    private static long[] Delta(long[] after, long[] before) => [.. after.Select((value, index) => value - before[index])];

    private static long[] Sum(long[] first, long[] second) => [.. first.Select((value, index) => value + second[index])];
}
