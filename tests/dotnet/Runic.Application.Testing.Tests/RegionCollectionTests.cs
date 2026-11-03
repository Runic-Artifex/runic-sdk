using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.Generated;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// region-collections/1 (D1): a [RunicRegionCollection] NavigationSelection reaches the page with every region's key and
// content and the selected index, and the page selects and closes regions by key through the departure guards.
internal sealed class RegionCollectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Test]
    public async Task PublishesRegionsKeysAndSelectionAsync()
    {
        await using var window = await DocumentsWindow.CreateAsync();
        var root = window.Host.Root;
        var empty = (await root.SnapshotAsync()).Regions(vm => vm.Documents);
        Require(empty.Regions.Count == 0 && empty.Selected is null, $"An empty collection published {empty}.");

        (await root.ExecuteAsync(vm => vm.OpenCommand, "\"a\"")).ThrowIfFailed();
        var opened = await root.ExecuteAsync(vm => vm.OpenCommand, "\"b\"");
        var state = opened.ThrowIfFailed().State!.Regions(vm => vm.Documents);
        Require(state.Regions.Count == 2 && state.Selected == 1 && state.Keys.Distinct().Count() == 2
                && state.Regions.All(region => region.Content?.Kind == "navEditor"),
            $"Two opened documents published {Describe(state)}.");
        Require(state.Keys.SequenceEqual(window.Shell.Documents.Regions.Select(BridgeRegionCollections.Key)),
            "The keys are not the regions' keys in the selection's order.");

        // A push inside a tab presents new content under the same key.
        var first = window.Shell.Documents.Regions[0];
        var before = state.Regions[0];
        await Wait(((NavigationRegion<INavPageViewModel>)first).PushAsync(NavigationTarget.Own<INavPageViewModel>(new NavEditorViewModel("a2"))));
        await Until(async () => (await root.SnapshotAsync()).Regions(vm => vm.Documents).Regions[0] is var region
            && region.Key == before.Key && region.Content?.Id != before.Content?.Id, "A push in a tab did not publish its new content.");
    }

    [Test]
    public async Task PageSelectsAndClosesByKeyAsync()
    {
        await using var window = await DocumentsWindow.CreateAsync();
        var root = window.Host.Root;
        foreach (var title in new[] { "a", "b", "c" })
            (await root.ExecuteAsync(vm => vm.OpenCommand, JsonSerializer.Serialize(title))).ThrowIfFailed();
        var keys = (await root.SnapshotAsync()).Regions(vm => vm.Documents).Keys;
        var firstContent = (await root.SnapshotAsync()).Regions(vm => vm.Documents).Regions[0].Content!;

        var selected = await root.SelectRegionAsync(vm => vm.Documents, keys[0]);
        Require(selected.Succeeded && selected.RegionOutcome?.Kind == NavigationOutcomeKind.Committed
                && selected.State!.Regions(vm => vm.Documents).Selected == 0 && window.Shell.Documents.SelectedIndex == 0,
            $"Selecting the first tab gave {selected}.");

        var closed = await root.CloseRegionAsync(vm => vm.Documents, keys[0]);
        var after = closed.State!.Regions(vm => vm.Documents);
        Require(closed.Succeeded && closed.RegionOutcome?.Kind == NavigationOutcomeKind.Committed
                && after.Keys.SequenceEqual(keys.Skip(1)) && after.Selected == 0,
            $"Closing the selected first tab gave {closed} with {Describe(after)}.");
        await Until(() => Task.FromResult(!BridgeDispatcher.For(window.Host.Transport).Serves($"content{firstContent.Id}")),
            "The closed tab's content route is still served.");
        Require(window.Shell.Opened["a"] is NavEditorViewModel { Disposed: 1 }, "The closed tab's owned page was not disposed.");

        var stale = await root.SelectRegionAsync(vm => vm.Documents, keys[0]);
        Require(!stale.Succeeded && stale.ErrorKind == "rejected" && stale.ErrorReason == "unresolved-key",
            $"Selecting a closed tab gave {stale}.");
    }

    [Test]
    public async Task GuardsDecideAndPageCloseCanBeOffAsync()
    {
        await using var context = new RunicModelContext();
        await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        var home = new NavHomeViewModel();
        using var tabs = new ReactiveNavDocumentsViewModel(navigator, [home, new NavEditorViewModel("other")]);
        using var host = new RunicWindowTestHost<ReactiveNavDocumentsViewModel>(tabs,
            (transport, content, vm) => new ReactiveNavDocumentsBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ModelContext = context, ViewLocator = new NavViewLocator() });
        var state = (await host.Root.SnapshotAsync()).Regions(vm => vm.Tabs);
        Require(state.Selected == 0 && state.Regions.Count == 2 && host.Root.Snapshot().Read(vm => vm.Count) == 2,
            $"The ReactiveUI tabs published {Describe(state)}.");

        home.Guard = _ => ValueTask.FromResult(false);
        var vetoed = await host.Root.SelectRegionAsync(vm => vm.Tabs, state.Keys[1]);
        Require(vetoed.Succeeded && vetoed.RegionOutcome is { Kind: NavigationOutcomeKind.Rejected, Rejection: NavigationRejection.Guard }
                && vetoed.State!.Regions(vm => vm.Tabs).Selected == 0,
            $"A vetoed switch gave {vetoed}.");

        home.Guard = null;
        var switched = await host.Root.SelectRegionAsync(vm => vm.Tabs, state.Keys[1]);
        Require(switched.RegionOutcome?.Kind == NavigationOutcomeKind.Committed && switched.State!.Regions(vm => vm.Tabs).Selected == 1,
            $"The switch gave {switched}.");

        var refused = await host.Root.CloseRegionAsync(vm => vm.Tabs, state.Keys[0]);
        Require(!refused.Succeeded && refused.ErrorReason == "unavailable" && tabs.Tabs.Regions.Count == 2,
            $"A close of a collection with PageCanClose = false gave {refused}.");

        // The ViewModel still closes through its command, and the Bridge follows the regions.
        var outcome = await Runic.Application.Views.ReactiveUI.Generated.ReactiveCommandExecution.Execute(tabs.CloseCommand, tabs.Tabs.Regions[0], CancellationToken.None).WaitScaledAsync(Timeout);
        Require(outcome.Kind == NavigationOutcomeKind.Committed, $"The close command gave {outcome}.");
        await Until(async () => (await host.Root.SnapshotAsync()).Regions(vm => vm.Tabs).Keys.SequenceEqual([state.Keys[1]])
            && host.Root.Snapshot().Read(vm => vm.Count) == 1, "The Bridge did not publish the closed tab.");
    }

    [Test]
    public async Task RegionRequestNeedsTheFeatureAsync()
    {
        await using var window = await DocumentsWindow.CreateAsync();
        var transport = window.Host.Transport;
        var hello = await transport.RequestAsync(
            """{"kind":"hello","protocol":4,"session":"plain","client":{"name":"test","version":"1"}}""", "plain");
        Require(hello.Contains("\"region-collections/1\"", StringComparison.Ordinal), $"The hello reply does not list the feature: {hello}");
        var reply = await transport.RequestAsync(
            """{"kind":"region","route":"navDocuments","member":"documents","action":"select","key":"1"}""", "plain");
        Require(reply.Contains("invalid-request", StringComparison.Ordinal), $"A region request without the feature gave {reply}.");

        await transport.RequestAsync(
            """{"kind":"hello","protocol":4,"session":"featured","client":{"name":"test","version":"1"},"features":["region-collections/1"]}""", "featured");
        var badAction = await transport.RequestAsync(
            """{"kind":"region","route":"navDocuments","member":"documents","action":"open","key":"1"}""", "featured");
        var badMember = await transport.RequestAsync(
            """{"kind":"region","route":"navDocuments","member":"open","action":"select","key":"1"}""", "featured");
        Require(badAction.Contains("invalid-request", StringComparison.Ordinal) && badMember.Contains("invalid-request", StringComparison.Ordinal),
            $"An unknown action or member gave {badAction} and {badMember}.");
    }

    [Test]
    public void ContractAndGeneratedClient()
    {
        var parts = BridgeContractShape.Parts(typeof(NavDocumentsViewModel));
        Require(parts.Contains("region-collection:Runic.Application.Testing.Tests.NavDocumentsViewModel:documents:contract:default:region:Runic.Application.Testing.Tests.INavPageViewModel:close:True"),
            $"The contract does not include the region collection:\n{string.Join('\n', parts)}");
        // A NavigationSelection without the attribute keeps its selection part, so its fingerprint stays.
        Require(BridgeContractShape.Parts(typeof(NavTabsViewModel)).Contains("selection:Runic.Application.Testing.Tests.NavTabsViewModel:tabs")
                && !BridgeContractShape.Parts(typeof(NavTabsViewModel)).Any(part => part.StartsWith("region-collection:Runic.Application.Testing.Tests.NavTabsViewModel:", StringComparison.Ordinal)),
            "A plain selection slot changed its contract part.");
        var generated = File.ReadAllText(Path.Combine(GeneratedTypeScriptDirectory(), "navDocuments.ts")).ReplaceLineEndings("\n");
        foreach (var expected in new[]
        {
            "  readonly documents: RegionCollection<ReactiveNavEditorPageReference | NavEditorPageReference | NavHomePageReference | NavLogPagePageReference | NavTabbedPagePageReference>;",
            "  selectDocuments(key: string): Promise<RegionOutcome>;",
            "  closeDocuments(key: string): Promise<RegionOutcome>;",
            "    async selectDocuments(key) { return view.region(\"documents\", \"select\", key); },",
            "  documents: (wire: unknown) => regionCollection(wire, item => { const reference = contentReference(item);",
        })
            Require(generated.Contains(expected, StringComparison.Ordinal), $"navDocuments.ts is missing:\n{expected}\n---\n{generated}");
        var reactive = File.ReadAllText(Path.Combine(GeneratedTypeScriptDirectory(), "reactiveNavDocuments.ts"));
        Require(reactive.Contains("selectTabs(key: string)", StringComparison.Ordinal) && !reactive.Contains("closeTabs", StringComparison.Ordinal),
            "A collection with PageCanClose = false has a close method, or no select method.");
        var mock = File.ReadAllText(Path.Combine(GeneratedTypeScriptDirectory(), "navDocuments.mock.ts"));
        Require(mock.Contains("readonly documents: MockRegionCollection<", StringComparison.Ordinal)
                && mock.Contains("regions: { documents: { close: true } },", StringComparison.Ordinal),
            $"The mock does not describe the region collection:\n{mock}");
    }

    // A guarded window close removes the collection's tabs one by one: the tabs whose pages agreed are gone even when a
    // later tab keeps the window open, so no empty tabs remain.
    [Test]
    public async Task WindowCloseRemovesTheTabsThatAgreedAsync()
    {
        var services = new ServiceCollection();
        services.AddRunicNavigation();
        services.AddScoped<NavDocumentsViewModel>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, NavDocumentsViewModel, IDisposable>>(
            _ => (transport, content, model) => new NavDocumentsBridge(transport, model, content: content));
        await using var provider = services.BuildServiceProvider();
        var window = await new TestWindowHost(provider).OpenWindowAsync<NavDocumentsWindow>();
        var native = (TestWindow<NavDocumentsViewModel>)window.Host;
        try
        {
            var model = window.ViewModel;
            (await native.Host.Root.ExecuteAsync(vm => vm.OpenCommand, "\"a\"")).ThrowIfFailed();
            (await native.Host.Root.ExecuteAsync(vm => vm.OpenCommand, "\"b\"")).ThrowIfFailed();
            var keeper = new NavHomeViewModel { Guard = _ => ValueTask.FromResult(false) };
            await model.Documents.Navigator.ModelContext.InvokeAsync(() => model.OpenPage(keeper));
            Require(model.Documents.Regions.Count == 3, "The window did not open three tabs.");

            Require(!await native.UserCloseAsync().WaitScaledAsync(Timeout) && native.IsOpen, "A vetoing tab did not keep the window open.");
            var left = (await native.Host.Root.SnapshotAsync()).Regions(vm => vm.Documents);
            Require(model.Documents.Regions.Count == 1 && left.Regions.Count == 1 && left.Regions[0].Content?.Kind == "navHome"
                    && left.Selected == 0 && model.Opened["a"] is NavEditorViewModel { Disposed: 1 },
                $"The tabs that agreed were not removed: {Describe(left)}.");

            keeper.Guard = null;
            Require(await native.UserCloseAsync().WaitScaledAsync(Timeout) && !native.IsOpen, "The window did not close once every tab agreed.");
        }
        finally { await window.DisposeAsync(); }
    }

    // A close of a tab whose close is pending joins it: the guard is asked once and both get the same outcome, on one
    // connection or two.
    [Test]
    public async Task ASecondCloseJoinsThePendingCloseAsync()
    {
        foreach (var secondConnection in new[] { false, true })
        {
            await using var window = await DocumentsWindow.CreateAsync();
            var (home, asks, decision) = HeldTab(window);
            var key = (await window.Host.Root.SnapshotAsync()).Regions(vm => vm.Documents).Keys[0];
            var first = window.Host.Root.CloseRegionAsync(vm => vm.Documents, key);
            await Until(() => Task.FromResult(asks() == 1), "The first close did not ask the guard.");
            var second = secondConnection
                ? RawRegionAsync(window.Host.Transport, "second", "close", key)
                : window.Host.Root.CloseRegionAsync(vm => vm.Documents, key).ContinueWith(task => task.Result.Json, TaskScheduler.Default);
            await Task.Delay(50);
            Require(!first.IsCompleted && !second.IsCompleted && asks() == 1, "The second close did not wait for the first, or asked again.");
            decision.SetResult(true);
            var replies = new[] { (await first.WaitScaledAsync(Timeout)).Json, await second.WaitScaledAsync(Timeout) };
            Require(replies.All(reply => reply.Contains("\"outcome\":{\"kind\":\"Committed\"", StringComparison.Ordinal)) && asks() == 1
                    && window.Shell.Documents.Regions.Count == 0,
                $"The joined closes ({(secondConnection ? "two connections" : "one connection")}) gave {string.Join(" and ", replies)} after {asks()} asks.");
            GC.KeepAlive(home);
        }
    }

    // A select while a close is pending supersedes the close, whose guard is cancelled; the tab stays.
    [Test]
    public async Task ASelectSupersedesAPendingCloseAsync()
    {
        await using var window = await DocumentsWindow.CreateAsync();
        (await window.Host.Root.ExecuteAsync(vm => vm.OpenCommand, "\"a\"")).ThrowIfFailed();
        var (_, asks, _) = HeldTab(window);
        var keys = (await window.Host.Root.SnapshotAsync()).Regions(vm => vm.Documents).Keys;
        var close = window.Host.Root.CloseRegionAsync(vm => vm.Documents, keys[1]);
        await Until(() => Task.FromResult(asks() == 1), "The close did not ask the guard.");
        var select = await window.Host.Root.SelectRegionAsync(vm => vm.Documents, keys[0]);
        var closed = await close.WaitScaledAsync(Timeout);
        Require(closed.RegionOutcome?.Kind == NavigationOutcomeKind.Superseded && select.RegionOutcome?.Kind == NavigationOutcomeKind.Committed
                && window.Shell.Documents.Regions.Count == 2 && window.Shell.Documents.SelectedIndex == 0,
            $"A select during a pending close gave {closed} and {select}.");
    }

    // After the selected tab closed, the page's next request names the newly selected tab's key from the same frame.
    [Test]
    public async Task ARequestWithTheNewlySelectedKeyCommitsAsync()
    {
        await using var window = await DocumentsWindow.CreateAsync();
        foreach (var title in new[] { "a", "b", "c" })
            (await window.Host.Root.ExecuteAsync(vm => vm.OpenCommand, JsonSerializer.Serialize(title))).ThrowIfFailed();
        var root = window.Host.Root;
        var keys = (await root.SnapshotAsync()).Regions(vm => vm.Documents).Keys;
        (await root.SelectRegionAsync(vm => vm.Documents, keys[1])).ThrowIfFailed();
        var closed = await root.CloseRegionAsync(vm => vm.Documents, keys[1]);
        var next = closed.State!.Regions(vm => vm.Documents);
        Require(next.SelectedRegion?.Key == keys[2], $"Closing the selected middle tab selected {Describe(next)}.");
        var selected = await root.SelectRegionAsync(vm => vm.Documents, next.SelectedRegion!.Key);
        var closedNext = await root.CloseRegionAsync(vm => vm.Documents, next.SelectedRegion!.Key);
        Require(selected.RegionOutcome?.Kind == NavigationOutcomeKind.Committed && closedNext.RegionOutcome?.Kind == NavigationOutcomeKind.Committed
                && closedNext.State!.Regions(vm => vm.Documents).Keys.SequenceEqual([keys[0]]),
            $"Requests with the newly selected key gave {selected} and {closedNext}.");
    }

    // Region requests count toward the window's 64 awaited calls, joined closes too.
    [Test]
    public async Task RegionRequestsCountTowardTheAwaitedCapacityAsync()
    {
        await using var window = await DocumentsWindow.CreateAsync();
        var (_, asks, decision) = HeldTab(window);
        var key = (await window.Host.Root.SnapshotAsync()).Regions(vm => vm.Documents).Keys[0];
        var closes = Enumerable.Range(0, 64).Select(_ => window.Host.Root.CloseRegionAsync(vm => vm.Documents, key)).ToArray();
        await Until(() => Task.FromResult(asks() == 1), "The closes did not ask the guard.");
        await Task.Delay(50);
        var refused = await window.Host.Root.CloseRegionAsync(vm => vm.Documents, key);
        Require(!refused.Succeeded && refused.ErrorReason == "capacity", $"The 65th awaited call gave {refused}.");
        decision.SetResult(true);
        var replies = await Task.WhenAll(closes).WaitScaledAsync(Timeout);
        Require(replies.All(reply => reply.RegionOutcome?.Kind == NavigationOutcomeKind.Committed) && asks() == 1,
            "The 64 joined closes did not all commit with one guard ask.");
    }

    // A close that settles after its route was released, here because the shell stopped presenting the page: the reply
    // has released: true instead of a revision, and keeps outcome.
    [Test]
    public async Task AReplyOfAReleasedRouteKeepsTheOutcomeAsync()
    {
        await using var context = new RunicModelContext();
        await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        var tab = new NavHomeViewModel { Guard = _ => { Interlocked.Increment(ref asked); return new(held.Task); } };
        var page = new NavTabbedPageViewModel(navigator, tab);
        var shell = new NavContentShellViewModel(page);
        using var host = new RunicWindowTestHost<NavContentShellViewModel>(shell,
            (transport, content, vm) => new NavContentShellBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ModelContext = context, ViewLocator = new NavViewLocator() });
        var driver = host.Root.View<NavTabbedPageViewModel>(vm => vm.Page);
        var key = (await driver.SnapshotAsync()).Regions(vm => vm.Documents).Keys[0];
        var close = driver.CloseRegionAsync(vm => vm.Documents, key);
        await Until(() => Task.FromResult(Volatile.Read(ref asked) == 1), "The close did not ask the guard.");
        await context.InvokeAsync(shell.Clear);
        await Until(async () => (await host.Root.SnapshotAsync()).Get(vm => vm.Page).ValueKind == JsonValueKind.Null
            && !BridgeDispatcher.For(host.Transport).Serves(driver.Route), "The page's route was not released.");
        held.SetResult(true);
        var reply = await close.WaitScaledAsync(Timeout);
        Require(reply.Released && reply.Revision is null && reply.RegionOutcome is { Kind: NavigationOutcomeKind.Committed },
            $"The close of a released route gave {reply}.");
    }

    // A tab whose page asks a held guard, and the number of asks; the test settles the decision.
    private static (NavHomeViewModel Page, Func<int> Asks, TaskCompletionSource<bool> Decision) HeldTab(DocumentsWindow window)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asks = 0;
        var page = new NavHomeViewModel { Guard = token => { Interlocked.Increment(ref asks); return new(decision.Task.WaitAsync(token)); } };
        window.Context.InvokeAsync(() => window.Shell.OpenPage(page)).AsTask().GetAwaiter().GetResult();
        return (page, () => Volatile.Read(ref asks), decision);
    }

    // A region request on another connection, after its own hello.
    private static async Task<string> RawRegionAsync(InMemoryBridgeTransport transport, string connection, string action, string key)
    {
        await transport.RequestAsync(
            $$"""{"kind":"hello","protocol":4,"session":"{{connection}}","client":{"name":"test","version":"1"},"features":["region-collections/1"]}""", connection);
        return await transport.RequestAsync(
            $$"""{"kind":"region","route":"navDocuments","member":"documents","action":"{{action}}","key":"{{key}}"}""", connection);
    }

    private static string Describe(RunicRegionCollectionState state) =>
        $"[{string.Join(", ", state.Regions.Select(region => $"{region.Key}:{region.Content?.Kind ?? "empty"}"))}] selected {state.Selected?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}";

    private static string GeneratedTypeScriptDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "obj", "bridge-frontend", "generated");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("The generated TypeScript directory was not found.");
    }

    private static async Task<T> Wait<T>(ValueTask<T> task) => await task.AsTask().WaitScaledAsync(Timeout);

    private static async Task Until(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(Timeout);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException(message);
            await Task.Delay(5);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // One window with the CommunityToolkit document tabs.
    private sealed class DocumentsWindow : IAsyncDisposable
    {
        private DocumentsWindow(RunicModelContext context, RunicNavigator navigator)
        {
            Context = context;
            Navigator = navigator;
            Shell = new NavDocumentsViewModel(navigator);
            Host = new RunicWindowTestHost<NavDocumentsViewModel>(Shell,
                (transport, content, vm) => new NavDocumentsBridge(transport, vm, content: content),
                new RunicWindowTestHostOptions { ModelContext = context, ViewLocator = new NavViewLocator() });
        }

        public RunicModelContext Context { get; }
        public RunicNavigator Navigator { get; }
        public NavDocumentsViewModel Shell { get; }
        public RunicWindowTestHost<NavDocumentsViewModel> Host { get; }

        public static Task<DocumentsWindow> CreateAsync()
        {
            var context = new RunicModelContext();
            return Task.FromResult(new DocumentsWindow(context, new RunicNavigator(new RunicNavigatorOptions { ModelContext = context })));
        }

        public async ValueTask DisposeAsync()
        {
            Host.Dispose();
            await Navigator.DisposeAsync();
            await Context.DisposeAsync();
        }
    }
}
