using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Navigation;
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

        // A transport that reuses Bob's closed key, as CS-WebUI reuses WebUI's slots: the window gives it a new key.
        var reused = host.Root.ForConnection(Bob.ConnectionKey, Bob.ClientKey);
        (await reused.ExecuteAsync(model => model.TouchCommand)).ThrowIfFailed();
        var reusedConnection = new RunicConnection(Bob.ConnectionKey + "#2", Bob.ClientKey);
        Require(vm.Callers[^1] == reusedConnection && connections.Current.Contains(reusedConnection) && !connections.Current.Contains(Bob),
            $"The reused transport key did not get a new connection key: {vm.Callers[^1]}");
        Require(!vm.Drafts.TryGet(Bob, out _), "The closed connection's key reached the connection that reused it.");
    }

    [Test]
    public async Task AConnectionThatClosesBeforeItsConnectedTurnIsNeverReportedAndKeepsNoValueAsync()
    {
        await using var provider = Provider();
        await using var window = await new TestWindowHost(provider).OpenWindowAsync<ConnectionsWindow>();
        var host = ((TestWindow<ConnectionsViewModel>)window.Host).Host;
        var vm = window.ViewModel;
        var reported = 0;
        vm.Connections.Connected += (_, _) => reported++;
        vm.Connections.Disconnected += (_, _) => reported++;

        // The model context is busy, so the Connected turn of the new page waits.
        var release = new TaskCompletionSource();
        var busy = host.Content.ModelContext!.InvokeAsync(() => release.Task.Wait());
        host.Transport.Request(Hello("s-early"), "connection-early", "client-early");
        var early = new RunicConnection("connection-early", "client-early");
        Require(!vm.Drafts.TryGet(early, out _), "A value was created for a connection before its Connected turn.");
        host.Transport.CloseConnection("connection-early");
        release.SetResult();
        await busy;
        await Turn(host);
        Require(reported == 0 && vm.Connections.Current.Count == 0 && vm.Drafts.Values.Count == 0 && vm.Viewers == 0,
            "A connection that closed before its Connected turn was reported or kept a value.");
    }

    [Test]
    public async Task ACommandWhoseConnectionClosesMidAwaitKeepsItsCallerButNotItsStateAsync()
    {
        await using var provider = Provider();
        await using var window = await new TestWindowHost(provider).OpenWindowAsync<ConnectionsWindow>();
        var host = ((TestWindow<ConnectionsViewModel>)window.Host).Host;
        var vm = window.ViewModel;
        var bob = host.Root.ForConnection(Bob.ConnectionKey, Bob.ClientKey);
        (await bob.ExecuteAsync<string>(model => model.TypeCommand, "bob's")).ThrowIfFailed();
        var bobsDraft = vm.Drafts.Values[Bob.ConnectionKey];

        var hold = bob.ExecuteAsync(model => model.HoldCommand);
        await vm.HoldStarted.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
        host.Transport.CloseConnection(Bob.ConnectionKey);
        await Turn(host);
        // Another page gets Bob's transport key while his command still awaits.
        var next = host.Root.ForConnection(Bob.ConnectionKey, Bob.ClientKey);
        (await next.ExecuteAsync(model => model.TouchCommand)).ThrowIfFailed();
        var nextConnection = new RunicConnection(Bob.ConnectionKey + "#2", Bob.ClientKey);
        Require(vm.Callers[^1] == nextConnection, $"The page that reused the key was {vm.Callers[^1]}.");

        vm.HoldGate.SetResult();
        var (caller, hasDraft) = await vm.HoldDone.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
        Require(caller == Bob, $"The resumed command's caller was {caller}, not its own closed connection.");
        Require(!hasDraft && bobsDraft.Disposed && !vm.Drafts.Values.ContainsKey(nextConnection.ConnectionKey),
            "The resumed command got a draft for its closed connection, or the next page's.");
        try { await hold.WaitScaledAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error) when (error is not TimeoutException) { }
    }

    [Test]
    public async Task WorkThatOutlivesItsCommandHasNoCallerAndFollowsTheRulesOutsideAScopeAsync()
    {
        await using var provider = Provider();
        await using var window = await new TestWindowHost(provider).OpenWindowAsync<ConnectionsWindow>();
        var host = ((TestWindow<ConnectionsViewModel>)window.Host).Host;
        var vm = window.ViewModel;
        using var alicePage = (await host.Root.MountPresentationAsync(clientKey: Alice.ClientKey, connectionKey: Alice.ConnectionKey))
            .Handle(model => model.Confirm, _ => true);
        using var bobPage = (await host.Root.MountPresentationAsync(clientKey: Bob.ClientKey, connectionKey: Bob.ConnectionKey))
            .Handle(model => model.Confirm, _ => true);
        (await host.Root.ForConnection(Bob.ConnectionKey, Bob.ClientKey).ExecuteAsync(model => model.StartBackgroundCommand)).ThrowIfFailed();

        // The command ended; its background work runs on. Outside a scope, a question with two pages that handle it asks
        // neither and uses its fallback, instead of asking the page whose command ended.
        vm.BackgroundGate.SetResult();
        var (caller, inCommand, answer) = await vm.Background!.WaitScaledAsync(TimeSpan.FromSeconds(5));
        Require(inCommand == Bob && caller is null, $"Work that outlived its command kept the caller {caller}.");
        Require(!answer, "A question asked after the command ended still reached the command's page.");

        // EnterWindow inside such work is the documented way to reach the window's page.
        bobPage.Unmount();
        bool windowAnswer;
        using (RunicInteractionInvocation.EnterWindow(vm))
            windowAnswer = await vm.Confirm.AskAsync(true).WaitScaledAsync(TimeSpan.FromSeconds(5));
        Require(windowAnswer, "The window scope did not ask the window's single page.");
    }

    [Test]
    public async Task OwnKeysOnlyLoggedFailuresAndHandlersOutsideAnyInvocationAsync()
    {
        await using var provider = Provider();
        await using var scope = provider.CreateAsyncScope();
        var logs = new LogCapture();
        var vm = new ConnectionsViewModel(scope.ServiceProvider.GetRequiredService<IRunicConnections>());
        using var host = RunicWindowTestHost.Create(scope.ServiceProvider, vm, new RunicWindowTestHostOptions { LoggerFactory = logs });
        var handlerRan = false;
        RunicInteractionInvocation? handlerInvocation = null;
        RunicConnection? handlerCaller = null;
        vm.Connections.Connected += (_, _) => throw new InvalidOperationException("handler");
        vm.Connections.Connected += (_, _) =>
        {
            handlerRan = true;
            handlerInvocation = RunicInteractionInvocation.Current;
            handlerCaller = vm.Connections.Caller;
        };

        // A page connects while the reporting thread runs in another page's live invocation; its handlers run in neither.
        using (RunicInteractionInvocation.Enter(host.Content, host.RootRoute, Alice.ClientKey, Alice.ConnectionKey))
        {
            host.Transport.Request(Hello("s-carol"), "connection-carol", "client-carol");
            await Turn(host);
        }
        var carol = new RunicConnection("connection-carol", "client-carol");
        Require(handlerRan && vm.Connections.Current.SequenceEqual([carol]) && vm.Viewers == 1,
            "The handlers after a throwing one did not run.");
        Require(handlerInvocation is null && handlerCaller is null, "A Connected handler ran in the reporting thread's invocation.");
        Require(logs.Entries.Any(entry => entry.EventId.Id == 1230 && entry.Exception?.Message == "handler"),
            "The throwing handler was not logged as event 1230.");

        // TryGet uses the window's own connection, never a client key the caller made up.
        Require(!vm.Drafts.TryGet(carol with { ClientKey = "client-mallory" }, out _), "A forged client key got a value.");
        Require(vm.Drafts.TryGet(carol, out var carolsDraft) && ReferenceEquals(carolsDraft.Connection, vm.Connections.Current[0]),
            "The value was not created with the window's own connection.");
        Require(!vm.Drafts.TryGet(carol with { ClientKey = "client-mallory" }, out _), "A forged client key read an existing value.");

        // Disposal on disconnect and on Dispose; one throwing value does not stop the others.
        (await host.Root.ExecuteAsync<string>(model => model.TypeCommand, "alice's")).ThrowIfFailed();
        var bob = host.Root.ForConnection(Bob.ConnectionKey, Bob.ClientKey);
        (await bob.ExecuteAsync<string>(model => model.TypeCommand, "bob's")).ThrowIfFailed();
        var drafts = vm.Drafts.Values;
        foreach (var draft in drafts.Values) draft.ThrowOnDispose = true;
        host.Transport.CloseConnection(Bob.ConnectionKey);
        await Turn(host);
        Require(drafts[Bob.ConnectionKey].Disposed && !vm.Drafts.Values.ContainsKey(Bob.ConnectionKey) && vm.Left.SequenceEqual([Bob]),
            "The disconnected connection's draft was not disposed and removed.");
        vm.Dispose();
        Require(drafts[Alice.ConnectionKey].Disposed && drafts[carol.ConnectionKey].Disposed && vm.Drafts.Values.Count == 0,
            "A throwing Dispose stopped the others.");
        Require(logs.Entries.Count(entry => entry.EventId.Id == 1231) == 3, "The throwing disposals were not logged as event 1231.");
    }

    [Test]
    public async Task AClosingWindowFollowsItsConnectionsWithoutRaisingEventsAsync()
    {
        using var transport = new InMemoryBridgeTransport();
        await using var context = new RefusingContext();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { ModelContext = context });
        using var connections = new RunicConnections();
        connections.Bind(session);
        var raised = 0;
        connections.Connected += (_, _) => raised++;
        connections.Disconnected += (_, _) => raised++;
        transport.Request(Hello("s-late"), "connection-late", "client-late");
        Require(connections.Current.SequenceEqual([new RunicConnection("connection-late", "client-late")]) && raised == 0,
            "A window whose context takes no turns did not follow its connection, or raised an event outside a turn.");
        transport.CloseConnection("connection-late");
        Require(connections.Current.Count == 0 && raised == 0, "The close was not followed, or raised an event outside a turn.");
    }

    private static string Hello(string session) => JsonSerializer.Serialize(new
    {
        kind = "hello", protocol = 4, session, client = new { name = "t", version = "1" },
    });

    private static async Task Turn<T>(RunicWindowTestHost<T> host) where T : class =>
        await host.Content.ModelContext!.InvokeAsync(static () => { });

    // A model context of a closing window: it takes no posted turns.
    private sealed class RefusingContext : IRunicModelContext
    {
        public bool IsExecuting => false;
        public event EventHandler<RunicModelTurnExceptionEventArgs>? UnhandledTurnException { add { } remove { } }
        public bool TryPost(Action turn) => false;
        public ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default) { turn(); return ValueTask.CompletedTask; }
        public ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default) => ValueTask.FromResult(turn());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LogCapture : ILoggerFactory
    {
        public ConcurrentQueue<(EventId EventId, Exception? Exception)> Entries { get; } = new();
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(LogCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => owner.Entries.Enqueue((eventId, exception));
        }
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
