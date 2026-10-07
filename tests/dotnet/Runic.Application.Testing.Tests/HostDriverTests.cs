using Microsoft.Extensions.Time.Testing;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// The typed drivers, fake clock, sequential ids and state tracker of RunicWindowTestHost.
internal static class HostDriverTests
{
    internal static async Task RunAsync()
    {
        SettersAndWritesEncodeLikeTheClient();
        await DriversUseMembersAndStableIdsAsync();
        await OperationsAndCommandsAsync();
        await TrackerFollowsCollectionDeltasAsync();
        await ClockDrivesCloseTimeoutsAsync();
    }

    private static void SettersAndWritesEncodeLikeTheClient()
    {
        var model = new ScalarSetterViewModel();
        using var host = new RunicWindowTestHost<ScalarSetterViewModel>(model,
            (transport, content, vm) => new ScalarSetterBridge(transport, vm, content: content));
        var driver = host.Root;
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        driver.Set(vm => vm.Count, 3).EnsureOk();
        driver.Set(vm => vm.Enabled, true).EnsureOk();
        driver.Set(vm => vm.Name, "name").EnsureOk();
        driver.Set(vm => vm.Note, null).EnsureOk();
        Require(model.Note is null, "A nullable string setter did not clear the value.");
        driver.Set(vm => vm.Note, "\"quoted\"").EnsureOk();
        driver.Set(vm => vm.Big, 9007199254740993L).EnsureOk();
        driver.Set(vm => vm.Maybe, null).EnsureOk();
        Require(model.Maybe is null, "A nullable integer setter did not clear the value.");
        driver.Set(vm => vm.Maybe, 5).EnsureOk();
        driver.Set(vm => vm.Flag, null).EnsureOk();
        driver.Set(vm => vm.Ratio, 0.5).EnsureOk();
        driver.Set(vm => vm.Price, 12.50m).EnsureOk();
        driver.Set(vm => vm.Id, id).EnsureOk();
        var reply = driver.Set(vm => vm.Mode, ScalarMode.Fancy).EnsureOk();
        Require(model is { Count: 3, Enabled: true, Name: "name", Note: "\"quoted\"", Big: 9007199254740993L, Maybe: 5, Flag: null,
            Ratio: 0.5, Price: 12.50m, Mode: ScalarMode.Fancy } && model.Id == id, "A typed setter did not set its value.");
        var state = reply.State!;
        Require(state.Read(vm => vm.Big) == 9007199254740993L && state.Get(vm => vm.Big).GetString() == "9007199254740993"
            && state.Read(vm => vm.Mode) == ScalarMode.Fancy && state.Get(vm => vm.Mode).GetString() == "fancy"
            && state.Read(vm => vm.Maybe) == 5 && state.Read(vm => vm.Flag) is null && state.Read(vm => vm.Price) == 12.50m,
            $"The typed state did not read wire values: {state}");

        foreach (var receipt in new[]
        {
            driver.Write(vm => vm.Count, 4), driver.Write(vm => vm.Name, "written"), driver.Write(vm => vm.Note, null),
            driver.Write(vm => vm.Big, -2L), driver.Write(vm => vm.Maybe, null), driver.Write(vm => vm.Flag, false),
            driver.Write(vm => vm.Price, 1m), driver.Write(vm => vm.Mode, ScalarMode.Plain),
        })
            Require(receipt.Kind == "applied", $"A typed checked write was not applied: {receipt}");
        Require(model is { Count: 4, Name: "written", Note: null, Big: -2L, Maybe: null, Flag: false, Price: 1m, Mode: ScalarMode.Plain },
            "A typed checked write did not set its value.");
    }

    private static async Task DriversUseMembersAndStableIdsAsync()
    {
        var model = new RootViewModel();
        using var host = new RunicWindowTestHost<RootViewModel>(model,
            (transport, content, vm) => new RootBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ViewLocator = new TestViewLocator() });
        Require(host.RootRoute == "root" && host.Time is FakeTimeProvider, "The options host did not derive its route or clock.");
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
        var clock = new FakeTimeProvider();
        using var host = new RunicWindowTestHost<ToolkitTypedViewModel>(model,
            (transport, content, vm) => new ToolkitTypedBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { TimeProvider = clock, CreateId = Sequence("page") });
        var waiting = host.Root.Start(vm => vm.CancelCommand, "\"wait\"");
        var close = host.BeginCloseAsync(TimeSpan.FromSeconds(10)).AsTask();
        Require(!close.IsCompleted, "The close finished before its timeout.");
        clock.Advance(TimeSpan.FromSeconds(10));
        var result = await close.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!result.Drained && result.RemainingOperations == 1, "The close did not time out on the fake clock.");
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
