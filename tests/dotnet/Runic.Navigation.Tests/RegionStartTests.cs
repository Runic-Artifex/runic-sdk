using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;

namespace Runic.Navigation.Tests;

// W260-005: NavigationRegion<TContent>.Start commits the root entry of a region created empty, on the
// path of CreateRegion's initial target. It is synchronous, throws on failure and is allowed once.
internal sealed partial class NavigationTests
{
    [Test]
    public async Task StartCommitsTheRootInlineAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        var caller = Environment.CurrentManagedThreadId;
        List<string> seen = [];
        region.PropertyChanged += (_, args) => seen.Add($"{args.PropertyName}:{region.Current}:{Environment.CurrentManagedThreadId == caller}");
        var root = new StartAwarePage("root", region);

        region.Start(NavigationTarget.Borrow<Page>(root));

        Require(ReferenceEquals(region.Current, root) && region.CurrentEntry is { Ownership: NavigationOwnership.Borrowed }
            && !region.CanGoBack && region.History.Count == 0 && !region.IsTransitioning,
            "Start did not make the target the region's only entry before it returned.");
        Require(root.Arrivals is ["Reset:root:True"],
            $"OnNavigatedTo did not run once, inline, with Reset and the new current: {string.Join(" | ", root.Arrivals)}");
        Require(seen is ["Current:root:True", "CurrentEntry:root:True"],
            $"The region's PropertyChanged did not run inline after the commit: {string.Join(" | ", seen)}");

        // The region navigates normally from there.
        Require(await Wait(region.PushAsync(NavigationTarget.Own(new Page("detail")))) is NavigationResult<Page>.Committed
            && region.CanGoBack, "A request after Start did not commit.");
        Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Committed && ReferenceEquals(region.Current, root),
            "Back did not return to the started root.");
    }

    [Test]
    public async Task StartCreatesAnOwnedRootInItsEntryScopeAsync()
    {
        await using var provider = StandaloneServices();
        Page created;
        await using (var fixture = new Fixture(services: provider, entryScopes: true))
        {
            var region = fixture.Navigator.CreateRegion<Page>(fixture.Root);
            region.Start(NavigationTarget.Create<ContextPage>());
            created = region.Current!;
            Require(created is ContextPage { StateAtConstruction: NavigationEntryState.Pending } page
                && page.Entry.Id == region.CurrentEntry!.Id && page.Entry.State == NavigationEntryState.Active
                && region.CurrentEntry.Ownership == NavigationOwnership.Owned && fixture.Navigator.UnretiredEntryCount == 1,
                "Start with a Create target did not construct an owned root with its own pending entry's context.");
        }
        Require(created.Disposed == 1, "The navigator did not dispose the root that Start created.");
    }

    [Test]
    public async Task StartFailureThrowsAndLeavesTheRegionEmptyAsync()
    {
        Scoped? scoped = null;
        await using var provider = StandaloneServices();
        await using var fixture = new Fixture(services: provider, entryScopes: true);
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        var changes = 0;
        region.PropertyChanged += (_, _) => changes++;

        var error = Catch(() => region.Start(NavigationTarget.Create<Page>((services, _) =>
        {
            scoped = services.GetRequiredService<Scoped>();
            throw new StartFailedException();
        })));

        Require(error is StartFailedException, $"A failing root factory did not propagate from Start: {error}");
        Require(region.Current is null && region.CurrentEntry is null && changes == 0,
            "A failed Start committed an entry or raised PropertyChanged.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(scoped is { Disposed: 1 } && fixture.Navigator.UnretiredEntryCount == 0,
            "A failed Start did not release the root's entry scope, or left an entry behind.");

        // Start is one-time, also after a failure; ResetAsync still navigates the region.
        Require(Catch(() => region.Start(NavigationTarget.Borrow(new Page("again")))) is InvalidOperationException { Message: var message }
            && message.Contains("once", StringComparison.Ordinal), "A second Start after a failure was accepted.");
        Require(await Wait(region.ResetAsync(NavigationTarget.Borrow(new Page("fallback")))) is NavigationResult<Page>.Committed,
            "ResetAsync failed after a failed Start.");
    }

    [Test]
    public async Task StartIsOneTimeAndOnlyBeforeRequestsAsync()
    {
        await using var fixture = new Fixture();

        // Twice.
        var twice = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        twice.Start(NavigationTarget.Borrow(new Page("first")));
        Require(Catch(() => twice.Start(NavigationTarget.Borrow(new Page("second")))) is InvalidOperationException { Message: var second }
            && second.Contains("Start can be called once", StringComparison.Ordinal) && twice.Current?.Name == "first",
            "A second Start was accepted or changed the region.");

        // On a region created with an initial target.
        var initial = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("initial")));
        Require(Catch(() => initial.Start(NavigationTarget.Borrow(new Page("late")))) is InvalidOperationException
            && initial.Current?.Name == "initial", "Start was accepted on a region created with an initial target.");

        // After a committed request.
        var pushed = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        await Wait(pushed.PushAsync(NavigationTarget.Borrow(new Page("pushed"))));
        Require(Catch(() => pushed.Start(NavigationTarget.Borrow(new Page("late")))) is InvalidOperationException { Message: var afterRequest }
            && afterRequest.Contains("navigation request", StringComparison.Ordinal) && pushed.Current?.Name == "pushed",
            "Start was accepted after a committed request.");

        // After a request that did not commit, and after one that was emptied again: still a request.
        var rejected = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        Require(await Wait(rejected.BackAsync()) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.NoHistory },
            "Back on an empty region was not rejected.");
        Require(Catch(() => rejected.Start(NavigationTarget.Borrow(new Page("late")))) is InvalidOperationException
            && rejected.Current is null, "Start was accepted after a rejected request.");
        var cleared = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        await Wait(cleared.PushAsync(NavigationTarget.Borrow(new Page("gone"))));
        await Wait(((INavigationRegion)cleared).ClearAsync());
        Require(cleared.Current is null && Catch(() => cleared.Start(NavigationTarget.Borrow(new Page("late")))) is InvalidOperationException,
            "Start was accepted on a region that was emptied by Clear.");

        // A request issued while the root is being constructed.
        var racing = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        Task<NavigationResult<Page>>? inner = null;
        Require(Catch(() => racing.Start(NavigationTarget.Create<Page>((_, _) =>
            {
                inner = racing.PushAsync(NavigationTarget.Borrow(new Page("inner"))).AsTask();
                return new Page("root");
            }))) is InvalidOperationException { Message: var racingMessage } && racingMessage.Contains("navigation request", StringComparison.Ordinal),
            "Start committed over a request issued during the root's construction.");
        await Wait(inner!);
        Require(racing.Current?.Name == "inner", $"The racing request did not keep the region: {racing.Current}.");

        // The rules of initial targets.
        var rules = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        Require(Catch(() => rules.Start(null!)) is ArgumentNullException, "A null target was accepted.");
        Require(Catch(() => rules.Start(NavigationTarget.Create<InputClockPage, string>("x"))) is ArgumentException,
            "A target with input was accepted by Start.");
        Require(Catch(() => rules.Start(NavigationTarget.Borrow<Page>(new InitPage("init")))) is ArgumentException,
            "A root implementing INavigationInitialize was accepted by Start.");
        rules.Start(NavigationTarget.Borrow(new Page("valid")));
        Require(rules.Current?.Name == "valid", "A rejected argument used up the region's Start.");

        // A closed navigator.
        var late = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        await fixture.Navigator.DisposeAsync();
        Require(Catch(() => late.Start(NavigationTarget.Borrow(new Page("late")))) is ObjectDisposedException,
            "Start was accepted on a closed navigator.");
    }

    private static Exception? Catch(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class StartFailedException() : Exception("the root page failed");

    private sealed class StartAwarePage(string name, NavigationRegion<Page> region) : Page(name), INavigationAware
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;

        public List<string> Arrivals { get; } = [];

        public void OnNavigatedTo(NavigationResume arrival) =>
            Arrivals.Add($"{arrival.Operation}:{region.Current}:{Environment.CurrentManagedThreadId == _thread}");

        public void OnNavigatedFrom(NavigationExit exit) { }
    }
}
