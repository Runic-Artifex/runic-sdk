using TUnit.Core;

namespace Runic.Navigation.Tests;

// #742: the departure guards of a child region's current entry asked outside that region's own transition: by a push
// that covers the owning entry (NavigationRegionOptions.AskOnParentRetain), and on demand without navigating
// (NavigationRegion.ConfirmDepartureAsync, NavigationOperation.Confirm).
internal sealed partial class NavigationTests
{
    private static readonly NavigationRegionOptions AskedOnParentRetain = new() { AskOnParentRetain = true };

    // A push over a page with tabs asks the page of the shown tab first, as retained; the hidden tab is not asked, and a
    // tab region without the option is not asked either.
    [Test]
    public async Task PushOverAPageAsksTheShownTabAsync()
    {
        await using var fixture = new Fixture();
        var asked = new Journal();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        TabsPage? page = null;
        await Wait(main.PushAsync(NavigationTarget.Create<Page>(_ => page = new TabsPage("page", fixture.Navigator, 2, regionOptions: AskedOnParentRetain))));
        var editor = new Page("editor");
        await Wait(page!.Tabs[0].PushAsync(NavigationTarget.Own(editor)));
        var dirty = true;
        editor.Guard = (departure, _) => Answer(asked, "editor", departure, !dirty);
        page.Roots[1].Guard = (departure, _) => Answer(asked, "second", departure, true);
        page.Guard = (departure, _) => Answer(asked, "page", departure, true);

        var vetoed = await Wait(main.PushAsync(NavigationTarget.Own(new Page("settings"))));
        Require(vetoed is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard, By: var by } && by == page.Tabs[0].CurrentEntry!.Id
                && main.Current == page && asked.Text == "editor Retain Push",
            $"The shown tab's dirty page did not keep the page: {vetoed}, asked {asked.Text}.");

        dirty = false;
        Require(await Wait(main.PushAsync(NavigationTarget.Own(new Page("settings")))) is NavigationResult<Page>.Committed
                && asked.Text == "editor Retain Push,editor Retain Push,page Retain Push" && Names(page.Tabs[0]) == "root0,editor",
            $"The allowed push asked {asked.Text}.");
        Require(await Wait(main.BackAsync()) is NavigationResult<Page>.Committed, "Back to the page failed.");

        // Tab regions without the option are not asked: the push asks only the page.
        var plain = new TabsPage("page", fixture.Navigator, 2);
        Require(await Wait(main.PushAsync(NavigationTarget.Own<Page>(plain))) is NavigationResult<Page>.Committed, "The plain page was not pushed.");
        plain.Roots[0].Guard = (departure, _) => Answer(asked, "plain-first", departure, true);
        plain.Guard = (departure, _) => Answer(asked, "plain", departure, true);
        Require(await Wait(main.PushAsync(NavigationTarget.Own(new Page("other")))) is NavigationResult<Page>.Committed
                && asked.Text.EndsWith(",page Retain Push,plain Retain Push", StringComparison.Ordinal),
            $"A tab region without the option was asked: {asked.Text}.");
    }

    // A commit in the asked tab while the push guards supersedes the push, as for a tab switch.
    [Test]
    public async Task CommitInTheAskedTabSupersedesThePushAsync()
    {
        await using var fixture = new Fixture();
        using var gates = new Gates();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var page = new TabsPage("page", fixture.Navigator, 2, regionOptions: AskedOnParentRetain);
        await Wait(main.PushAsync(NavigationTarget.Own<Page>(page)));
        var guarding = gates.Signal();
        var answer = gates.Answer();
        page.Guard = async (_, _) =>
        {
            guarding.TrySetResult();
            return await answer.Task;
        };
        var push = main.PushAsync(NavigationTarget.Own(new Page("settings"))).AsTask();
        await Wait(guarding.Task);
        Require(await Wait(page.Tabs[0].PushAsync(NavigationTarget.Own(new Page("late")))) is NavigationResult<Page>.Committed,
            "The tab's own push did not commit.");
        answer.SetResult(true);
        Require(await Wait(push) is NavigationResult<Page>.Superseded && main.Current == page,
            "A commit in the asked tab did not supersede the push.");
    }

    // A retiring confirmation asks every entry of the regions the owned page owns, deepest first, then the page; nothing
    // changes, the guards' actions run in the commit turn and the caller's action after them.
    [Test]
    public async Task ConfirmDepartureAsksWithoutNavigatingAsync()
    {
        await using var fixture = new Fixture();
        var asked = new Journal();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var page = new TabsPage("page", fixture.Navigator, 2);
        await Wait(main.PushAsync(NavigationTarget.Own<Page>(page)));
        var editor = new InitPage("editor");
        await Wait(page.Tabs[0].PushAsync(NavigationTarget.Own<Page>(editor)));
        var context = (RunicModelContext)fixture.Context;
        List<string> ran = [];
        var allow = true;
        editor.Guard = (departure, _) =>
        {
            departure.OnCommitted(() => ran.Add($"discard:{context.IsExecuting}"));
            return Answer(asked, "editor", departure, allow);
        };
        page.Roots[0].Guard = (departure, _) => Answer(asked, "first", departure, true);
        page.Roots[1].Guard = (departure, _) => Answer(asked, "second", departure, true);
        page.Guard = (departure, _) => Answer(asked, "page", departure, true);
        var states = 0;
        editor.Entry!.StateChanged += (_, _) => states++;

        allow = false;
        var vetoed = await Wait(main.ConfirmDepartureAsync(NavigationDepartureKind.Retire, () => ran.Add("confirmed")));
        Require(vetoed is NavigationResult<object>.Rejected { Reason: NavigationRejection.Guard, By: var by } && by == page.Tabs[0].CurrentEntry!.Id
                && ran.Count == 0, $"A veto gave {vetoed}, ran {string.Join(",", ran)}.");

        allow = true;
        var before = asked.Text;
        var confirmed = await Wait(main.ConfirmDepartureAsync(NavigationDepartureKind.Retire, () => ran.Add($"confirmed:{context.IsExecuting}")));
        Require(confirmed is NavigationResult<object>.Committed { Current.Content: var current, Retired.Count: 0 } && current == page,
            $"The confirmation gave {confirmed}.");
        Require(asked.Text[(before.Length + 1)..] == "editor Retire Confirm,first Retire Confirm,second Retire Confirm,page Retire Confirm",
            $"The confirmation asked {asked.Text[before.Length..]}.");
        Require(string.Join(",", ran) == "discard:True,confirmed:True", $"The actions ran as {string.Join(",", ran)}.");
        Require(Names(page.Tabs[0]) == "root0,editor" && Names(main) == "home,page" && states == 0 && editor.Disposed == 0,
            "The confirmation changed a region or an entry.");

        // Retained: only the current entries, of the selected tab, then the page.
        before = asked.Text;
        Require(await Wait(main.ConfirmDepartureAsync(NavigationDepartureKind.Retain)) is NavigationResult<object>.Committed
                && asked.Text[(before.Length + 1)..] == "editor Retain Confirm,page Retain Confirm",
            $"A retained confirmation asked {asked.Text[before.Length..]}.");

        // While the page is retained below another page, its tab is asked through the tab's own region.
        await Wait(main.PushAsync(NavigationTarget.Own(new Page("top"))));
        before = asked.Text;
        Require(await Wait(page.Tabs[0].ConfirmDepartureAsync(NavigationDepartureKind.Retire)) is NavigationResult<object>.Committed
                && asked.Text[(before.Length + 1)..] == "editor Retire Confirm" && Names(main) == "home,page,top",
            $"Asking the retained page's tab asked {asked.Text[before.Length..]}.");

        // An empty region asks nothing and commits in a turn.
        var empty = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        var inTurn = false;
        Require(await Wait(empty.ConfirmDepartureAsync(NavigationDepartureKind.Retire, () => inTurn = context.IsExecuting))
                is NavigationResult<object>.Committed { Current: null } && inTurn, "An empty region's confirmation did not commit in a turn.");
        Require(Throws<ArgumentOutOfRangeException>(() => _ = empty.ConfirmDepartureAsync((NavigationDepartureKind)7).AsTask()),
            "An undefined kind was accepted.");
    }

    // A confirmation is a request of its region: a commit in an asked region supersedes it, its token cancels it, Queue
    // waits for the region's pending requests, and its own commit supersedes no other plan.
    [Test]
    public async Task ConfirmDepartureIsARequestOfItsRegionAsync()
    {
        await using var fixture = new Fixture();
        using var gates = new Gates();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var parent = new ParentPage("parent", fixture.Navigator);
        await Wait(main.PushAsync(NavigationTarget.Own<Page>(parent)));
        var guarding = gates.Signal();
        var answer = gates.Answer();
        parent.Guard = async (departure, _) =>
        {
            if (departure.Operation != NavigationOperation.Confirm) return true;
            guarding.TrySetResult();
            return await answer.Task;
        };
        var confirm = main.ConfirmDepartureAsync(NavigationDepartureKind.Retire).AsTask();
        await Wait(guarding.Task);
        Require(main.IsTransitioning && main.IsBusy, "The confirmation did not make its region busy.");
        await Wait(parent.Child.PushAsync(NavigationTarget.Own(new Page("child"))));
        answer.SetResult(true);
        Require(await Wait(confirm) is NavigationResult<object>.Superseded,
            "A commit in an asked region did not supersede the confirmation.");

        // Cancelled through the token.
        guarding = gates.Signal();
        answer = gates.Answer();
        using var cancel = new CancellationTokenSource();
        var cancelled = main.ConfirmDepartureAsync(NavigationDepartureKind.Retire, cancellationToken: cancel.Token).AsTask();
        await Wait(guarding.Task);
        await cancel.CancelAsync();
        answer.SetResult(true);
        Require(await Wait(cancelled) is NavigationResult<object>.Rejected { Reason: NavigationRejection.Cancelled },
            "Cancelling the token did not end the confirmation.");

        // Queue: waits behind a pending push instead of superseding it, then asks the entry the push made current.
        var gate = gates.Signal();
        var slow = new InitPage("slow") { Initialize = async (_, _) => await gate.Task };
        var push = main.PushAsync(NavigationTarget.Own<Page>(slow)).AsTask();
        var queued = main.ConfirmDepartureAsync(NavigationDepartureKind.Retire,
            options: new NavigationRequestOptions { Admission = NavigationAdmission.Queue }).AsTask();
        gate.SetResult();
        Require(await Wait(push) is NavigationResult<Page>.Committed
                && await Wait(queued) is NavigationResult<object>.Committed { Current.Content: var asked } && asked == slow
                && slow.Departures is [{ Operation: NavigationOperation.Confirm, Kind: NavigationDepartureKind.Retire }],
            "A queued confirmation did not wait for the push.");
        await Wait(main.BackAsync());

        // The confirmation's commit changes no version: a parent Back guarding meanwhile still commits.
        guarding = gates.Signal();
        answer = gates.Answer();
        parent.Guard = async (departure, _) =>
        {
            if (departure.Operation != NavigationOperation.Back) return true;
            guarding.TrySetResult();
            return await answer.Task;
        };
        var back = main.BackAsync().AsTask();
        await Wait(guarding.Task);
        Require(await Wait(parent.Child.ConfirmDepartureAsync(NavigationDepartureKind.Retain)) is NavigationResult<object>.Committed,
            "The child's confirmation did not commit while the parent's Back guarded.");
        answer.SetResult(true);
        Require(await Wait(back) is NavigationResult<Page>.Committed && Names(main) == "home",
            "The child's confirmation superseded the parent's Back.");
    }

    // The caller's action throwing fails the confirmation in Committing (event 1062), after the guards' actions ran.
    [Test]
    public async Task ConfirmDepartureActionFailureFailsTheRequestAsync()
    {
        await using var fixture = new Fixture();
        var page = new Page("page");
        var discarded = false;
        page.Guard = (departure, _) =>
        {
            departure.OnCommitted(() => discarded = true);
            return ValueTask.FromResult(true);
        };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(page));
        var failed = await Wait(region.ConfirmDepartureAsync(NavigationDepartureKind.Retire, () => throw new FormatException()));
        Require(failed is NavigationResult<object>.Failed { Phase: NavigationPhase.Committing, Error: FormatException } && discarded
                && fixture.Logs.Single(1062).State["Operation"]?.ToString() == "Confirm",
            $"A throwing action gave {failed}.");
        Require(!region.IsBusy && region.Current == page, "The region did not stay as it was.");
    }

    // LeaveConfirmation asks a retiring confirmation and discards in its commit turn, before the caller's action.
    [Test]
    public async Task ConfirmDepartureWithLeaveConfirmationAsync()
    {
        await using var fixture = new Fixture();
        var dirty = true;
        List<string> ran = [];
        var leave = new LeaveConfirmation(() => dirty, (_, _) => ValueTask.FromResult(true), () =>
        {
            dirty = false;
            ran.Add("discard");
        });
        var page = new Page("page") { Guard = leave.CanDepartAsync };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(page));
        Require(await Wait(region.ConfirmDepartureAsync(NavigationDepartureKind.Retain, () => ran.Add("retain"))) is NavigationResult<object>.Committed
                && dirty, "A retained confirmation asked LeaveConfirmation or discarded.");
        Require(await Wait(region.ConfirmDepartureAsync(NavigationDepartureKind.Retire, () => ran.Add("switch"))) is NavigationResult<object>.Committed
                && !dirty && string.Join(",", ran) == "retain,discard,switch", $"The actions ran as {string.Join(",", ran)}.");
    }
}
