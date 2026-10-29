using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// DynamicData and ReactiveUI.Validation integration of the producer (PLAN-0.14 V1).
internal sealed class ForkIntegrationTests
{
    // Protocol 4: a ViewModel with validation publishes its keyed collection and append-only text as frames, and each
    // frame carries the validation the change causes. Applying every frame gives the snapshot.
    [Test]
    public async Task ValidationAndCollectionChangesShareOneFrameAsync()
    {
        await using var context = new RunicModelContext();
        var model = new ValidatedRowsViewModel();
        model.Add(1);
        model.Add(2);
        model.Add(3);
        using var host = new RunicWindowTestHost<ValidatedRowsViewModel>(model,
            (transport, content, vm) => new ValidatedRowsBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "validatedRows", ModelContext = context, ViewLocator = new TestViewLocator() });
        // A page reads the state first; a change before the route's first state is part of it, not a frame.
        _ = host.Root.Snapshot();
        await context.InvokeAsync(() => model.Add(4));
        var invalid = await host.Root.NextPublicationAsync();
        Require(invalid.Kind == RunicPublicationKind.Frame
                && invalid.Changes.Any(change => change.Field == "rows" && change.Kind == RunicCollectionChangeKind.Add)
                && invalid.Texts.Any(text => text.Field == "log" && text.Text == "+4")
                && invalid.Sets.Any(set => set.Field == "validation" && set.Value.GetProperty("hasErrors").GetBoolean())
                && !invalid.Sets.Any(set => set.Field is "rows" or "log"),
            $"The row, its log and the validation it caused were not one frame of indexed changes: {invalid.Json}");
        await context.InvokeAsync(() => model.RemoveAt(0));
        var valid = await host.Root.NextPublicationAsync();
        Require(valid.Kind == RunicPublicationKind.Frame
                && valid.Changes.Any(change => change.Field == "rows" && change.Kind == RunicCollectionChangeKind.Remove)
                && valid.Sets.Any(set => set.Field == "validation" && !set.Value.GetProperty("hasErrors").GetBoolean()),
            $"The removal and the validation it cleared were not one frame: {valid.Json}");

        // Replaying the frames on the state they start from gives the snapshot.
        var tracker = host.Root.Track();
        await context.InvokeAsync(() => model.Add(5));
        await context.InvokeAsync(() => model.Move(0, 2));
        await context.InvokeAsync(() => model.RemoveAt(1));
        await tracker.WaitUntilAsync(state => state["rows"].GetArrayLength() == 3 && state["log"].GetString()!.EndsWith("+5-", StringComparison.Ordinal));
        tracker.Verify();
        Require(tracker.FullStates == 0 && tracker.Deltas >= 3, $"Expected frames only, not {tracker.Deltas} frames and {tracker.FullStates} states.");
    }

    // A closed model context rejects the turn of a change notification. The change counts as one for a released
    // Bridge instead of throwing ObjectDisposedException into the code that changed the model, such as a DynamicData
    // Bind; the Bridge can still be disposed.
    [Test]
    public async Task ClosedModelContextDoesNotThrowIntoTheChangeAsync()
    {
        var context = new RunicModelContext();
        var model = new CollectionDeltaViewModel();
        using var lease = RunicModelContextRegistry.Shared.Bind(context, model);
        var transport = new InMemoryBridgeTransport();
        var bridge = await context.InvokeAsync(() => new CollectionDeltaBridge(transport, model, "rows"));
        await context.DisposeAsync();
        model.Items.Add(new CollectionRow(1, "one"));
        model.Items.RemoveAt(0);
        model.Title = "changed";
        using (BridgeSnapshotBatch.Begin(model)) model.Items.Add(new CollectionRow(2, "two"));
        bridge.Dispose();
    }

    // Event 1025: a collection that changes outside the model context's turn is logged once per field.
    [Test]
    public async Task CollectionChangedOutsideTheModelTurnIsLoggedOnceAsync()
    {
        await using var context = new RunicModelContext();
        var logs = new OperationRetirementTests.LogCapture();
        var model = new CollectionDeltaViewModel();
        using var host = new RunicWindowTestHost<CollectionDeltaViewModel>(model,
            (transport, content, vm) => new CollectionDeltaBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "collectionDelta", ModelContext = context, LoggerFactory = logs, ViewLocator = new TestViewLocator() });
        await context.InvokeAsync(() => model.Items.Add(new CollectionRow(1, "in a turn")));
        Require(!logs.All(1025).Any(), "A change in a model turn was logged.");
        model.Items.Add(new CollectionRow(2, "outside"));
        model.Items.Add(new CollectionRow(3, "outside again"));
        var entries = logs.All(1025).ToArray();
        Require(entries.Length == 1 && entries[0].Level == Microsoft.Extensions.Logging.LogLevel.Warning
                && Equals(entries[0].State["Field"], "rows") && Equals(entries[0].State["Model"], nameof(CollectionDeltaViewModel)),
            $"A change outside the model turn was not logged once for its field: {string.Join("; ", entries.Select(entry => entry.Message))}");
    }

    // The scheduler diagnostic (event 1043) treats an assembly as a library only by its exact name or a dotted child.
    [Test]
    public void LibraryAssembliesMatchByExactNameOrDottedChild()
    {
        Require(OutsideModelTurnReporter.IsInFamily("DynamicData", "DynamicData")
                && OutsideModelTurnReporter.IsInFamily("DynamicData.Reactive", "DynamicData")
                && OutsideModelTurnReporter.IsInFamily("System.Private.CoreLib", "System")
                && OutsideModelTurnReporter.IsInFamily("Microsoft.Extensions.Logging", "Microsoft"),
            "A library assembly was not recognized.");
        Require(!OutsideModelTurnReporter.IsInFamily("DynamicDataViewer", "DynamicData")
                && !OutsideModelTurnReporter.IsInFamily("ReactiveUIDemo", "ReactiveUI")
                && !OutsideModelTurnReporter.IsInFamily("SystemMonitor", "System")
                && !OutsideModelTurnReporter.IsInFamily("Splatter", "Splat"),
            "An application assembly whose name starts with a library's was taken for the library.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
