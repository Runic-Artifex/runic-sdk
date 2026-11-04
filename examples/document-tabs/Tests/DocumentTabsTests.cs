using DocumentTabs;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;

namespace DocumentTabs.Tests;

// Drives the real ViewModels and generated Bridges of one window, as the page
// would, without a browser or native window.
public sealed class DocumentTabsTests : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly AsyncServiceScope _scope;
    private readonly RunicWindowTestHost<DocumentsViewModel> _host;

    public DocumentTabsTests()
    {
        _services = new ServiceCollection().AddDocumentTabs().AddRunicViews()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _services.CreateAsyncScope();
        _host = RunicWindowTestHost.Create<DocumentsViewModel>(_scope.ServiceProvider);
    }

    private RunicViewDriver<DocumentsViewModel> Root => _host.Root;

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

    public async ValueTask DisposeAsync()
    {
        _host.Dispose();
        // The window scope disposes its navigator, which closes the tabs and disposes their pages.
        await _scope.DisposeAsync();
        await _services.DisposeAsync();
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
}
