using System.Text.Json;
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
            "  readonly documents: RegionCollection<ReactiveNavEditorPageReference | NavEditorPageReference | NavHomePageReference | NavLogPagePageReference>;",
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

    private static string Describe(RunicRegionCollectionState state) =>
        $"[{string.Join(", ", state.Regions.Select(region => $"{region.Key}:{region.Content?.Kind ?? "empty"}"))}] selected {state.Selected?.ToString() ?? "none"}";

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
