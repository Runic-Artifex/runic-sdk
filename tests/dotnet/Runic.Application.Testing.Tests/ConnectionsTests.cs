using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// D4: IRunicConnections lists a window's page connections, IRunicConnections.Caller is the connection the transport
// authenticated for the running command, and RunicConnectionState keeps a value per connection until it disconnects.
internal sealed class ConnectionsTests
{
    private static readonly RunicConnection Alice = new(InMemoryBridgeTransport.DefaultConnectionKey, InMemoryBridgeTransport.DefaultClientKey);
    private static readonly RunicConnection Bob = new("connection-bob", "client-bob");

    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddRunicReactiveModelContext();
        services.AddRunicWindowService();
        services.AddScoped<ConnectionsViewModel>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, ConnectionsViewModel, IDisposable>>(
            _ => (transport, content, vm) => new ConnectionsBridge(transport, vm, content: content));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Test]
    public async Task TwoConnectionsAreListedAndEachCommandRunsWithItsOwnCallerAsync()
    {
        await using var provider = Provider();
        // OpenWindowAsync binds the Window scope's service when the Bridge attaches, as on Runic Desktop and CS-WebUI.
        await using var window = await new TestWindowHost(provider).OpenWindowAsync<ConnectionsWindow>();
        var host = ((TestWindow<ConnectionsViewModel>)window.Host).Host;
        var vm = window.ViewModel;
        var connections = window.Services.GetRequiredService<IRunicConnections>();
        Require(ReferenceEquals(connections, vm.Connections), "The ViewModel did not receive the Window scope's IRunicConnections.");
        Require(connections.Current.Count == 0 && connections.Caller is null, "A window without pages listed a connection.");

        var changes = new List<IReadOnlyList<RunicConnection>>();
        using var subscription = connections.WhenChanged().Subscribe(new Recorder(changes));

        var alice = host.Root;
        var bob = host.Root.ForConnection(Bob.ConnectionKey, Bob.ClientKey);
        Require(bob.ConnectionKey == Bob.ConnectionKey && bob.ClientKey == Bob.ClientKey && alice.ConnectionKey == Alice.ConnectionKey,
            "ForConnection did not keep its connection.");
        (await alice.ExecuteAsync(model => model.TouchCommand)).ThrowIfFailed();
        (await bob.ExecuteAsync(model => model.TouchCommand)).ThrowIfFailed();
        (await bob.ExecuteAsync(model => model.TouchLaterCommand)).ThrowIfFailed();
        (await alice.ExecuteAsync(model => model.TouchLaterCommand)).ThrowIfFailed();

        Require(vm.Callers.SequenceEqual([Alice, Bob, Bob, Alice]),
            $"The commands did not run with their connections as callers: {string.Join(", ", vm.Callers)}");
        Require(connections.Current.SequenceEqual([Alice, Bob]) && host.Connections.Current.SequenceEqual([Alice, Bob]),
            $"The window did not list both connections in connection order: {string.Join(", ", connections.Current)}");
        Require(vm.Viewers == 2 && (await alice.SnapshotAsync()).Read(model => model.Viewers) == 2,
            "Connected did not run in a model turn whose change the page sees.");
        Require(connections.Caller is null, "The test's own flow, outside a command, has a caller.");
        Require(changes.Count == 3 && changes[0].Count == 0 && changes[2].SequenceEqual([Alice, Bob]),
            $"WhenChanged did not emit the list on subscribe and after each change: {changes.Count}");

        // Each connection keeps its own draft; a disconnect removes and disposes it.
        (await alice.ExecuteAsync<string>(model => model.TypeCommand, "alice's")).ThrowIfFailed();
        (await bob.ExecuteAsync<string>(model => model.TypeCommand, "bob's")).ThrowIfFailed();
        var drafts = vm.Drafts.Values;
        Require(drafts.Count == 2 && drafts[Alice.ConnectionKey].Text == "alice's" && drafts[Bob.ConnectionKey].Text == "bob's",
            "The drafts were not kept per caller.");
        var bobsDraft = drafts[Bob.ConnectionKey];

        host.Transport.CloseConnection(Bob.ConnectionKey);
        await host.Content.ModelContext!.InvokeAsync(static () => { });
        Require(connections.Current.SequenceEqual([Alice]) && vm.Viewers == 1 && vm.Left.SequenceEqual([Bob]),
            "Disconnected did not remove the closed connection.");
        Require(bobsDraft.Disposed && !vm.Drafts.Values.ContainsKey(Bob.ConnectionKey) && !vm.Drafts.TryGet(Bob, out _),
            "The closed connection's draft was not removed, or a new one was created for it.");
        Require(vm.Drafts.TryGet(Alice, out var alicesDraft) && alicesDraft.Text == "alice's", "The open connection lost its draft.");
        Require(changes.Count == 4 && changes[3].SequenceEqual([Alice]), "WhenChanged did not emit the disconnect.");

        // A reconnect is a new connection with an empty draft.
        var again = host.Root.ForConnection("connection-bob-2", Bob.ClientKey);
        (await again.ExecuteAsync(model => model.TouchCommand)).ThrowIfFailed();
        Require(vm.Callers[^1] == new RunicConnection("connection-bob-2", Bob.ClientKey) && connections.Current.Count == 2
            && !vm.Drafts.Values.ContainsKey("connection-bob-2"), "A reconnect was not a new connection.");
    }

    [Test]
    public async Task TheCallerBelongsToItsOwnWindowAndOnlyTheBridgeSetsItAsync()
    {
        await using var provider = Provider();
        var windows = new TestWindowHost(provider);
        await using var first = await windows.OpenWindowAsync<ConnectionsWindow>();
        await using var second = await windows.OpenWindowAsync<ConnectionsWindow>();
        var firstHost = ((TestWindow<ConnectionsViewModel>)first.Host).Host;
        var secondHost = ((TestWindow<ConnectionsViewModel>)second.Host).Host;
        (await firstHost.Root.ExecuteAsync(model => model.TouchCommand)).ThrowIfFailed();
        (await secondHost.Root.ForConnection(Bob.ConnectionKey, Bob.ClientKey).ExecuteAsync(model => model.TouchCommand)).ThrowIfFailed();
        Require(first.ViewModel.Connections.Current.SequenceEqual([Alice]) && second.ViewModel.Connections.Current.SequenceEqual([Bob]),
            "A window listed another window's connection.");

        // A command of the first window that reads the second window's connections has no caller there.
        using (RunicInteractionInvocation.Enter(firstHost.Content, firstHost.RootRoute, Alice.ClientKey, Alice.ConnectionKey))
        {
            Require(first.ViewModel.Connections.Caller == Alice, "The window's own command had no caller.");
            Require(second.ViewModel.Connections.Caller is null, "Another window's command was reported as a caller.");
        }
        // The window scope and code that enters a window carry no identity.
        using (RunicInteractionInvocation.EnterWindow(first.ViewModel))
            Require(first.ViewModel.Connections.Caller is null, "The window scope reported a caller.");
        // A connection that has not completed its hello is not listed, and its state is not created.
        Require(!first.ViewModel.Drafts.TryGet(new RunicConnection("connection-unknown", "client-1"), out _),
            "A value was created for a connection the window does not have.");
    }

    [Test]
    public async Task OutsideAWindowScopeTheServiceIsEmptyAndHandlersAreIsolatedAsync()
    {
        // As in a WPF RunicViewHost, which creates no Window scope.
        await using var provider = Provider();
        await using var scope = provider.CreateAsyncScope();
        var unbound = scope.ServiceProvider.GetRequiredService<IRunicConnections>();
        Require(unbound.Current.Count == 0 && unbound.Caller is null, "An unbound IRunicConnections reported a connection.");

        // A handler that throws is logged; the other handlers still run.
        using var host = RunicWindowTestHost.Create(scope.ServiceProvider, new ConnectionsViewModel(unbound));
        var seen = 0;
        host.Connections.Connected += (_, _) => throw new InvalidOperationException("handler");
        host.Connections.Connected += (_, _) => seen++;
        (await host.Root.ExecuteAsync(model => model.TouchCommand)).ThrowIfFailed();
        await host.Content.ModelContext!.InvokeAsync(static () => { });
        Require(seen == 1 && host.Connections.Current.SequenceEqual([Alice]), "A throwing handler stopped the others.");
        // RunicWindowTestHost.Create binds the IRunicConnections of the services it was given.
        Require(unbound.Current.SequenceEqual([Alice]) && host.ViewModel.Callers.SequenceEqual([Alice]),
            "Create did not bind the services' IRunicConnections to the test window.");
    }

    private sealed class Recorder(List<IReadOnlyList<RunicConnection>> changes) : IObserver<IReadOnlyList<RunicConnection>>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(IReadOnlyList<RunicConnection> value) { lock (changes) changes.Add(value); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
