using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;

namespace Runic.Navigation.Tests;

// W315-020 point 2 (#350): NavigationTarget.Share holds content together with the other entries that hold it; the last
// holder to retire forgets and disposes it.
internal sealed partial class NavigationTests
{
    // Push and Back with Share: the content outlives the entry that retires first, and goes with the last.
    [Test]
    public async Task SharedContentLivesUntilItsLastEntryRetiresAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var document = new InitPage("document");
        await Wait(region.PushAsync(NavigationTarget.Own<Page>(document)));
        var shared = await Wait(region.PushAsync(NavigationTarget.Share<Page>(document)));
        Require(shared is NavigationResult<Page>.Committed { Current: { Ownership: NavigationOwnership.Shared } current } && current.Content == document
            && Names(region) == "home,document,document" && document.Initialized == 2 && fixture.Navigator.HolderRecordCount == 1,
            $"Sharing the current content gave {shared}; initialized {document.Initialized} times.");

        await Wait(region.BackAsync());
        Require(document.Disposed == 0 && RunicModelContextRegistry.Shared.TryGet(document, out _) && Names(region) == "home,document",
            "Retiring one holder disposed the shared content or released its lease.");
        var back = await Wait(region.BackAsync());
        Require(back is NavigationResult<Page>.Committed && document.Disposed == 1 && !RunicModelContextRegistry.Shared.TryGet(document, out _)
            && fixture.Navigator.HolderRecordCount == 0 && fixture.Navigator.UnretiredEntryCount == 1,
            "The last holder did not dispose the shared content and release its lease.");
    }

    // Replace with Share(current) hands the content over: nothing is disposed, the departing entry's content continues,
    // and LeaveConfirmation doesn't ask.
    [Test]
    public async Task ReplaceWithShareHandsTheContentOverAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var editor = new Page("editor");
        await Wait(region.PushAsync(NavigationTarget.Own(editor)));
        var asks = 0;
        var leave = new LeaveConfirmation(() => true, (_, _) => { asks++; return ValueTask.FromResult(false); });
        editor.Guard = leave.CanDepartAsync;
        var replaced = await Wait(region.ReplaceAsync(NavigationTarget.Share(editor)));
        Require(replaced is NavigationResult<Page>.Committed { Retired.Count: 1 } && editor.Disposed == 0 && asks == 0
            && region.Current == editor && region.CurrentEntry!.Ownership == NavigationOwnership.Shared,
            $"Replacing with Share(current) gave {replaced}; asked {asks}, disposed {editor.Disposed}.");
        Require(editor.Departures is [{ Kind: NavigationDepartureKind.Retire, ContentContinues: true }],
            "The departing holder's departure did not report that its content continues.");

        // The last holder retires normally: its content doesn't continue, so the confirmation asks.
        var back = await Wait(region.BackAsync());
        Require(back is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard } && asks == 1
            && editor.Departures[^1] is { Kind: NavigationDepartureKind.Retire, ContentContinues: false },
            $"Leaving the last holder gave {back}; asked {asks}.");
    }

    // Three holders across two regions; the content is disposed once, with the last, whichever order they retire in;
    // concurrent retirements dispose it once.
    [Test]
    public async Task SharedContentAcrossRegionsIsDisposedOnceAsync()
    {
        await using var fixture = new Fixture();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var side = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Borrow(new Page("side")));
        var report = new Page("report");
        await Wait(main.PushAsync(NavigationTarget.Own(report)));
        await Wait(side.PushAsync(NavigationTarget.Share(report)));
        await Wait(main.PushAsync(NavigationTarget.Share(report)));
        await Wait(main.ClearHistoryAsync());
        Require(report.Disposed == 0 && Names(main) == "report", "ClearHistory of one holder disposed the shared content.");
        var clears = await Wait(Task.WhenAll(main.ClearAsync().AsTask(), side.ClearAsync().AsTask()));
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(clears.All(clear => clear is NavigationResult<Page>.Committed) && report.Disposed == 1 && fixture.Navigator.HolderRecordCount == 0,
            $"Concurrent retirements disposed the shared content {report.Disposed} times.");
    }

    // A pending Share holds from admission and drops its hold when its push doesn't commit; a Share of a new instance
    // that doesn't commit disposes it, like Own.
    [Test]
    public async Task PendingShareDropsItsHoldAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var document = new Page("document");
        await Wait(region.PushAsync(NavigationTarget.Own(document)));
        document.Guard = (_, _) => ValueTask.FromResult(false);
        var vetoed = await Wait(region.PushAsync(NavigationTarget.Share(document)));
        Require(vetoed is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard } && document.Disposed == 0 && Names(region) == "home,document",
            $"A vetoed Share push gave {vetoed}, disposed {document.Disposed}.");
        document.Guard = null;
        await Wait(region.BackAsync());
        Require(document.Disposed == 1 && fixture.Navigator.HolderRecordCount == 0, "The vetoed Share kept a hold on the content.");

        var fresh = new Page("fresh");
        var blocked = new Page("blocked") { Guard = (_, _) => ValueTask.FromResult(false) };
        await Wait(region.PushAsync(NavigationTarget.Own(blocked)));
        Require(await Wait(region.PushAsync(NavigationTarget.Share(fresh))) is NavigationResult<Page>.Rejected && fresh.Disposed == 1,
            "A Share of a new instance that didn't commit was not disposed.");
    }

    // Share refuses a released instance, another navigator's instance and content that owns regions; Own refuses shared
    // content; shared content can't own a region; a shared target can't be an initial target.
    [Test]
    public async Task ShareOwnershipErrorsAsync()
    {
        await using var fixture = new Fixture();
        await using var other = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var released = new Page("released");
        await Wait(region.PushAsync(NavigationTarget.Own(released)));
        await Wait(region.BackAsync());
        Require(Throws<InvalidOperationException>(() => _ = region.PushAsync(NavigationTarget.Share(released)).AsTask()),
            "Share of a released instance was accepted.");

        var foreign = new Page("foreign");
        var otherRegion = other.Navigator.CreateRegion<Page>(other.Root, NavigationTarget.Borrow(new Page("home")));
        await Wait(otherRegion.PushAsync(NavigationTarget.Own(foreign)));
        Require(Throws<InvalidOperationException>(() => _ = region.PushAsync(NavigationTarget.Share(foreign)).AsTask()),
            "Share of another navigator's instance was accepted.");

        var shared = new Page("shared");
        await Wait(region.PushAsync(NavigationTarget.Share(shared)));
        Require(Throws<InvalidOperationException>(() => _ = region.PushAsync(NavigationTarget.Own(shared)).AsTask()), "Own after Share was accepted.");
        Require(Throws<InvalidOperationException>(() => fixture.Navigator.CreateRegion<Page>(shared)), "Shared content created a region.");

        var parent = new ParentPage("parent", fixture.Navigator);
        await Wait(region.PushAsync(NavigationTarget.Own<Page>(parent)));
        Require(Throws<InvalidOperationException>(() => _ = region.PushAsync(NavigationTarget.Share<Page>(parent)).AsTask()),
            "Share of content that owns a region was accepted.");
        Require(Throws<ArgumentException>(() => fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Share(new Page("root")))),
            "A shared initial target was accepted.");
    }

    // The entry scope of created content moves to the content and is disposed with the last holder.
    [Test]
    public async Task SharedContentKeepsItsEntryScopeAsync()
    {
        var services = new ServiceCollection().AddScoped<ScopedDependency>().BuildServiceProvider();
        await using var fixture = new Fixture(services: services, entryScopes: true);
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        await Wait(region.PushAsync<SharedScopedPage>());
        var page = (SharedScopedPage)region.Current!;
        await Wait(region.ReplaceAsync(NavigationTarget.Share<Page>(page)));
        Require(page.Disposed == 0 && !page.Dependency.Disposed, "Retiring the creating entry disposed the shared content's scope.");
        await Wait(region.BackAsync());
        Require(page.Disposed == 1 && page.Dependency.Disposed && page.Dependency.DisposedAfterContent,
            "The last holder did not dispose the content, then its entry scope.");
        await services.DisposeAsync();
    }

    // Navigator disposal releases shared content once.
    [Test]
    public async Task DisposalReleasesSharedContentOnceAsync()
    {
        var fixture = new Fixture();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var side = fixture.Navigator.CreateRegion<Page>(new object());
        var report = new Page("report");
        await Wait(main.PushAsync(NavigationTarget.Own(report)));
        await Wait(side.PushAsync(NavigationTarget.Share(report)));
        await fixture.DisposeAsync();
        Require(report.Disposed == 1 && fixture.Navigator.HolderRecordCount == 0 && fixture.Navigator.UnretiredEntryCount == 0,
            $"Disposal disposed the shared content {report.Disposed} times.");
    }

    private sealed class ScopedDependency : IDisposable
    {
        public SharedScopedPage? Owner { get; set; }
        public bool Disposed { get; private set; }
        public bool DisposedAfterContent { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            DisposedAfterContent = Owner?.Disposed == 1;
        }
    }

    private sealed class SharedScopedPage : Page
    {
        public SharedScopedPage(ScopedDependency dependency) : base("scoped")
        {
            Dependency = dependency;
            dependency.Owner = this;
        }

        public ScopedDependency Dependency { get; }
    }
}
