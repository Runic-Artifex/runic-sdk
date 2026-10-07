using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// The typed drivers, manual clock, sequential ids and state tracker of RunicWindowTestHost.
internal static class HostDriverTests
{
    internal static async Task RunAsync()
    {
        ManualClockFiresTimersInOrder();
        await DriversUseMembersAndStableIdsAsync();
        await OperationsAndCommandsAsync();
        await TrackerFollowsCollectionDeltasAsync();
        await ClockDrivesCloseTimeoutsAsync();
    }

    private static void ManualClockFiresTimersInOrder()
    {
        var clock = new ManualTimeProvider();
        var start = clock.GetUtcNow();
        var fired = new List<string>();
        using var late = clock.CreateTimer(_ => fired.Add("late"), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        using var early = clock.CreateTimer(_ => fired.Add("early"), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3), clock);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Require(fired.Count == 0 && !cancellation.IsCancellationRequested, "A manual timer fired early.");
        clock.Advance(TimeSpan.FromSeconds(2.001));
        Require(fired.SequenceEqual(["early", "late", "early", "early"]), $"Manual timers fired as {string.Join(", ", fired)}.");
        Require(cancellation.IsCancellationRequested, "A CancellationTokenSource timeout did not use the manual clock.");
        Require(clock.GetUtcNow() - start == TimeSpan.FromSeconds(3), "The manual clock did not land on its target.");
        Require(Throws<ArgumentOutOfRangeException>(() => clock.SetUtcNow(start)), "The manual clock moved backwards.");
    }

    private static async Task DriversUseMembersAndStableIdsAsync()
    {
        var model = new RootViewModel();
        using var host = new RunicWindowTestHost<RootViewModel>(model,
            (transport, content, vm) => new RootBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ViewLocator = new TestViewLocator() });
        Require(host.RootRoute == "root" && host.Time is ManualTimeProvider, "The options host did not derive its route or clock.");
        var root = host.Root.Snapshot();
        Require(root.Read(vm => vm.Count) == 0, "The typed snapshot did not read the count.");
        Require(root.Reference(vm => vm.Child) == new PageReference("child", "1"), $"Content ids are not sequential: {root}");

        var child = host.Root.View<ChildViewModel>(vm => vm.Child);
        Require(child.Route == "content1", "The content driver did not use the referenced route.");
        Require(Throws<InvalidOperationException>(() => host.Root.View<RootViewModel>(vm => vm.Child)),
            "A content driver accepted the wrong ViewModel kind.");
        var token = child.Mount();
        var childModel = model.Child!;
        Require(token == "test:mount1" && childModel.Title == "mounted", "The driver did not mount the View with a stable token.");

        var tracker = child.Track();
        var reply = child.Set(vm => vm.Title, "edited").EnsureOk();
        Require(childModel.Title == "edited" && reply.State!.Read(vm => vm.Title) == "edited", "The typed setter did not update the model.");
        await tracker.WaitUntilAsync(state => state.Read(vm => vm.Title) == "edited");
        tracker.Verify();

        var version = child.Snapshot().FieldVersion(vm => vm.Title);
        var applied = child.Write(vm => vm.Title, "written");
        Require(applied.Kind == "applied" && applied.Version > version && childModel.Title == "written", $"The checked write did not apply: {applied}");
        var stale = child.Write(vm => vm.Title, "stale", expectedVersion: version);
        Require(stale.Kind == "conflict" && stale.Value?.GetString() == "written", $"A stale checked write did not conflict: {stale}");

        (await host.Root.ExecuteAsync(vm => vm.IncrementCommand)).EnsureOk();
        Require(model.Count == 1, "The typed command did not run.");
        var publication = await host.Root.NextPublicationAsync();
        Require(publication.Kind == RunicPublicationKind.State && publication.Route == "root", $"Unexpected root frame {publication}.");
        Require(child.Unmount(token) == "ok", "The driver did not unmount the View.");
    }

    private static async Task OperationsAndCommandsAsync()
    {
        var model = new ToolkitTypedViewModel();
        using var host = new RunicWindowTestHost<ToolkitTypedViewModel>(model,
            (transport, content, vm) => new ToolkitTypedBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ViewLocator = new TestViewLocator() });
        var driver = host.Root;
        Require(driver.CanExecute(vm => vm.AddCommand, "3") && !driver.CanExecute(vm => vm.AddCommand, "30"),
            "Typed availability queries did not reach the command.");
        var rejected = await driver.ExecuteAsync(vm => vm.AddCommand, "30");
        Require(!rejected.Ok && rejected.ErrorKind == "rejected", $"An unavailable command was not rejected: {rejected}");
        (await driver.ExecuteAsync(vm => vm.AsyncIntCommand, "4")).EnsureOk();
        Require(model.Total == 4, "The awaited typed command did not run.");

        var operation = driver.Start(vm => vm.AsyncDtoCommand, """{"documentId":"document-1","delta":2}""");
        Require(operation.Admission == "accepted" && operation.RequestId == "request1", $"The operation was not admitted: {operation.AdmissionJson}");
        var status = await operation.WaitAsync();
        Require(status.Kind == "succeeded" && model.Total == 6, $"The operation did not succeed: {status.Json}");
        var duplicate = driver.Start(vm => vm.AsyncDtoCommand, """{"documentId":"document-1","delta":2}""", operation.RequestId);
        Require(duplicate.Admission == "duplicate" && model.Total == 6, "A repeated request id ran twice.");

        var waiting = driver.Start(vm => vm.CancelCommand, "\"wait\"");
        Require(waiting.Status().Kind == "running", "The waiting operation did not run.");
        Require(waiting.Cancel() == "cancellation-requested", "The operation could not be cancelled.");
        Require((await waiting.WaitAsync()).Kind == "cancelled" && model.CancelledCount == 1, "The operation did not end cancelled.");
    }

    private static async Task TrackerFollowsCollectionDeltasAsync()
    {
        var model = new CollectionDeltaViewModel();
        using var host = new RunicWindowTestHost<CollectionDeltaViewModel>(model,
            (transport, content, vm) => new CollectionDeltaBridge(transport, vm, content: content), new RunicWindowTestHostOptions
            {
                RootRoute = "collectionDelta",
            });
        var tracker = host.Root.Track();
        for (var id = 1; id <= 3; id++) model.Items.Add(new(id, $"row {id}"));
        await tracker.WaitUntilAsync(state => state.Keys(vm => vm.Rows).Count == 3);
        model.Items.Move(2, 0);
        model.Items[1] = new(1, "one");
        model.Items.RemoveAt(2);
        var state = await tracker.WaitUntilAsync(state => state.Keys(vm => vm.Rows).SequenceEqual(["3", "1"]));
        Require(state["rows"][1].GetProperty("label").GetString() == "one", $"The replaced row was not applied: {state}");
        Require(tracker.Deltas >= 2 && tracker.FullStates == 0, $"Collection edits did not arrive as deltas ({tracker.Deltas}, {tracker.FullStates}).");
        Require(tracker.Changes.Select(change => change.Kind).Distinct().Order().SequenceEqual(["add", "move", "remove", "replace"]),
            $"Unexpected changes: {string.Join(", ", tracker.Changes.Select(change => change.Kind))}");
        tracker.Verify();

        model.Title = "changed";
        await tracker.WaitUntilAsync(state => state.Read(vm => vm.Title) == "changed");
        tracker.Verify();
    }

    private static async Task ClockDrivesCloseTimeoutsAsync()
    {
        var model = new ToolkitTypedViewModel();
        var clock = new ManualTimeProvider();
        using var host = new RunicWindowTestHost<ToolkitTypedViewModel>(model,
            (transport, content, vm) => new ToolkitTypedBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { TimeProvider = clock, CreateId = Sequence("page") });
        var waiting = host.Root.Start(vm => vm.CancelCommand, "\"wait\"");
        var close = host.BeginCloseAsync(TimeSpan.FromSeconds(10)).AsTask();
        await Task.Delay(50);
        Require(!close.IsCompleted, "The close timeout ran on the system clock.");
        clock.Advance(TimeSpan.FromSeconds(10));
        var result = await close.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!result.Drained && result.RemainingOperations == 1, "The close did not time out on the manual clock.");
        await result.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Require((await waiting.WaitAsync()).Kind == "cancelled", "The timed-out operation was not cancelled.");
    }

    private static Func<string> Sequence(string prefix)
    {
        var next = 0;
        return () => $"{prefix}{++next}";
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try { action(); return false; }
        catch (TException) { return true; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
