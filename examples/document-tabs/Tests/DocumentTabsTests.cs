using DocumentTabs;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using Runic.Platform;
using Runic.Platform.Runtime;

namespace DocumentTabs.Tests;

// Drives the real ViewModels and generated Bridges of one window, as the page
// would, without a browser or native window.
public sealed class DocumentTabsTests : IAsyncDisposable
{
    private readonly string _stateFile = Path.Combine(Path.GetTempPath(), $"document-tabs-{Guid.NewGuid():N}.json");
    private readonly ServiceProvider _services;
    private AsyncServiceScope _scope;
    private RunicWindowTestHost<DocumentsViewModel> _host;

    public DocumentTabsTests()
    {
        _services = new ServiceCollection().AddDocumentTabs(_stateFile).AddRunicViews()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _services.CreateAsyncScope();
        _host = RunicWindowTestHost.Create<DocumentsViewModel>(_scope.ServiceProvider);
    }

    private RunicViewDriver<DocumentsViewModel> Root => _host.Root;

    private RunicNavigator Navigator => _scope.ServiceProvider.GetRequiredService<RunicNavigator>();

    [Test]
    public async Task New_opens_a_selected_tab_and_the_page_selects_by_key()
    {
        var tabs = await OpenAsync(2);

        await Assert.That(tabs.Selected).IsEqualTo(1);
        await Assert.That(tabs.Regions.Select(region => region.Content?.Kind ?? "")).IsEquivalentTo(["document", "document"]);
        await Assert.That(Document(tabs, 0).Snapshot().Read(vm => vm.Title)).IsEqualTo("Untitled 1");
        await Assert.That(Document(tabs, 1).Snapshot().Read(vm => vm.Title)).IsEqualTo("Untitled 2");

        var selected = await Root.SelectRegionAsync(vm => vm.Documents, tabs.Keys[0]);
        await Assert.That(selected.RegionOutcome?.Kind).IsEqualTo(NavigationOutcomeKind.Committed);
        await Assert.That(selected.State!.Regions(vm => vm.Documents).Selected).IsEqualTo(0);
    }

    [Test]
    public async Task Each_tab_keeps_its_own_back_stack()
    {
        var tabs = await OpenAsync(2);
        var second = tabs.Regions[1].Content;

        (await Document(tabs, 0).ExecuteAsync(vm => vm.ShowDetailsCommand)).ThrowIfFailed();
        var pushed = await TabsAsync();
        await Assert.That(pushed.Keys).IsEquivalentTo(tabs.Keys);
        await Assert.That(pushed.Regions[0].Content?.Kind).IsEqualTo("documentInfo");
        await Assert.That(pushed.Regions[1].Content).IsEqualTo(second);
        var info = _host.View<DocumentInfoViewModel>(pushed.Regions[0].Content!);
        await Assert.That(info.Snapshot().Read(vm => vm.Title)).IsEqualTo("Untitled 1");

        (await info.ExecuteAsync(vm => vm.BackCommand)).ThrowIfFailed();
        var back = await TabsAsync();
        await Assert.That(back.Regions[0].Content?.Kind).IsEqualTo("document");
        await Assert.That(back.Regions[1].Content).IsEqualTo(second);
        await Assert.That(Document(back, 0).Snapshot().Read(vm => vm.Title)).IsEqualTo("Untitled 1");
    }

    [Test]
    public async Task Unsaved_changes_do_not_block_switching_tabs()
    {
        var tabs = await OpenAsync(2);
        (await Document(tabs, 1).SetAsync(vm => vm.Body, "Draft")).ThrowIfFailed();

        var switched = await Root.SelectRegionAsync(vm => vm.Documents, tabs.Keys[0]);

        await Assert.That(switched.RegionOutcome?.Kind).IsEqualTo(NavigationOutcomeKind.Committed);
        await Assert.That(switched.State!.Regions(vm => vm.Documents).Selected).IsEqualTo(0);
        await Assert.That(Document(tabs, 1).Snapshot().Read(vm => vm.Body)).IsEqualTo("Draft");
    }

    [Test]
    public async Task A_dirty_tab_closes_only_after_its_changes_are_discarded()
    {
        var tabs = await OpenAsync(2);
        var document = Document(tabs, 0);
        (await document.SetAsync(vm => vm.Title, "Letter")).ThrowIfFailed();
        await Assert.That(document.Snapshot().Read(vm => vm.IsDirty)).IsTrue();

        var refused = await Root.CloseRegionAsync(vm => vm.Documents, tabs.Keys[0]);
        await Assert.That(refused.RegionOutcome?.Kind).IsEqualTo(NavigationOutcomeKind.Rejected);
        await Assert.That(refused.RegionOutcome?.Rejection).IsEqualTo(NavigationRejection.Guard);
        await Assert.That(refused.State!.Regions(vm => vm.Documents).Keys).IsEquivalentTo(tabs.Keys);

        var discarded = (await document.ExecuteAsync(vm => vm.DiscardCommand)).ThrowIfFailed().State!;
        await Assert.That(discarded.Read(vm => vm.Title)).IsEqualTo("Untitled 1");
        await Assert.That(discarded.Read(vm => vm.IsDirty)).IsFalse();

        var closed = await Root.CloseRegionAsync(vm => vm.Documents, tabs.Keys[0]);
        await Assert.That(closed.RegionOutcome?.Kind).IsEqualTo(NavigationOutcomeKind.Committed);
        var after = closed.State!.Regions(vm => vm.Documents);
        await Assert.That(after.Keys).IsEquivalentTo([tabs.Keys[1]]);
        await Assert.That(after.Selected).IsEqualTo(0);
    }

    [Test]
    public async Task Closing_asks_the_document_under_its_details_page()
    {
        var tabs = await OpenAsync(1);
        var document = Document(tabs, 0);
        (await document.SetAsync(vm => vm.Body, "Draft")).ThrowIfFailed();
        (await document.ExecuteAsync(vm => vm.ShowDetailsCommand)).ThrowIfFailed();

        var refused = await Root.CloseRegionAsync(vm => vm.Documents, tabs.Keys[0]);
        await Assert.That(refused.RegionOutcome?.Rejection).IsEqualTo(NavigationRejection.Guard);

        // The covered document's View was released; Back presents it again.
        var info = _host.View<DocumentInfoViewModel>((await TabsAsync()).Regions[0].Content!);
        (await info.ExecuteAsync(vm => vm.BackCommand)).ThrowIfFailed();
        (await Document(await TabsAsync(), 0).ExecuteAsync(vm => vm.SaveCommand)).ThrowIfFailed();
        var closed = await Root.CloseRegionAsync(vm => vm.Documents, tabs.Keys[0]);
        await Assert.That(closed.RegionOutcome?.Kind).IsEqualTo(NavigationOutcomeKind.Committed);
        var after = closed.State!.Regions(vm => vm.Documents);
        await Assert.That(after.Regions).IsEmpty();
        await Assert.That(after.Selected).IsNull();
    }

    [Test]
    public async Task The_tabs_and_their_pages_reopen_after_a_restart()
    {
        var tabs = await OpenAsync(3);
        (await Document(tabs, 0).ExecuteAsync(vm => vm.ShowDetailsCommand)).ThrowIfFailed();
        (await Root.SelectRegionAsync(vm => vm.Documents, tabs.Keys[1])).ThrowIfFailed();
        // The ViewModel saves the tabs shortly after each change.
        await SavedAsync("\"selected\":1", "documents/1/info");

        await RestartAsync();
        var restored = await _host.ViewModel.RestoreNavigationAsync();
        await Assert.That(restored.IsComplete).IsTrue();
        var after = await TabsAsync();
        await Assert.That(after.Regions.Select(region => region.Content?.Kind ?? "")).IsEquivalentTo(["documentInfo", "document", "document"]);
        await Assert.That(after.Selected).IsEqualTo(1);
        await Assert.That(Document(after, 1).Snapshot().Read(vm => vm.Title)).IsEqualTo("Untitled 2");
        await Assert.That(_host.View<DocumentInfoViewModel>(after.Regions[0].Content!).Snapshot().Read(vm => vm.Title)).IsEqualTo("Untitled 1");
    }

    [Test]
    public async Task The_saved_path_restores_exactly_in_a_new_window()
    {
        var tabs = await OpenAsync(2);
        (await Document(tabs, 1).ExecuteAsync(vm => vm.ShowDetailsCommand)).ThrowIfFailed();
        var saved = Navigator.SavePath();

        await RestartAsync();
        var restore = await Navigator.RestorePathAsync(saved);
        await Assert.That(restore.IsExact).IsTrue();
        await Assert.That(restore.Restored).Contains("documents/2/info");
    }

    [Test]
    public async Task A_deep_link_opens_or_selects_the_document_tab()
    {
        var tabs = await OpenAsync(2);
        // The details of the first document: its tab is selected and the details page pushed in it.
        await Assert.That(await _host.ViewModel.OpenLinkAsync(new Uri("document-tabs://documents/1/info"))).IsTrue();
        var linked = await TabsAsync();
        await Assert.That(linked.Keys).IsEquivalentTo(tabs.Keys);
        await Assert.That(linked.Selected).IsEqualTo(0);
        await Assert.That(linked.Regions[0].Content?.Kind).IsEqualTo("documentInfo");

        // A document without a tab opens in a new one; unknown documents, routes and schemes open nothing.
        (await Root.CloseRegionAsync(vm => vm.Documents, linked.Keys[1])).ThrowIfFailed();
        await Assert.That(await _host.ViewModel.OpenLinkAsync(new Uri("document-tabs://documents/2"))).IsTrue();
        await Assert.That((await TabsAsync()).Regions.Count).IsEqualTo(2);
        await Assert.That(await _host.ViewModel.OpenLinkAsync(new Uri("document-tabs://documents/99"))).IsFalse();
        await Assert.That(await _host.ViewModel.OpenLinkAsync(new Uri("document-tabs://settings"))).IsFalse();
        await Assert.That(await _host.ViewModel.OpenLinkAsync(new Uri("https://example.org/documents/1"))).IsFalse();
        await Assert.That((await TabsAsync()).Regions.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_second_launch_and_a_notification_route_their_links_to_the_window()
    {
        await OpenAsync(2);
        // A second instance forwards its arguments to the running one, which turns its URLs into open requests.
        var instance = new ForwardingInstance();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var opens = ApplicationInstance.CreateOpenRequests(new DesktopOpenOptions([DocumentsViewModel.LinkScheme]), instance))
        {
            opens.Opened += (_, request) => _ = Task.Run(async () =>
            {
                foreach (var link in request.Items) await _host.ViewModel.OpenLinkAsync(link);
                opened.TrySetResult();
            });
            instance.Launch("document-tabs://documents/1/info");
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var tabs = await TabsAsync();
        await Assert.That(tabs.Selected).IsEqualTo(0);
        await Assert.That(tabs.Regions[0].Content?.Kind).IsEqualTo("documentInfo");

        // A notification's activation link opens the same way.
        var activated = await _host.ViewModel.OpenNotificationAsync(
            new DesktopNotificationActivation("saved-2", "", new Uri("document-tabs://documents/2/info")) { IsTrusted = true });
        await Assert.That(activated).IsTrue();
        tabs = await TabsAsync();
        await Assert.That(tabs.Selected).IsEqualTo(1);
        await Assert.That(tabs.Regions[1].Content?.Kind).IsEqualTo("documentInfo");
    }

    public async ValueTask DisposeAsync()
    {
        File.Delete(_stateFile);
        _host.Dispose();
        // The window scope disposes its navigator, which closes the tabs and disposes their pages.
        await _scope.DisposeAsync();
        await _services.DisposeAsync();
    }

    private async Task SavedAsync(params string[] parts)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(File.Exists(_stateFile) && parts.All(File.ReadAllText(_stateFile).Contains)))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"The tabs were not saved: {(File.Exists(_stateFile) ? File.ReadAllText(_stateFile) : "no file")}");
            await Task.Delay(20);
        }
    }

    // A new window scope, as after a restart: the old window's navigator and pages are gone.
    private async Task RestartAsync()
    {
        _host.Dispose();
        await _scope.DisposeAsync();
        _scope = _services.CreateAsyncScope();
        _host = RunicWindowTestHost.Create<DocumentsViewModel>(_scope.ServiceProvider);
    }

    private async Task<RunicRegionCollectionState> OpenAsync(int count)
    {
        for (var i = 0; i < count; i++)
            (await Root.ExecuteAsync(vm => vm.NewDocumentCommand)).ThrowIfFailed();
        return await TabsAsync();
    }

    private async Task<RunicRegionCollectionState> TabsAsync() => (await Root.SnapshotAsync()).Regions(vm => vm.Documents);

    private RunicViewDriver<DocumentViewModel> Document(RunicRegionCollectionState tabs, int index) =>
        _host.View<DocumentViewModel>(tabs.Regions[index].Content!);

    // The running instance's side of ApplicationInstance: raises a later launch, as a second process's forwarding does.
    private sealed class ForwardingInstance : IApplicationInstance
    {
        public event EventHandler<ApplicationLaunch>? Launched;

        public void Launch(params string[] arguments) => Launched?.Invoke(this, ApplicationLaunch.FromProcess(arguments));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
