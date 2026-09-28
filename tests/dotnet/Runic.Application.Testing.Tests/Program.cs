using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Testing.Tests;
using Runic.Application.Views;

if (args is ["--export-generated-client-fixture", var fixturePath])
{
    await GeneratedClientFixtureExporter.WriteAsync(fixturePath);
    return;
}

var model = new RootViewModel();
using (var host = new RunicWindowTestHost<RootViewModel>(model, "root",
    (transport, content, vm) => new RootBridge(transport, vm, content: content),
    new TestViewLocator()))
{
    using var initial = host.Snapshot();
    Require(initial.RootElement.GetProperty("state").GetProperty("count").GetInt32() == 0,
        "The generated root snapshot did not include the initial count.");
    var child = Reference(initial);
    Require(host.Mount(child, "first:child", "first", "connection-one") == "ok",
        "The first View mount failed.");
    Require(host.Mount(child, "second:child", "second", "connection-two") == "ok",
        "The second View mount failed.");
    Require(ChildView.Attached == 2 && ChildView.Mounted == 2,
        "Each browser presentation must get a fresh View.");
    Require(model.Child?.Title == "mounted", "The mounted View lifetime did not mutate on the model context.");
    Require(await PublishedAsync(host.Transport, publication =>
        publication.Route == $"content{child.Id}" && publication.StateJson.Contains("mounted", StringComparison.Ordinal)),
        "A property change from the mounted View lifetime did not publish.");

    using var childState = host.Snapshot(child);
    Require(childState.RootElement.GetProperty("state").GetProperty("title").GetString() == "mounted",
        "The routed View snapshot was not available.");
    var setReply = host.Transport.Call($"content{child.Id}SetTitle", new(StringValue: "edited"));
    Require(model.Child?.Title == "edited", $"The generated setter did not update the model: {setReply}");
    _ = host.Transport.Call("rootIncrement");
    Require(model.Count == 1, "The generated command did not run.");
    Require(await PublishedAsync(host.Transport, publication => publication.Route == "root"),
        "Publications were not captured and drained.");

    host.Content.ReleaseConnection("connection-one");
    Require(ChildView.Detached == 1 && ChildView.Unmounted == 1 && ChildView.Disposed == 1,
        "Disconnect did not release only the first presentation.");
    Require(host.Unmount(child, "second:child", "second", "connection-two") == "ok",
        "The remaining presentation could not unmount.");
    Require(ChildView.Detached == 2 && ChildView.Unmounted == 2 && ChildView.Disposed == 2,
        "Unmount did not release the second presentation.");

    model.ClearChild();
    using var afterClear = host.Snapshot();
    Require(afterClear.RootElement.GetProperty("state").GetProperty("child").ValueKind == JsonValueKind.Null,
        "The cleared content was still in the generated snapshot.");
    Require(!host.Transport.Routes.Contains($"content{child.Id}Snapshot"),
        "The removed content route remained bound.");
    var close = await host.BeginCloseAsync(TimeSpan.Zero);
    Require(close.Drained && close.RemainingOperations == 0, "The idle Window did not close cleanly.");
}

using (var transport = new InMemoryViewTransport())
{
    using var binding = transport.BindAsync("await", async (_, cancellationToken) =>
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return "done";
    });
    Require(await transport.CallAsync("await") == "done", "Asynchronous routes did not complete.");
    Require(Throws<InvalidOperationException>(() => transport.Bind("await", _ => "duplicate")),
        "A duplicate route was accepted.");
    binding.Dispose();
    Require(Throws<KeyNotFoundException>(() => transport.Call("await")),
        "An unbound route remained callable.");
}

using var generatedReactiveModel = new GeneratedReactiveViewModel();
using (var generatedReactiveHost = new RunicWindowTestHost<GeneratedReactiveViewModel>(generatedReactiveModel,
    "generatedReactive", (transport, content, vm) => new GeneratedReactiveBridge(transport, vm, content: content),
    new TestViewLocator()))
{
    using var initial = generatedReactiveHost.Snapshot();
    var initialState = initial.RootElement.GetProperty("state");
    Require(initialState.GetProperty("name").GetString() == string.Empty,
        "The generated ReactiveUI scalar did not reach the bridge snapshot.");
    Require(initialState.GetProperty("summary").GetString() == "summary:",
        "The ReactiveUI.Binding OAPH did not reach the bridge snapshot.");
    Require(!initialState.GetProperty("canSave").GetBoolean(),
        "The generated ReactiveUI command started enabled for an empty name.");
    using (var disabledReply = JsonDocument.Parse(await generatedReactiveHost.Transport.CallAsync("generatedReactiveSave")))
    {
        Require(!disabledReply.RootElement.GetProperty("ok").GetBoolean()
            && disabledReply.RootElement.GetProperty("error").GetProperty("kind").GetString() == "rejected",
            "The disabled generated ReactiveUI command did not reject at the bridge boundary.");
        Require(generatedReactiveModel.SaveCount == 0,
            "The disabled generated ReactiveUI command changed state.");
    }

    _ = generatedReactiveHost.Transport.Call("generatedReactiveSetName", new(StringValue: "note"));
    Require(generatedReactiveModel.Summary == "summary:note",
        "WhenAnyValue did not update the ReactiveUI.Binding OAPH.");
    Require(await PublishedAsync(generatedReactiveHost.Transport, publication =>
        publication.Route == "generatedReactive" && publication.StateJson.Contains("summary:note", StringComparison.Ordinal)),
        "A ReactiveUI generated-property change did not publish through the bridge.");

    using var afterName = generatedReactiveHost.Snapshot();
    Require(afterName.RootElement.GetProperty("state").GetProperty("canSave").GetBoolean(),
        "CanExecuteChanged from the generated ReactiveUI command did not reach the bridge state.");
    using var saveReply = JsonDocument.Parse(await generatedReactiveHost.Transport.CallAsync("generatedReactiveSave"));
    Require(saveReply.RootElement.GetProperty("ok").GetBoolean()
        && saveReply.RootElement.GetProperty("state").GetProperty("saveCount").GetInt32() == 1,
        "The generated asynchronous ReactiveUI command did not return its successful state.");
    Require(generatedReactiveModel.SaveCount == 1,
        "The generated asynchronous ReactiveUI command was not invoked by the bridge.");
    Require(await PublishedAsync(generatedReactiveHost.Transport, publication =>
        publication.Route == "generatedReactive" && PublishedInteger(publication.StateJson, "saveCount") == 1),
        "The generated command output state did not publish through the bridge.");
}

CheckedDataTests.Run();
DataCodecTests.Run();
await DataShapeTests.RunAsync();
await OperationResultTests.RunAsync();
await ModelContextTests.RunAsync();
await TypedReactiveTests.RunAsync();
await SnapshotDeliveryTests.RunAsync();
await InteractionFixture.VerifyAsync();
await GeneratedInteractionTests.RunAsync();
await GeneratedClientHarness.RunAsync();
await GeneratedInteractionClientHarness.RunAsync();

Console.WriteLine("Runic.Application.Testing Window/View host passed.");

static async Task<bool> PublishedAsync(InMemoryViewTransport transport, Func<ViewTestPublication, bool> predicate)
{
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline)
    {
        if (transport.DrainPublications().Any(predicate)) return true;
        await Task.Delay(10);
    }
    return false;
}

static PageReference Reference(JsonDocument state)
{
    var page = state.RootElement.GetProperty("state").GetProperty("child");
    return new(page.GetProperty("kind").GetString()!, page.GetProperty("id").GetString()!);
}

static int? PublishedInteger(string stateJson, string property)
{
    using var state = JsonDocument.Parse(stateJson);
    return state.RootElement.TryGetProperty(property, out var value) && value.TryGetInt32(out var integer)
        ? integer : null;
}

static bool Throws<T>(Action action) where T : Exception
{
    try { action(); return false; }
    catch (T) { return true; }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
