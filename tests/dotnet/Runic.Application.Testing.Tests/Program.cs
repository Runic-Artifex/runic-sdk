using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Testing.Tests;
using Runic.Application.Views;

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

    using var childState = host.Snapshot(child);
    Require(childState.RootElement.GetProperty("state").GetProperty("title").GetString() == "initial",
        "The routed View snapshot was not available.");
    _ = host.Transport.Call($"content{child.Id}SetTitle", new(StringValue: "edited"));
    Require(model.Child?.Title == "edited", "The generated setter did not update the model.");
    _ = host.Transport.Call("rootIncrement");
    Require(model.Count == 1, "The generated command did not run.");
    Require(host.Transport.DrainPublications().Any(publication => publication.Route == "root")
        && host.Transport.DrainPublications().Count == 0,
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

Console.WriteLine("Runic.Application.Testing Window/View host passed.");

static PageReference Reference(JsonDocument state)
{
    var page = state.RootElement.GetProperty("state").GetProperty("child");
    return new(page.GetProperty("kind").GetString()!, page.GetProperty("id").GetString()!);
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
