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
    // The transport's keys of two pages. The window gives each connection a key of its own (the driver's Connection).
    private static readonly RunicConnection Alice = new(InMemoryBridgeTransport.DefaultConnectionKey, InMemoryBridgeTransport.DefaultClientKey);
    private static readonly RunicConnection Bob = new("connection-bob", "client-bob");

    private static RunicConnection Own<T>(RunicViewDriver<T> driver) where T : class =>
        driver.Connection ?? throw new InvalidOperationException($"{driver.ConnectionKey} is not open.");

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

    // client-languages/1: a connection lists the languages its hello reported, and compares by its keys only.
    [Test]
    public async Task AConnectionListsTheLanguagesItsPageReportedAsync()
    {
        await using var provider = Provider();
        await using var window = await new TestWindowHost(provider).OpenWindowAsync<ConnectionsWindow>();
        var host = ((TestWindow<ConnectionsViewModel>)window.Host).Host;
        var connections = window.Services.GetRequiredService<IRunicConnections>();
        // Case-only duplicates collapse to one tag in BCP 47 case; a tag longer than 64 characters is ignored, not rejected.
        host.Transport.SetLanguages(Bob.ConnectionKey,
            ["DE-ch", "de", "DE", "not a tag", "de-CH", "en", "ZH-hant-TW", "es-419", "en-US-U-CA-Buddhist", "a" + new string('b', 64)]);
        var bob = host.Root.ForConnection(Bob.ConnectionKey, Bob.ClientKey);
        (await host.Root.ExecuteAsync(model => model.TouchCommand)).ThrowIfFailed();
        (await bob.ExecuteAsync(model => model.TouchCommand)).ThrowIfFailed();
        var alice = Own(host.Root);
        var listed = Own(bob);
        Require(alice.Languages.Count == 0, $"A page that reported no languages lists [{string.Join(", ", alice.Languages)}].");
        Require(listed.Languages.SequenceEqual(["de-CH", "de", "en", "zh-Hant-TW", "es-419", "en-US-u-ca-buddhist"]),
            $"The connection lists [{string.Join(", ", listed.Languages)}], not the language tags of its hello once each, in BCP 47 case.");
        Require(connections.Current.Single(connection => connection.Equals(listed)).Languages.Count == 6,
            "IRunicConnections did not list the languages.");
        // The list is read-only, and a list set with `with` is copied.
        Require(listed.Languages is not List<string> && listed.Languages is ICollection<string> { IsReadOnly: true },
            $"The languages are a {listed.Languages.GetType()}.");
        var source = new List<string> { "fr" };
        var copy = listed with { Languages = source };
        source.Add("it");
        Require(copy.Languages.SequenceEqual(["fr"]), "The languages were not copied when set.");
        Require(listed.ToString() == $"RunicConnection {{ ConnectionKey = {listed.ConnectionKey}, ClientKey = {listed.ClientKey}, " +
            "Languages = [de-CH, de, en, zh-Hant-TW, es-419, en-US-u-ca-buddhist] }", $"ToString: {listed}");
        Require(listed.Equals(new RunicConnection(listed.ConnectionKey, listed.ClientKey))
            && listed.GetHashCode() == new RunicConnection(listed.ConnectionKey, listed.ClientKey).GetHashCode(),
            "Two connections with the same keys are not equal.");
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
        var a = Own(alice);
        var b = Own(bob);
        Require(a.ConnectionKey.StartsWith(Alice.ConnectionKey + "#", StringComparison.Ordinal) && a.ClientKey == Alice.ClientKey,
            $"The window's key of the connection is {a.ConnectionKey}.");

        Require(vm.Callers.SequenceEqual([a, b, b, a]),
            $"The commands did not run with their connections as callers: {string.Join(", ", vm.Callers)}");
        Require(connections.Current.SequenceEqual([a, b]) && host.Connections.Current.SequenceEqual([a, b]),
            $"The window did not list both connections in connection order: {string.Join(", ", connections.Current)}");
        Require(vm.Viewers == 2 && (await alice.SnapshotAsync()).Read(model => model.Viewers) == 2,
            "Connected did not run in a model turn whose change the page sees.");
        Require(connections.Caller is null, "The test's own flow, outside a command, has a caller.");
        Require(changes.Count == 3 && changes[0].Count == 0 && changes[2].SequenceEqual([a, b]),
            $"WhenChanged did not emit the list on subscribe and after each change: {changes.Count}");

        // Each connection keeps its own draft; a disconnect removes and disposes it.
        (await alice.ExecuteAsync<string>(model => model.TypeCommand, "alice's")).ThrowIfFailed();
        (await bob.ExecuteAsync<string>(model => model.TypeCommand, "bob's")).ThrowIfFailed();
        var drafts = vm.Drafts.Values;
        Require(drafts.Count == 2 && drafts[a.ConnectionKey].Text == "alice's" && drafts[b.ConnectionKey].Text == "bob's",
            "The drafts were not kept per caller.");
        var bobsDraft = drafts[b.ConnectionKey];

        host.Transport.CloseConnection(Bob.ConnectionKey);
        await host.Content.ModelContext!.InvokeAsync(static () => { });
        Require(connections.Current.SequenceEqual([a]) && vm.Viewers == 1 && vm.Left.SequenceEqual([b]) && bob.Connection is null,
            "Disconnected did not remove the closed connection.");
        Require(bobsDraft.Disposed && !vm.Drafts.Values.ContainsKey(b.ConnectionKey) && !vm.Drafts.TryGet(b, out _),
            "The closed connection's draft was not removed, or a new one was created for it.");
        Require(vm.Drafts.TryGet(a, out var alicesDraft) && alicesDraft.Text == "alice's", "The open connection lost its draft.");
        Require(changes.Count == 4 && changes[3].SequenceEqual([a]), "WhenChanged did not emit the disconnect.");

        // A reconnect is a new connection with an empty draft.
        var again = host.Root.ForConnection("connection-bob-2", Bob.ClientKey);
        (await again.ExecuteAsync(model => model.TouchCommand)).ThrowIfFailed();
        Require(vm.Callers[^1] == Own(again) && connections.Current.Count == 2
            && !vm.Drafts.Values.ContainsKey(Own(again).ConnectionKey), "A reconnect was not a new connection.");

        // A transport that reuses Bob's closed key, as CS-WebUI reuses WebUI's slots: the window gives it a new key.
        var reused = host.Root.ForConnection(Bob.ConnectionKey, Bob.ClientKey);
        (await reused.ExecuteAsync(model => model.TouchCommand)).ThrowIfFailed();
        var reusedConnection = Own(reused);
        Require(reusedConnection != b && vm.Callers[^1] == reusedConnection && connections.Current.Contains(reusedConnection)
            && !connections.Current.Contains(b), $"The reused transport key did not get a new connection key: {vm.Callers[^1]}");
        Require(!vm.Drafts.TryGet(b, out _), "The closed connection's key reached the connection that reused it.");
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
        var early = new RunicConnection(host.Content.Dispatcher.ConnectionKeyOf("connection-early")!, "client-early");
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
        var b = Own(bob);
        var bobsDraft = vm.Drafts.Values[b.ConnectionKey];

        var hold = bob.ExecuteAsync(model => model.HoldCommand);
        await vm.HoldStarted.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
        host.Transport.CloseConnection(Bob.ConnectionKey);
        await Turn(host);
        // Another page gets Bob's transport key while his command still awaits.
        var next = host.Root.ForConnection(Bob.ConnectionKey, Bob.ClientKey);
        (await next.ExecuteAsync(model => model.TouchCommand)).ThrowIfFailed();
        var nextConnection = Own(next);
        Require(nextConnection != b && vm.Callers[^1] == nextConnection, $"The page that reused the key was {vm.Callers[^1]}.");

        vm.HoldGate.SetResult();
        var (caller, hasDraft) = await vm.HoldDone.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
        Require(caller == b, $"The resumed command's caller was {caller}, not its own closed connection.");
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
        var bob = host.Root.ForConnection(Bob.ConnectionKey, Bob.ClientKey);
        (await bob.ExecuteAsync(model => model.StartBackgroundCommand)).ThrowIfFailed();

        // The command ended; its background work runs on. Outside a scope, a question with two pages that handle it asks
        // neither and uses its fallback, instead of asking the page whose command ended.
        vm.BackgroundGate.SetResult();
        var (caller, inCommand, answer) = await vm.Background!.WaitScaledAsync(TimeSpan.FromSeconds(5));
        Require(inCommand == Own(bob) && caller is null, $"Work that outlived its command kept the caller {caller}.");
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
        using (RunicInteractionInvocation.Enter(host.Content, host.RootRoute, Alice.ClientKey, "connection-reporting"))
        {
            host.Transport.Request(Hello("s-carol"), "connection-carol", "client-carol");
            await Turn(host);
        }
        var carol = new RunicConnection(host.Content.Dispatcher.ConnectionKeyOf("connection-carol")!, "client-carol");
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
        var a = Own(host.Root);
        var bob = host.Root.ForConnection(Bob.ConnectionKey, Bob.ClientKey);
        (await bob.ExecuteAsync<string>(model => model.TypeCommand, "bob's")).ThrowIfFailed();
        var b = Own(bob);
        var drafts = vm.Drafts.Values;
        foreach (var draft in drafts.Values) draft.ThrowOnDispose = true;
        host.Transport.CloseConnection(Bob.ConnectionKey);
        await Turn(host);
        Require(drafts[b.ConnectionKey].Disposed && !vm.Drafts.Values.ContainsKey(b.ConnectionKey) && vm.Left.SequenceEqual([b]),
            "The disconnected connection's draft was not disposed and removed.");
        vm.Dispose();
        Require(drafts[a.ConnectionKey].Disposed && drafts[carol.ConnectionKey].Disposed && vm.Drafts.Values.Count == 0,
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
        Require(connections.Current.SequenceEqual([new RunicConnection(session.Dispatcher.ConnectionKeyOf("connection-late")!, "client-late")]) && raised == 0,
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
        var secondBob = secondHost.Root.ForConnection(Bob.ConnectionKey, Bob.ClientKey);
        (await secondBob.ExecuteAsync(model => model.TouchCommand)).ThrowIfFailed();
        var a = Own(firstHost.Root);
        Require(first.ViewModel.Connections.Current.SequenceEqual([a]) && second.ViewModel.Connections.Current.SequenceEqual([Own(secondBob)]),
            "A window listed another window's connection.");

        // A command of the first window that reads the second window's connections has no caller there.
        using (RunicInteractionInvocation.Enter(firstHost.Content, firstHost.RootRoute, a.ClientKey, a.ConnectionKey))
        {
            Require(first.ViewModel.Connections.Caller == a, "The window's own command had no caller.");
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
        var a = Own(host.Root);
        Require(seen == 1 && host.Connections.Current.SequenceEqual([a]), "A throwing handler stopped the others.");
        // RunicWindowTestHost.Create binds the IRunicConnections of the services it was given.
        Require(unbound.Current.SequenceEqual([a]) && host.ViewModel.Callers.SequenceEqual([a]),
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
