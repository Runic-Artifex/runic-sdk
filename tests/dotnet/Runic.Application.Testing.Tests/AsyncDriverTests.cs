using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views.ReactiveUI;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// A1: the asynchronous driver paths never block a thread, typed command inputs follow the Bridge's JSON rules, and
// recorded pushes name their kind and route.
internal sealed class AsyncDriverTests
{
    private static RunicWindowTestHost<AsyncDriverViewModel> Host(AsyncDriverViewModel model) =>
        new(model, (transport, content, vm) => new AsyncDriverBridge(transport, vm, content: content));

    [Test]
    public async Task AsyncPathsReturnBeforeTheWindowAnswersAsync()
    {
        var model = new AsyncDriverViewModel();
        using var host = Host(model);
        // The command waits for the test, so a path that blocked would never return its task.
        var execute = host.Root.ExecuteAsync(vm => vm.WaitCommand);
        Require(!execute.IsCompleted, "ExecuteAsync blocked or finished before the command did.");
        // Concurrent requests on the same connection share its hello and answer while the command runs.
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => host.Root.SnapshotAsync()));
        Require(snapshots.All(state => state.Read(vm => vm.Title) == ""), "A snapshot did not answer while the command ran.");
        Require((await host.Root.SetAsync(vm => vm.Title, "set")).ThrowIfFailed().State?.Read(vm => vm.Title) == "set",
            "SetAsync did not read the state after its reply.");
        model.Release();
        var reply = (await execute).ThrowIfFailed();
        Require(reply.State?.Read(vm => vm.Title) == "released", $"ExecuteAsync did not read the state after the command: {reply}");
        var presentation = await host.Root.MountAsync();
        await host.Root.UnmountAsync(presentation);
        var tracker = await host.Root.TrackAsync();
        await host.Root.SetAsync(vm => vm.Title, "tracked");
        await tracker.WaitUntilAsync(state => state.Read(vm => vm.Title) == "tracked");
        await tracker.VerifyAsync();
    }

    [Test]
    public async Task CheckedWritesAndScalarSettersRunAsynchronouslyAsync()
    {
        var model = new ScalarSetterViewModel();
        using var host = new RunicWindowTestHost<ScalarSetterViewModel>(model,
            (transport, content, vm) => new ScalarSetterBridge(transport, vm, content: content));
        (await host.Root.SetAsync(vm => vm.Mode, ScalarMode.Fancy)).ThrowIfFailed();
        var receipt = await host.Root.WriteAsync(vm => vm.Count, 4);
        Require(receipt.Kind == RunicFieldWriteReceiptKind.Applied && model is { Count: 4, Mode: ScalarMode.Fancy },
            $"An asynchronous write or setter was not applied: {receipt}");
        var stale = await host.Root.WriteAsync(vm => vm.Count, 5, expectedVersion: 0);
        Require(stale.Kind == RunicFieldWriteReceiptKind.Conflict, $"A stale asynchronous write was not a conflict: {stale}");
    }

    [Test]
    public async Task TypedInputsEncodeLikeTheGeneratedClientAsync()
    {
        var model = new AsyncDriverViewModel();
        using var host = Host(model);
        var input = new AsyncDriverInput("Groceries", 9007199254740993L, AsyncDriverPriority.High, [1, 2],
            new() { ["aisle"] = "3" }, new AsyncDriverCountNote(4), new AsyncDriverMoney(12.50m), null, "groc");
        (await host.Root.ExecuteAsync(vm => vm.ApplyCommand, input)).ThrowIfFailed();
        var last = model.Last;
        Require(last is { Title: "Groceries", Big: 9007199254740993L, Priority: AsyncDriverPriority.High, Comment: null,
                ShortName: "groc", Note: AsyncDriverCountNote { Count: 4 } }
            && last.Counts.SequenceEqual([1, 2]) && last.Labels["aisle"] == "3" && last.Price.Value == 12.50m,
            $"The typed input did not arrive as sent: {JsonSerializer.Serialize(last)}");

        Require(await host.Root.CanExecuteAsync(vm => vm.ApplyCommand, input), "CanExecuteAsync did not encode the typed input.");
        (await host.Root.ExecuteAsync<string>(vm => vm.SearchCommand, "milk")).ThrowIfFailed();
        Require(model.Title == "milk", $"A typed string input arrived as '{model.Title}'.");
        // A string passed to the wire JSON overload must be JSON, and the error names the typed overload.
        var error = await Catch<ArgumentException>(() => host.Root.ExecuteAsync(vm => vm.SearchCommand, "milk"));
        Require(error?.Message.Contains("ExecuteAsync<string>", StringComparison.Ordinal) == true,
            $"Text that is not JSON was not reported: {error}");
        var token = await Catch<ArgumentException>(() => host.Root.ExecuteAsync(vm => vm.SearchCommand, CancellationToken.None));
        Require(token is not null, "A cancellation token was accepted as the command input.");
    }

    [Test]
    public async Task MessagesNameTheirKindAndRouteAsync()
    {
        var model = new AsyncDriverViewModel();
        using var host = Host(model);
        await host.Root.MountAsync();
        _ = host.Transport.TakeMessages();
        model.Title = "pushed";
        await host.Root.NextPublicationAsync();
        var messages = host.Transport.TakeMessages();
        Require(messages.Any(message => message.Route == host.RootRoute && message.Kind is "frame" or "state"),
            $"No push named its route and kind: {string.Join(", ", messages)}");
        var copy = messages[0] with { Json = """{"kind":"failure","route":"other"}""" };
        Require(copy is { Kind: "failure", Route: "other" }, "A copied message kept the header of its original JSON.");
        Require(new RunicBridgeMessage("[]", null) is { Kind: null, Route: null }, "A push without a header named one.");
    }

    [Test]
    public async Task HandBuiltViewModelsAndWindowScopesAreReachableAsync()
    {
        var services = new ServiceCollection();
        services.AddRunicReactiveModelContext();
        services.AddScoped<AsyncDriverMarker>();
        services.AddScoped<AsyncDriverViewModel>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, AsyncDriverViewModel, IDisposable>>(
            _ => (transport, content, vm) => new AsyncDriverBridge(transport, vm, content: content));
        await using var provider = services.BuildServiceProvider();

        // A ViewModel the test built needs no registration of its own.
        var built = new AsyncDriverViewModel();
        using (var host = RunicWindowTestHost.Create(provider, built))
        {
            Require(ReferenceEquals(host.ViewModel, built) && ReferenceEquals(host.Services, provider),
                "Create did not use the hand-built ViewModel and keep its services.");
            Require((await host.Root.SetAsync(vm => vm.Title, "built")).Succeeded && built.Title == "built",
                "The hand-built ViewModel's Bridge did not answer.");
        }

        // A Window's scope: the services that resolved its ViewModel.
        Require(Throws(() => new AsyncDriverWindow().Services), "A Window that has not opened reported services.");
        await using var opened = await new TestWindowHost(provider).OpenWindowAsync<AsyncDriverWindow>();
        Require(ReferenceEquals(opened.Services.GetRequiredService<AsyncDriverViewModel>(), opened.ViewModel)
            && !ReferenceEquals(opened.Services, provider)
            && ReferenceEquals(((IRunicWindow)opened).Services, opened.Services),
            "Window.Services is not the scope that resolved the Window's ViewModel.");
        Require(ReferenceEquals(opened.Services.GetRequiredService<AsyncDriverMarker>(), opened.Services.GetRequiredService<AsyncDriverMarker>())
            && !ReferenceEquals(opened.Services.GetRequiredService<AsyncDriverMarker>(), provider.CreateScope().ServiceProvider.GetRequiredService<AsyncDriverMarker>()),
            "Window.Services did not return the Window's scoped services.");
    }

    private static bool Throws(Func<object> read)
    {
        try { _ = read(); return false; }
        catch (InvalidOperationException) { return true; }
    }

    private static async Task<TException?> Catch<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); return null; }
        catch (TException error) { return error; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
