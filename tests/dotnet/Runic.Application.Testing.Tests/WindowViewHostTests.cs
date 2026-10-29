using System.Reflection;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// The Window/View host checks that the former Program.cs ran first. The root host case counts ChildView's static
// lifetime calls, so the other cases that present a ChildView (HostDriverTests, ServiceHostTests) depend on it and
// run after it, as they did in the former runner.
internal sealed class WindowViewHostTests
{
    [Test]
    public async Task GeneratedRootHostPresentsAndReleasesEachBrowserViewAsync()
    {
        var model = new RootViewModel();
        using (var host = new RunicWindowTestHost<RootViewModel>(model,
            (transport, content, vm) => new RootBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "root", ViewLocator = new TestViewLocator() }))
        {
            using var initial = host.RawSnapshot();
            Require(initial.RootElement.GetProperty("state").GetProperty("count").GetInt32() == 0,
                "The generated root snapshot did not include the initial count.");
            // Protocol 4: the hello reply, not the snapshot, names the protocol and the implementation's version.
            using var hello = JsonDocument.Parse(host.Transport.Request(BridgeRequests.Json(writer =>
            {
                writer.WriteString("kind", "hello");
                writer.WriteNumber("protocol", BridgeProtocol.Version);
                writer.WriteString("session", "version-check");
                writer.WriteStartObject("client");
                writer.WriteString("name", "Runic.Application.Testing.Tests");
                writer.WriteString("version", "1.0.0");
                writer.WriteEndObject();
            }), "connection-version"));
            Require(hello.RootElement.GetProperty("protocol").GetInt32() == BridgeProtocol.Version,
                "The hello did not report its wire protocol version.");
            // D02: the package version lets the browser runtime report an npm package of another release.
            var version = hello.RootElement.GetProperty("implementation").GetProperty("version").GetString()!;
            Require(version == typeof(BridgeProtocol).Assembly
                    .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0]
                && !version.Contains('+', StringComparison.Ordinal),
                $"The hello reported version {version}.");
            var child = Reference(initial);
            var childView = host.View<ChildViewModel>(child);
            Require(childView.Mount("first:child", "first", "connection-one") == "first:child",
                "The first View mount failed.");
            Require(childView.Mount("second:child", "second", "connection-two") == "second:child",
                "The second View mount failed.");
            Require(ChildView.Attached == 2 && ChildView.Mounted == 2,
                "Each browser presentation must get a fresh View.");
            Require(model.Child?.Title == "mounted", "The mounted View lifetime did not mutate on the model context.");
            Require(await PublishedAsync(childView, publication => Published(publication, "title")?.GetString() == "mounted"),
                "A property change from the mounted View lifetime did not publish.");

            using var childState = host.RawSnapshot(child);
            Require(childState.RootElement.GetProperty("state").GetProperty("title").GetString() == "mounted",
                "The routed View snapshot was not available.");
            var setReply = childView.Set(vm => vm.Title, "edited");
            Require(model.Child?.Title == "edited", $"The generated setter did not update the model: {setReply}");
            _ = await host.Root.ExecuteAsync(vm => vm.IncrementCommand);
            Require(model.Count == 1, "The generated command did not run.");
            Require(await PublishedAsync(host.Root, publication => Published(publication, "count")?.GetInt32() == 1),
                "Publications were not captured and drained.");

            host.Transport.CloseConnection("connection-one");
            Require(ChildView.Detached == 1 && ChildView.Unmounted == 1 && ChildView.Disposed == 1,
                "Disconnect did not release only the first presentation.");
            childView.Unmount("second:child", "second", "connection-two");
            Require(ChildView.Detached == 2 && ChildView.Unmounted == 2 && ChildView.Disposed == 2,
                "Unmount did not release the second presentation.");

            model.ClearChild();
            using var afterClear = host.RawSnapshot();
            Require(afterClear.RootElement.GetProperty("state").GetProperty("child").ValueKind == JsonValueKind.Null,
                "The cleared content was still in the generated snapshot.");
            Require(!BridgeDispatcher.For(host.Transport).Serves($"content{child.Id}"),
                "The removed content route remained bound.");
            var close = await host.DrainAsync(TimeSpan.Zero);
            Require(close.Drained && close.RemainingOperations == 0, "The idle Window did not close cleanly.");
        }
    }

    [Test]
    public async Task InMemoryTransportBindsAsynchronousAndUniqueRoutesAsync()
    {
        using (var transport = new InMemoryBridgeTransport())
        {
            // Protocol 4: routes are served by the window's dispatcher; the transport carries their requests.
            var dispatcher = BridgeDispatcher.For(transport);
            var route = new AwaitingRoute();
            var binding = dispatcher.RegisterRoute("await", route);
            const string request = """{"kind":"snapshot","route":"await"}""";
            Require(await transport.SendAsync(request) == """{"ok":true}""", "Asynchronous routes did not complete.");
            Require(Throws<InvalidOperationException>(() => dispatcher.RegisterRoute("await", route)),
                "A duplicate route was accepted.");
            binding.Dispose();
            using var released = JsonDocument.Parse(await transport.SendAsync(request));
            Require(released.RootElement.GetProperty("error").GetProperty("kind").GetString() == "released",
                "An unbound route remained callable.");
        }
    }

    private sealed class AwaitingRoute : IBridgeRouteHandler
    {
        public async ValueTask<ReadOnlyMemory<byte>> HandleAsync(BridgeRouteRequest request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return BridgeMessages.Ok;
        }
    }

    [Test]
    public async Task GeneratedReactiveHostBridgesReactiveUIPropertiesAndCommandsAsync()
    {
        using var generatedReactiveModel = new GeneratedReactiveViewModel();
        using (var generatedReactiveHost = new RunicWindowTestHost<GeneratedReactiveViewModel>(generatedReactiveModel,
            (transport, content, vm) => new GeneratedReactiveBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "generatedReactive", ViewLocator = new TestViewLocator() }))
        {
            using var initial = generatedReactiveHost.RawSnapshot();
            var initialState = initial.RootElement.GetProperty("state");
            Require(initialState.GetProperty("name").GetString() == string.Empty,
                "The generated ReactiveUI scalar did not reach the bridge snapshot.");
            Require(initialState.GetProperty("summary").GetString() == "summary:",
                "The ReactiveUI.Binding OAPH did not reach the bridge snapshot.");
            Require(!initialState.GetProperty("canSave").GetBoolean(),
                "The generated ReactiveUI command started enabled for an empty name.");
            var driver = generatedReactiveHost.Root;
            var disabledReply = await driver.ExecuteAsync(vm => vm.SaveCommand);
            Require(!disabledReply.Succeeded && disabledReply is { ErrorKind: "rejected", ErrorReason: "unavailable" },
                $"The disabled generated ReactiveUI command did not reject at the bridge boundary: {disabledReply}");
            Require(generatedReactiveModel.SaveCount == 0,
                "The disabled generated ReactiveUI command changed state.");

            _ = driver.Set(vm => vm.Name, "note");
            Require(generatedReactiveModel.Summary == "summary:note",
                "WhenAnyValue did not update the ReactiveUI.Binding OAPH.");
            Require(await PublishedAsync(driver, publication => Published(publication, "summary")?.GetString() == "summary:note"),
                "A ReactiveUI generated-property change did not publish through the bridge.");

            using var afterName = generatedReactiveHost.RawSnapshot();
            Require(afterName.RootElement.GetProperty("state").GetProperty("canSave").GetBoolean(),
                "CanExecuteChanged from the generated ReactiveUI command did not reach the bridge state.");
            // Protocol 4: the reply carries the revision; the state at it includes the command's effects.
            var saveReply = await driver.ExecuteAsync(vm => vm.SaveCommand);
            Require(saveReply.Succeeded && saveReply.State?.Read(vm => vm.SaveCount) == 1,
                $"The generated asynchronous ReactiveUI command did not return its successful state: {saveReply}");
            Require(generatedReactiveModel.SaveCount == 1,
                "The generated asynchronous ReactiveUI command was not invoked by the bridge.");
            Require(await PublishedAsync(driver, publication => Published(publication, "saveCount")?.GetInt32() == 1),
                "The generated command output state did not publish through the bridge.");
        }
    }

    private static async Task<bool> PublishedAsync<TModel>(RunicViewDriver<TModel> driver, Func<RunicViewPublication, bool> predicate)
        where TModel : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (driver.TakePublications().Any(predicate)) return true;
            await Task.Delay(10);
        }
        return false;
    }

    // The value a state push or a frame's set change published for a member, or null.
    private static JsonElement? Published(RunicViewPublication publication, string member)
    {
        if (publication.State is { } state && state.TryGetProperty(member, out var value)) return value;
        return publication.Sets.LastOrDefault(set => set.Field == member)?.Value;
    }

    private static PageReference Reference(JsonDocument state)
    {
        var page = state.RootElement.GetProperty("state").GetProperty("child");
        return new(page.GetProperty("kind").GetString()!, page.GetProperty("id").GetString()!);
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
