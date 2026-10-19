using TUnit.Core;

namespace Runic.Navigation.Tests;

// W315-020 point 3 (#350): departure guards for a whole region (NavigationRegion<T>.AddDepartureGuard) and for the
// navigator (RunicNavigator.AddDepartureGuard).
internal sealed partial class NavigationTests
{
    private static readonly NavigationRegionGuardOptions AskAlways = new() { AskOnRetain = true, AskOnClearHistory = true };

    // Entry guards deepest first, then region guards deepest region first, then the navigator's, with each departure's
    // region, kind and entries.
    [Test]
    public async Task RegionGuardsRunAfterEntryGuardsDeepestFirstAsync()
    {
        await using var fixture = new Fixture();
        var asked = new Journal();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var parent = new ParentPage("parent", fixture.Navigator);
        await Wait(main.PushAsync(NavigationTarget.Own<Page>(parent)));
        var detail = new Page("detail");
        await Wait(parent.Child.PushAsync(NavigationTarget.Own(detail)));
        detail.Guard = (departure, _) => Answer(asked, "detail", departure, true);
        parent.ChildRoot.Guard = (departure, _) => Answer(asked, "root", departure, true);
        parent.Guard = (departure, _) => Answer(asked, "parent", departure, true);
        var child = new RecordingRegionGuard("child-region", asked);
        var mainGuard = new RecordingRegionGuard("main-region", asked);
        var second = new RecordingRegionGuard("main-region-2", asked);
        var navigatorGuard = new RecordingRegionGuard("navigator", asked);
        using var childRegistration = parent.Child.AddDepartureGuard(child);
        using var mainRegistration = ((INavigationRegion)main).AddDepartureGuard(mainGuard);
        using var secondRegistration = main.AddDepartureGuard(second);
        using var navigatorRegistration = fixture.Navigator.AddDepartureGuard(navigatorGuard);
        var detailId = parent.Child.CurrentEntry!.Id;
        var rootId = parent.Child.History[0].Id;
        var parentId = main.CurrentEntry!.Id;

        var back = await Wait(main.BackAsync());
        Require(back is NavigationResult<Page>.Committed && Names(main) == "home", $"The parent Back gave {back}.");
        Require(asked.Text == "detail Retire Back,root Retire Back,parent Retire Back,child-region Retire Back,main-region Retire Back,"
            + "main-region-2 Retire Back,navigator Retire Back", $"The guards were asked as {asked.Text}.");
        Require(child.Departures is [{ Entries: [var d, var r] } childDeparture] && d == detailId && r == rootId
            && ReferenceEquals(childDeparture.Region, parent.Child) && childDeparture.ModelContext == fixture.Context,
            "The child region's guard did not get its region and its entries, current first.");
        Require(mainGuard.Departures is [{ Entries: [var p] } mainDeparture] && p == parentId && ReferenceEquals(mainDeparture.Region, main),
            "The main region's guard did not get its region and entry.");
        Require(navigatorGuard.Departures is [{ Entries: [var n1, var n2, var n3] } navigatorDeparture] && n1 == detailId && n2 == rootId && n3 == parentId
            && ReferenceEquals(navigatorDeparture.Region, main),
            "The navigator's guard did not get the requested region and every departing entry, deepest first.");
    }

    // A veto rejects without a By entry; a throwing guard fails Guarding with event 1060 naming the guard.
    [Test]
    public async Task RegionGuardVetoAndFailureAsync()
    {
        await using var fixture = new Fixture();
        var asked = new Journal();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        await Wait(main.PushAsync(NavigationTarget.Own(new Page("document"))));
        var veto = new RecordingRegionGuard("veto", asked) { Answer = false };
        var later = new RecordingRegionGuard("later", asked);
        var registration = main.AddDepartureGuard(veto);
        using var navigatorRegistration = fixture.Navigator.AddDepartureGuard(later);
        var vetoed = await Wait(main.BackAsync());
        Require(vetoed is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard, By: null } && Names(main) == "home,document"
            && asked.Text == "veto Retire Back", $"A vetoing region guard gave {vetoed}, asked {asked.Text}.");

        registration.Dispose();
        registration.Dispose();
        using var throwing = main.AddDepartureGuard(new ThrowingRegionGuard());
        var failed = await Wait(main.BackAsync());
        Require(failed is NavigationResult<Page>.Failed { Phase: NavigationPhase.Guarding, Error: InvalidOperationException { Message: "region guard failed" } }
            && Names(main) == "home,document" && asked.Text == "veto Retire Back",
            $"A throwing region guard gave {failed}, asked {asked.Text}.");
        var logged = fixture.Logs.Single(1060);
        Require(Equals(logged.State["EntryType"], nameof(ThrowingRegionGuard)) && Equals(logged.State["Operation"], "Back"),
            $"Event 1060 did not name the region guard: {logged.Message}");
    }

    // Region and navigator guards are skipped for ClearHistory (also of child regions it closes) unless they opt in; the
    // retained entries' own guards are still asked.
    [Test]
    public async Task ClearHistorySkipsRegionGuardsAsync()
    {
        await using var fixture = new Fixture();
        var asked = new Journal();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var parent = new ParentPage("parent", fixture.Navigator);
        await Wait(main.PushAsync(NavigationTarget.Own<Page>(parent)));
        await Wait(main.PushAsync(NavigationTarget.Own(new Page("top"))));
        parent.Guard = (departure, _) => Answer(asked, "parent", departure, true);
        using var mainRegistration = main.AddDepartureGuard(new RecordingRegionGuard("main", asked));
        using var childRegistration = parent.Child.AddDepartureGuard(new RecordingRegionGuard("child", asked));
        using var navigatorRegistration = fixture.Navigator.AddDepartureGuard(new RecordingRegionGuard("navigator", asked));
        using var optIn = main.AddDepartureGuard(new RecordingRegionGuard("opted-in", asked), new NavigationRegionGuardOptions { AskOnClearHistory = true });

        var cleared = await Wait(main.ClearHistoryAsync());
        Require(cleared is NavigationResult<Page>.Committed && Names(main) == "top", $"ClearHistory gave {cleared}.");
        Require(asked.Text == "parent Retire ClearHistory,opted-in Retire ClearHistory", $"ClearHistory asked {asked.Text}.");
    }

    // A transition that only retains entries skips region guards unless AskOnRetain; a push into an empty region and a
    // BackTo the current entry ask none.
    [Test]
    public async Task RegionGuardsOnRetainAndWithoutDepartureAsync()
    {
        await using var fixture = new Fixture();
        var asked = new Journal();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        using var defaults = main.AddDepartureGuard(new RecordingRegionGuard("default", asked));
        using var retain = main.AddDepartureGuard(new RecordingRegionGuard("retain", asked), new NavigationRegionGuardOptions { AskOnRetain = true });
        using var navigator = fixture.Navigator.AddDepartureGuard(new RecordingRegionGuard("navigator", asked), AskAlways);

        Require(await Wait(main.PushAsync(NavigationTarget.Own(new Page("first")))) is NavigationResult<Page>.Committed && asked.Text == "",
            $"A push into an empty region asked {asked.Text}.");
        Require(await Wait(main.PushAsync(NavigationTarget.Own(new Page("second")))) is NavigationResult<Page>.Committed
            && asked.Text == "retain Retain Push,navigator Retain Push", $"A push over an entry asked {asked.Text}.");
        Require(await Wait(main.BackToAsync(main.CurrentEntry!.Id)) is NavigationResult<Page>.Committed { Retired.Count: 0 }
            && asked.Text == "retain Retain Push,navigator Retain Push", $"BackTo the current entry asked {asked.Text}.");
    }

    // A tab switch leaves the tab's page region as kept: its region guard is asked with Retain only when it opts in.
    [Test]
    public async Task TabSwitchAsksKeptRegionGuardsOnRetainAsync()
    {
        await using var fixture = new Fixture();
        var asked = new Journal();
        var customers = new ParentPage("customers", fixture.Navigator);
        var reports = new Page("reports");
        var tabs = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow<Page>(customers));
        using var pages = customers.Child.AddDepartureGuard(new RecordingRegionGuard("pages", asked));
        Require(await Wait(tabs.ReplaceAsync(NavigationTarget.Borrow(reports))) is NavigationResult<Page>.Committed && asked.Text == "",
            $"A tab switch asked a default region guard of the kept region: {asked.Text}.");
        using var optIn = customers.Child.AddDepartureGuard(new RecordingRegionGuard("pages-retain", asked),
            new NavigationRegionGuardOptions { AskOnRetain = true });
        await Wait(tabs.ReplaceAsync(NavigationTarget.Borrow<Page>(customers)));
        await Wait(tabs.ReplaceAsync(NavigationTarget.Borrow(reports)));
        Require(asked.Text == "pages-retain Retain Replace", $"The opted-in kept region guard was asked as {asked.Text}.");
    }

    // OnCommitted actions run after the entry guards' actions, in asking order, and are dropped when a later guard vetoes.
    [Test]
    public async Task RegionGuardOnCommittedOrderAsync()
    {
        await using var fixture = new Fixture();
        var actions = new Journal();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var document = new Page("document");
        await Wait(main.PushAsync(NavigationTarget.Own(document)));
        document.Guard = (departure, _) =>
        {
            departure.OnCommitted(() => actions.Add("entry"));
            return ValueTask.FromResult(true);
        };
        var regionGuard = new RecordingRegionGuard("region", new Journal()) { OnCommitted = () => actions.Add("region") };
        var navigatorGuard = new RecordingRegionGuard("navigator", new Journal()) { OnCommitted = () => actions.Add("navigator"), Answer = false };
        using var regionRegistration = main.AddDepartureGuard(regionGuard);
        var navigatorRegistration = fixture.Navigator.AddDepartureGuard(navigatorGuard);
        var vetoed = await Wait(main.BackAsync());
        Require(vetoed is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard } && actions.Text == "",
            $"A vetoed transition ran OnCommitted actions: {actions.Text}.");
        navigatorGuard.Answer = true;
        var back = await Wait(main.BackAsync());
        Require(back is NavigationResult<Page>.Committed && actions.Text == "entry,region,navigator", $"OnCommitted ran as {actions.Text}.");
        Require(Throws<InvalidOperationException>(() => regionGuard.Departures[^1].OnCommitted(() => { })),
            "OnCommitted was accepted after the region guard returned.");
        navigatorRegistration.Dispose();
    }

    // A guard is read when guarding starts: one removed before its turn is not asked, one added during the transition waits.
    [Test]
    public async Task RegionGuardRegistrationSnapshotAsync()
    {
        await using var fixture = new Fixture();
        using var gates = new Gates();
        var asked = new Journal();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var document = new Page("document");
        await Wait(main.PushAsync(NavigationTarget.Own(document)));
        var guarding = gates.Signal();
        var allow = gates.Answer();
        document.Guard = async (_, _) =>
        {
            guarding.TrySetResult();
            return await allow.Task;
        };
        var removed = main.AddDepartureGuard(new RecordingRegionGuard("removed", asked));
        using var kept = main.AddDepartureGuard(new RecordingRegionGuard("kept", asked));
        var back = main.BackAsync().AsTask();
        await Wait(guarding.Task);
        removed.Dispose();
        using var added = main.AddDepartureGuard(new RecordingRegionGuard("added", asked));
        allow.SetResult(true);
        Require(await Wait(back) is NavigationResult<Page>.Committed && asked.Text == "kept Retire Back",
            $"The region guards were asked as {asked.Text}.");
    }

    // A later request supersedes a transition whose region guard runs, cancelling its token; requests from a region guard
    // into its region are Reentrant, into an unrelated region they are admitted.
    [Test]
    public async Task RegionGuardSupersessionAndReentrancyAsync()
    {
        await using var fixture = new Fixture();
        using var gates = new Gates();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var dialogs = fixture.Navigator.CreateRegion<Page>(new object());
        await Wait(main.PushAsync(NavigationTarget.Own(new Page("document"))));
        var started = gates.Signal();
        CancellationToken guardToken = default;
        NavigationResult<Page>? reentrant = null;
        NavigationResult<Page>? sibling = null;
        var blocking = new RecordingRegionGuard("blocking", new Journal())
        {
            Hook = async (_, token) =>
            {
                reentrant = await main.PushAsync(NavigationTarget.Own(new Page("inner")), cancellationToken: token);
                sibling = await dialogs.PushAsync(NavigationTarget.Own(new Page("confirm")), cancellationToken: token);
                guardToken = token;
                started.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
                return true;
            },
        };
        var registration = main.AddDepartureGuard(blocking);
        var back = main.BackAsync().AsTask();
        await Wait(started.Task);
        Require(reentrant is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Reentrant }
            && sibling is NavigationResult<Page>.Committed, $"A region guard's requests gave {reentrant} and {sibling}.");
        registration.Dispose();
        var replace = await Wait(main.ReplaceAsync(NavigationTarget.Own(new Page("other"))));
        Require(await Wait(back) is NavigationResult<Page>.Superseded && guardToken.IsCancellationRequested
            && replace is NavigationResult<Page>.Committed && Names(main) == "home,other",
            $"Superseding a region guard gave {replace}.");
    }

    private static readonly NavigationRegionGuardOptions Before = new() { Order = NavigationRegionGuardOrder.BeforeEntries };

    // #412: guards asked BeforeEntries run before the entries' guards, in the same order as the others (region by region,
    // deepest first, then the navigator's), whatever their registration order; the AfterEntries guards follow the entries.
    [Test]
    public async Task BeforeEntriesGuardsRunBeforeEntryGuardsAsync()
    {
        await using var fixture = new Fixture();
        var asked = new Journal();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var parent = new ParentPage("parent", fixture.Navigator);
        await Wait(main.PushAsync(NavigationTarget.Own<Page>(parent)));
        var detail = new Page("detail");
        await Wait(parent.Child.PushAsync(NavigationTarget.Own(detail)));
        detail.Guard = (departure, _) => Answer(asked, "detail", departure, true);
        parent.ChildRoot.Guard = (departure, _) => Answer(asked, "root", departure, true);
        parent.Guard = (departure, _) => Answer(asked, "parent", departure, true);
        using var navigatorAfter = fixture.Navigator.AddDepartureGuard(new RecordingRegionGuard("navigator-after", asked));
        using var mainAfter = main.AddDepartureGuard(new RecordingRegionGuard("main-after", asked));
        using var navigatorBefore = fixture.Navigator.AddDepartureGuard(new RecordingRegionGuard("navigator-before", asked), Before);
        using var mainBefore = main.AddDepartureGuard(new RecordingRegionGuard("main-before", asked), Before);
        using var childAfter = parent.Child.AddDepartureGuard(new RecordingRegionGuard("child-after", asked),
            new NavigationRegionGuardOptions { Order = NavigationRegionGuardOrder.AfterEntries });
        var childBeforeGuard = new RecordingRegionGuard("child-before", asked);
        using var childBefore = parent.Child.AddDepartureGuard(childBeforeGuard, Before);
        var detailId = parent.Child.CurrentEntry!.Id;
        var rootId = parent.Child.History[0].Id;

        var back = await Wait(main.BackAsync());
        Require(back is NavigationResult<Page>.Committed && Names(main) == "home", $"The parent Back gave {back}.");
        Require(asked.Text == "child-before Retire Back,main-before Retire Back,navigator-before Retire Back,"
            + "detail Retire Back,root Retire Back,parent Retire Back,"
            + "child-after Retire Back,main-after Retire Back,navigator-after Retire Back", $"The guards were asked as {asked.Text}.");
        Require(childBeforeGuard.Departures is [{ Entries: [var d, var r] } departure] && d == detailId && r == rootId
            && ReferenceEquals(departure.Region, parent.Child), "A BeforeEntries guard did not get its region and entries.");
        Require(new NavigationRegionGuardOptions().Order == NavigationRegionGuardOrder.AfterEntries,
            "The default order is not AfterEntries.");
    }

    // #412: a precondition registered on the navigator BeforeEntries with AskOnRetain vetoes a push that retains the page,
    // a Back and a Clear before the page's guard asks its question; the veto has no By entry. Once it allows, the page asks.
    [Test]
    public async Task BeforeEntriesVetoAsksNoEntryGuardAsync()
    {
        await using var fixture = new Fixture();
        var asked = new Journal();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var editor = new Page("editor");
        await Wait(main.PushAsync(NavigationTarget.Own(editor)));
        editor.Guard = (departure, _) => Answer(asked, "editor", departure, true);
        var operation = new RecordingRegionGuard("operation", asked) { Answer = false };
        using var registration = fixture.Navigator.AddDepartureGuard(operation,
            new NavigationRegionGuardOptions { Order = NavigationRegionGuardOrder.BeforeEntries, AskOnRetain = true });
        using var after = fixture.Navigator.AddDepartureGuard(new RecordingRegionGuard("after", asked), AskAlways);

        var push = await Wait(main.PushAsync(NavigationTarget.Own(new Page("next"))));
        var back = await Wait(main.BackAsync());
        var clear = await Wait(main.ClearAsync());
        Require(push is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard, By: null }
            && back is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard, By: null }
            && clear is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard, By: null } && Names(main) == "home,editor",
            $"The operation guard's vetoes gave {push}, {back} and {clear}.");
        Require(asked.Text == "operation Retain Push,operation Retire Back,operation Retire Clear",
            $"A vetoing BeforeEntries guard let other guards be asked: {asked.Text}.");

        operation.Answer = true;
        Require(await Wait(main.BackAsync()) is NavigationResult<Page>.Committed && Names(main) == "home",
            "The Back did not commit once the operation guard allowed it.");
        Require(asked.Text.EndsWith(",operation Retire Back,editor Retire Back,after Retire Back", StringComparison.Ordinal),
            $"The allowed Back asked {asked.Text}.");
    }

    // #412: Order does not change when a guard is asked: a BeforeEntries guard is skipped on retain and for ClearHistory
    // unless it opts in, and its OnCommitted actions run before the entries' guards', in asking order.
    [Test]
    public async Task BeforeEntriesSkipRulesAndOnCommittedOrderAsync()
    {
        await using var fixture = new Fixture();
        var asked = new Journal();
        var actions = new Journal();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        Page Guarded(string name)
        {
            var page = new Page(name);
            page.Guard = (departure, _) =>
            {
                asked.Add($"{name} {departure.Kind} {departure.Operation}");
                departure.OnCommitted(() => actions.Add("entry"));
                return ValueTask.FromResult(true);
            };
            return page;
        }
        await Wait(main.PushAsync(NavigationTarget.Own(Guarded("document"))));
        using var before = main.AddDepartureGuard(new RecordingRegionGuard("before", asked) { OnCommitted = () => actions.Add("before") }, Before);
        using var after = main.AddDepartureGuard(new RecordingRegionGuard("after", asked) { OnCommitted = () => actions.Add("after") });

        await Wait(main.PushAsync(NavigationTarget.Own(new Page("top"))));
        Require(asked.Text == "document Retain Push", $"A push that retains asked {asked.Text}.");
        await Wait(main.ClearHistoryAsync());
        Require(asked.Text == "document Retain Push,document Retire ClearHistory", $"ClearHistory asked {asked.Text}.");
        using var optIn = main.AddDepartureGuard(new RecordingRegionGuard("opted-in", asked),
            new NavigationRegionGuardOptions { Order = NavigationRegionGuardOrder.BeforeEntries, AskOnClearHistory = true });
        await Wait(main.PushAsync(NavigationTarget.Own(new Page("again"))));
        await Wait(main.ClearHistoryAsync());
        Require(asked.Text == "document Retain Push,document Retire ClearHistory,opted-in Retire ClearHistory",
            $"ClearHistory with an opted-in guard asked {asked.Text}.");

        await Wait(main.PushAsync(NavigationTarget.Own(Guarded("editor"))));
        var actionsBefore = actions.Text;
        var back = await Wait(main.BackAsync());
        Require(back is NavigationResult<Page>.Committed && asked.Text.EndsWith(
                ",before Retire Back,opted-in Retire Back,editor Retire Back,after Retire Back", StringComparison.Ordinal),
            $"The Back asked {asked.Text}.");
        Require(actions.Text == (actionsBefore.Length == 0 ? "" : actionsBefore + ",") + "before,entry,after",
            $"The OnCommitted actions ran as {actions.Text}.");
    }

    // Disposing the navigator asks no guards.
    [Test]
    public async Task DisposalAsksNoRegionGuardsAsync()
    {
        var asked = new Journal();
        var fixture = new Fixture();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Own(new Page("home")));
        await Wait(main.PushAsync(NavigationTarget.Own(new Page("document"))));
        main.AddDepartureGuard(new RecordingRegionGuard("region", asked), AskAlways);
        fixture.Navigator.AddDepartureGuard(new RecordingRegionGuard("navigator", asked), AskAlways);
        await fixture.DisposeAsync();
        Require(asked.Text == "" && fixture.Navigator.UnretiredEntryCount == 0, $"Disposal asked {asked.Text}.");
    }

    private sealed class RecordingRegionGuard(string name, Journal asked) : INavigationRegionGuard
    {
        public bool Answer { get; set; } = true;
        public Action? OnCommitted { get; init; }
        public Func<NavigationRegionDeparture, CancellationToken, ValueTask<bool>>? Hook { get; init; }
        public List<NavigationRegionDeparture> Departures { get; } = [];

        public ValueTask<bool> CanLeaveAsync(NavigationRegionDeparture departure, CancellationToken cancellationToken)
        {
            lock (Departures) Departures.Add(departure);
            asked.Add($"{name} {departure.Kind} {departure.Operation}");
            if (OnCommitted is { } action) departure.OnCommitted(action);
            return Hook?.Invoke(departure, cancellationToken) ?? ValueTask.FromResult(Answer);
        }
    }

    private sealed class ThrowingRegionGuard : INavigationRegionGuard
    {
        public ValueTask<bool> CanLeaveAsync(NavigationRegionDeparture departure, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("region guard failed");
    }
}
