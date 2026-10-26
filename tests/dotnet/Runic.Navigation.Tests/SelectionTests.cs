using TUnit.Core;

namespace Runic.Navigation.Tests;

// #648: a navigator-known selection among sibling regions that an owner owns, such as tabs that each keep their own
// stack. Changing it is a transition that asks the region left as retained; the regions stay the owner's, so retiring the
// owner asks every one of them.
internal sealed partial class NavigationTests
{
    // Rules 2-4: a switch asks the current entry of the region left, and of the regions below it, deepest first, as
    // Retain with Select. The commit changes only the selection: no stack, no entry state, no entry notification.
    [Test]
    public async Task SelectAsksTheLeftRegionAsRetainedAsync()
    {
        await using var fixture = new Fixture();
        var tabs = await TabsAsync(fixture);
        var selection = tabs.Page.Selection;
        var asked = new Journal();
        tabs.Detail.Guard = (departure, _) => Answer(asked, "detail", departure, true);
        tabs.Editor.ChildRoot.Guard = (departure, _) => Answer(asked, "editor-root", departure, true);
        tabs.Editor.Guard = (departure, _) => Answer(asked, "editor", departure, true);
        tabs.Page.Roots[0].Guard = (departure, _) => Answer(asked, "root0", departure, true);
        tabs.Other.Guard = (departure, _) => Answer(asked, "other", departure, true);
        tabs.Page.Guard = (departure, _) => Answer(asked, "page", departure, true);
        var editorEntry = tabs.Page.Tabs[0].CurrentEntry!;
        var otherEntry = tabs.Page.Tabs[1].CurrentEntry!;
        var notifications = tabs.Detail.Notifications.Text + "|" + tabs.Other.Notifications.Text;
        var raised = new Journal();
        selection.PropertyChanged += (sender, args) => raised.Add($"{args.PropertyName} {ReferenceEquals(sender, selection) && fixture.Context.IsExecuting}");
        var regionChanges = new Journal();
        foreach (var region in tabs.Page.Tabs) region.PropertyChanged += (_, args) => regionChanges.Add(args.PropertyName!);
        Require(selection.Selected == tabs.Page.Tabs[0] && selection.SelectedIndex == 0 && selection.Regions.SequenceEqual(tabs.Page.Tabs)
            && !selection.IsBusy && !selection.IsTransitioning && selection.ToString().StartsWith("NavigationSelection #", StringComparison.Ordinal),
            "The selection did not start with its first region selected.");

        var selected = await Wait(selection.SelectAsync(tabs.Page.Tabs[1]));
        Require(selected is NavigationResult<object>.Committed { Current.Id: var current, Retired.Count: 0, Arrival: NavigationArrivalState.None }
                && current == otherEntry.Id,
            $"The switch gave {selected}.");
        Require(asked.Text == "detail Retain Select,editor Retain Select", $"The switch asked {asked.Text}.");
        Require(selection.Selected == tabs.Page.Tabs[1] && selection.SelectedIndex == 1, "The switch did not select the second region.");
        Require(Names(tabs.Page.Tabs[0]) == "root0,editor" && Names(tabs.Editor.Child) == "root,detail"
            && ReferenceEquals(tabs.Page.Tabs[0].CurrentEntry, editorEntry) && editorEntry.State == NavigationEntryState.Active
            && tabs.Detail.Disposed == 0 && tabs.Editor.Disposed == 0 && tabs.Detail.Resumed == 0 && tabs.Other.Resumed == 0,
            "The switch changed a stack, an entry's state or disposed a page.");
        Require(tabs.Detail.Notifications.Text + "|" + tabs.Other.Notifications.Text == notifications && regionChanges.Text == "",
            $"The switch notified entries or regions: {tabs.Detail.Notifications.Text}|{tabs.Other.Notifications.Text}, {regionChanges.Text}.");
        await Until(() => raised.Text.Split(',').Count(item => item.StartsWith("IsBusy", StringComparison.Ordinal)) == 2,
            $"The selection did not report busy and idle: {raised.Text}.");
        Require(raised.Text.Contains("Selected True,SelectedIndex True", StringComparison.Ordinal)
            && raised.Text.Contains("IsTransitioning True", StringComparison.Ordinal) && !raised.Text.Contains("False", StringComparison.Ordinal),
            $"The selection's notifications were not raised in model turns: {raised.Text}.");

        // Coming back presents the same entry, and asks the region left now.
        var back = await Wait(selection.SelectAsync(tabs.Page.Tabs[0]));
        Require(back is NavigationResult<object>.Committed { Current.Id: var again } && again == editorEntry.Id
                && asked.Text == "detail Retain Select,editor Retain Select,other Retain Select" && tabs.Editor.Child.Current == tabs.Detail,
            $"Returning to the first region gave {back}, asked {asked.Text}.");
    }

    // Rule 4: a veto, a failure or a cancellation keeps the selection and every stack.
    [Test]
    public async Task SelectVetoFailureAndCancellationKeepTheSelectionAsync()
    {
        await using var fixture = new Fixture();
        var tabs = await TabsAsync(fixture);
        var selection = tabs.Page.Selection;
        var target = tabs.Page.Tabs[1];
        var editorId = tabs.Page.Tabs[0].CurrentEntry!.Id;
        bool Kept() => selection.SelectedIndex == 0 && Names(tabs.Page.Tabs[0]) == "root0,editor" && Names(tabs.Editor.Child) == "root,detail";

        tabs.Editor.Guard = (_, _) => ValueTask.FromResult(false);
        var vetoed = await Wait(selection.SelectAsync(target));
        Require(vetoed is NavigationResult<object>.Rejected { Reason: NavigationRejection.Guard, By: var by } && by == editorId && Kept(),
            $"A veto gave {vetoed}.");

        tabs.Editor.Guard = (_, _) => throw new InvalidOperationException("tab guard failed");
        var failed = await Wait(selection.SelectAsync(target));
        Require(failed is NavigationResult<object>.Failed { Phase: NavigationPhase.Guarding, Error: InvalidOperationException } && Kept(),
            $"A throwing guard gave {failed}.");
        var logged = fixture.Logs.Single(1060);
        Require(Equals(logged.State["Region"], nameof(NavigationSelection)) && Equals(logged.State["Operation"], "Select")
            && selection.ToString() == $"NavigationSelection #{logged.State["RegionId"]}",
            $"Event 1060 did not name the selection: {logged.Message}");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tabs.Editor.Guard = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
            return true;
        };
        using var cancellation = new CancellationTokenSource();
        var selecting = selection.SelectAsync(target, cancellationToken: cancellation.Token).AsTask();
        await Wait(started.Task);
        cancellation.Cancel();
        Require(await Wait(selecting) is NavigationResult<object>.Rejected { Reason: NavigationRejection.Cancelled } && Kept(),
            "A cancelled switch did not keep the selection.");
        Require(AtOnce(selection.SelectAsync(target, cancellationToken: cancellation.Token)) is NavigationResult<object>.Rejected { Reason: NavigationRejection.Cancelled }
                && Kept(),
            "A switch with a cancelled token was admitted.");
        await Wait(selection.WhenIdleAsync());
        Require(!selection.IsBusy, "The selection stayed busy.");
    }

    // Rule 2: selecting the selected region with nothing pending commits at once and asks nothing; with a request pending
    // it supersedes that request and commits unchanged.
    [Test]
    public async Task SelectingTheSelectedRegionCommitsUnchangedAsync()
    {
        await using var fixture = new Fixture();
        var tabs = await TabsAsync(fixture);
        var selection = tabs.Page.Selection;
        var asked = new Journal();
        var editorId = tabs.Page.Tabs[0].CurrentEntry!.Id;
        var raised = 0;
        selection.PropertyChanged += (_, _) => Interlocked.Increment(ref raised);
        tabs.Editor.Guard = (departure, _) => Answer(asked, "editor", departure, true);

        var same = AtOnce(selection.SelectAsync(tabs.Page.Tabs[0]));
        Require(same is NavigationResult<object>.Committed { Current.Id: var id, Retired.Count: 0 } && id == editorId
                && asked.Text == "" && !selection.IsBusy && !selection.IsTransitioning,
            $"Selecting the selected region gave {same}, asked {asked.Text}.");
        await fixture.Context.InvokeAsync(() => { });
        Require(Volatile.Read(ref raised) == 0, "Selecting the selected region raised PropertyChanged.");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tabs.Editor.Guard = async (departure, token) =>
        {
            await Answer(asked, "editor", departure, true);
            started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
            return true;
        };
        var pending = selection.SelectAsync(tabs.Page.Tabs[1]).AsTask();
        await Wait(started.Task);
        var stay = await Wait(selection.SelectAsync(tabs.Page.Tabs[0]));
        Require(await Wait(pending) is NavigationResult<object>.Superseded
                && stay is NavigationResult<object>.Committed { Current.Id: var stayed } && stayed == editorId
                && selection.SelectedIndex == 0 && asked.Text == "editor Retain Select",
            $"Selecting the selected region while a switch was pending gave {stay}, asked {asked.Text}.");
    }

    // OnCommitted actions run only when the switch commits, in its commit turn before PropertyChanged; with
    // askOnRetain, LeaveConfirmation discards only then.
    [Test]
    public async Task SelectRunsOnCommittedOnlyOnCommitAsync()
    {
        await using var fixture = new Fixture();
        var tabs = await TabsAsync(fixture);
        var selection = tabs.Page.Selection;
        var asked = new Journal();
        var order = new Journal();
        var asks = 0;
        var confirmation = new LeaveConfirmation(() => true, (_, _) =>
        {
            Interlocked.Increment(ref asks);
            return ValueTask.FromResult(true);
        }, () => order.Add($"discard {fixture.Context.IsExecuting}"), askOnRetain: true);
        tabs.Editor.Guard = confirmation.CanDepartAsync;
        var veto = new RecordingRegionGuard("navigator", asked) { Answer = false };
        using var registration = fixture.Navigator.AddDepartureGuard(veto, AskAlways);
        selection.PropertyChanged += (_, args) => order.Add(args.PropertyName!);

        var vetoed = await Wait(selection.SelectAsync(tabs.Page.Tabs[1]));
        Require(vetoed is NavigationResult<object>.Rejected { Reason: NavigationRejection.Guard, By: null } && asks == 1
                && !order.Text.Contains("discard", StringComparison.Ordinal) && selection.SelectedIndex == 0,
            $"A vetoed switch gave {vetoed} and ran {order.Text}.");

        veto.Answer = true;
        var committed = await Wait(selection.SelectAsync(tabs.Page.Tabs[1]));
        Require(committed is NavigationResult<object>.Committed && asks == 2 && selection.SelectedIndex == 1,
            $"The allowed switch gave {committed} after {asks} asks.");
        var text = order.Text;
        Require(text.Split(',').Count(item => item.StartsWith("discard", StringComparison.Ordinal)) == 1
                && text.IndexOf("discard True", StringComparison.Ordinal) < text.IndexOf("Selected", StringComparison.Ordinal),
            $"The discard did not run once in the commit turn before PropertyChanged: {text}.");
    }

    // Rule 5: a later switch supersedes an earlier one that hasn't started committing, and a LeaveConfirmation's yes for
    // the superseded switch carries over, so the user is asked once and the discard runs once.
    [Test]
    public async Task LaterSelectWinsWithTheStandingYesAsync()
    {
        await using var fixture = new Fixture();
        var tabs = await TabsAsync(fixture);
        var selection = tabs.Page.Selection;
        var asked = new Journal();
        var asks = 0;
        var discards = 0;
        var confirmation = new LeaveConfirmation(() => true, (_, _) =>
        {
            Interlocked.Increment(ref asks);
            return ValueTask.FromResult(true);
        }, () => discards++, askOnRetain: true);
        tabs.Editor.Guard = confirmation.CanDepartAsync;
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var registration = fixture.Navigator.AddDepartureGuard(new RecordingRegionGuard("navigator", asked)
        {
            Hook = async (_, token) =>
            {
                if (Interlocked.Increment(ref calls) > 1) return true;
                holding.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
                return true;
            },
        }, AskAlways);

        var toSecond = selection.SelectAsync(tabs.Page.Tabs[1]).AsTask();
        await Wait(holding.Task);
        Require(asks == 1 && selection.IsTransitioning, $"The first switch asked {asks} times.");
        var toThird = await Wait(selection.SelectAsync(tabs.Page.Tabs[2]));
        Require(await Wait(toSecond) is NavigationResult<object>.Superseded, "The first switch was not superseded.");
        Require(toThird is NavigationResult<object>.Committed && selection.Selected == tabs.Page.Tabs[2] && asks == 1 && discards == 1
                && asked.Text == "navigator Retain Select,navigator Retain Select",
            $"The later switch gave {toThird}, asked the user {asks} times and discarded {discards} times.");
    }

    // Rules 5 and 8: a switch neither supersedes nor busies the region it leaves, and a commit below that region while
    // the switch asks supersedes the switch: its answers were for another page.
    [Test]
    public async Task CommitBelowTheLeftRegionSupersedesTheSelectAsync()
    {
        await using var fixture = new Fixture();
        var tabs = await TabsAsync(fixture);
        var selection = tabs.Page.Selection;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tabs.Editor.Guard = async (_, _) =>
        {
            started.TrySetResult();
            return await gate.Task;
        };
        var selecting = selection.SelectAsync(tabs.Page.Tabs[1]).AsTask();
        await Wait(started.Task);
        Require(selection.IsTransitioning && selection.IsBusy && fixture.Navigator.IsBusy
                && !tabs.Page.Tabs[0].IsTransitioning && !tabs.Page.Tabs[0].IsBusy && !tabs.Editor.Child.IsBusy,
            "The switch did not make only the selection busy.");
        var pushed = await Wait(tabs.Editor.Child.PushAsync(NavigationTarget.Own(new Page("more"))));
        Require(pushed is NavigationResult<Page>.Committed, $"A push below the region left gave {pushed}.");
        gate.SetResult(true);
        var switched = await Wait(selecting);
        Require(switched is NavigationResult<object>.Superseded && selection.SelectedIndex == 0 && Names(tabs.Editor.Child) == "root,detail,more",
            $"A commit below the region left did not supersede the switch: {switched}.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(!selection.IsBusy && !fixture.Navigator.IsBusy, "The navigator stayed busy after the switch.");
    }

    // Rule 6: a request to the selection from a hook of its own transition, of a region at or below its regions, or of an
    // ancestor region is Reentrant, and so is a request from a selection's hook into those regions.
    [Test]
    public async Task SelectionReentrancyAsync()
    {
        await using var fixture = new Fixture();
        var tabs = await TabsAsync(fixture);
        var selection = tabs.Page.Selection;
        NavigationResult<object>? self = null;
        NavigationResult<Page>? sibling = null;
        NavigationResult<Page>? below = null;
        NavigationResult<Page>? ancestor = null;
        tabs.Editor.Guard = async (departure, token) =>
        {
            if (departure.Operation != NavigationOperation.Select) return true;
            self = await selection.SelectAsync(tabs.Page.Tabs[2], cancellationToken: token);
            sibling = await tabs.Page.Tabs[1].PushAsync(NavigationTarget.Own(new Page("x")), cancellationToken: token);
            below = await tabs.Editor.Child.PushAsync(NavigationTarget.Own(new Page("y")), cancellationToken: token);
            ancestor = await tabs.Main.PushAsync(NavigationTarget.Own(new Page("z")), cancellationToken: token);
            return true;
        };
        var selected = await Wait(selection.SelectAsync(tabs.Page.Tabs[1]));
        Require(selected is NavigationResult<object>.Committed && self is NavigationResult<object>.Rejected { Reason: NavigationRejection.Reentrant }
                && sibling is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Reentrant }
                && below is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Reentrant }
                && ancestor is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Reentrant },
            $"Requests from a switch's guard gave {self}, {sibling}, {below}, {ancestor}.");

        // From a hook of a request of a region below the selection, and of an ancestor region.
        NavigationResult<object>? fromBelow = null;
        tabs.Other.Guard = async (_, token) =>
        {
            fromBelow = await selection.SelectAsync(tabs.Page.Tabs[0], cancellationToken: token);
            return true;
        };
        Require(await Wait(tabs.Page.Tabs[1].BackAsync()) is NavigationResult<Page>.Committed
                && fromBelow is NavigationResult<object>.Rejected { Reason: NavigationRejection.Reentrant },
            $"A switch from a hook of a region of the selection gave {fromBelow}.");
        NavigationResult<object>? fromAncestor = null;
        tabs.Page.Guard = async (_, token) =>
        {
            fromAncestor = await selection.SelectAsync(tabs.Page.Tabs[0], cancellationToken: token);
            return true;
        };
        Require(await Wait(tabs.Main.PushAsync(NavigationTarget.Own(new Page("over")))) is NavigationResult<Page>.Committed
                && fromAncestor is NavigationResult<object>.Rejected { Reason: NavigationRejection.Reentrant } && selection.SelectedIndex == 1,
            $"A switch from a hook of an ancestor region gave {fromAncestor}.");
    }

    // Rules 7 and 12: ExpectedCurrent names the entry of the region left (of the removed region for RemoveAsync); a
    // Requester in a region that isn't selected, at any level, is NotCurrent at admission and at commit, while a page in a
    // hidden tab still navigates its own region.
    [Test]
    public async Task SelectionExpectedCurrentAndRequesterAsync()
    {
        await using var fixture = new Fixture();
        var tabs = await TabsAsync(fixture);
        var selection = tabs.Page.Selection;
        var editorId = tabs.Page.Tabs[0].CurrentEntry!.Id;
        var detailId = tabs.Editor.Child.CurrentEntry!.Id;
        var otherId = tabs.Page.Tabs[1].CurrentEntry!.Id;

        var stale = await Wait(selection.SelectAsync(tabs.Page.Tabs[1], new NavigationRequestOptions(otherId)));
        Require(stale is NavigationResult<object>.Rejected { Reason: NavigationRejection.NotCurrent } && selection.SelectedIndex == 0,
            $"ExpectedCurrent naming the entering region's entry gave {stale}.");
        var removeStale = await Wait(selection.RemoveAsync(tabs.Page.Tabs[2], new NavigationRequestOptions(editorId)));
        Require(removeStale is NavigationResult<object>.Rejected { Reason: NavigationRejection.NotCurrent } && selection.Regions.Count == 3,
            $"ExpectedCurrent naming another region's entry gave {removeStale} for a removal.");
        var expected = await Wait(selection.SelectAsync(tabs.Page.Tabs[1], new NavigationRequestOptions(editorId) { RetainView = true }));
        Require(expected is NavigationResult<object>.Committed && selection.SelectedIndex == 1,
            $"ExpectedCurrent naming the region left's entry gave {expected}.");

        // Tab 0 is hidden now: its pages, at any level, can't navigate the page that shows the tabs, nor a sibling region.
        foreach (var requester in new[] { editorId, detailId })
        {
            var hidden = await Wait(tabs.Main.PushAsync(NavigationTarget.Own(new Page("from-hidden")), new() { Requester = requester }));
            Require(hidden is NavigationResult<Page>.Rejected { Reason: NavigationRejection.NotCurrent, By: var by } && by == requester,
                $"A request for an entry of a hidden region gave {hidden}.");
        }
        var toSibling = await Wait(tabs.Page.Tabs[1].PushAsync(NavigationTarget.Own(new Page("sibling")), new() { Requester = editorId }));
        Require(toSibling is NavigationResult<Page>.Rejected { Reason: NavigationRejection.NotCurrent, By: var siblingBy } && siblingBy == editorId,
            $"A request for a hidden entry into a sibling region gave {toSibling}.");
        var own = await Wait(tabs.Editor.Child.BackAsync(new NavigationRequestOptions(detailId)));
        Require(own is NavigationResult<Page>.Committed && tabs.Editor.Child.Current == tabs.Editor.ChildRoot,
            $"A page in a hidden region could not navigate its own region: {own}.");

        // Selected at admission, hidden at commit.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tabs.Page.Guard = async (_, _) =>
        {
            started.TrySetResult();
            return await gate.Task;
        };
        var late = tabs.Main.PushAsync(NavigationTarget.Own(new Page("late")), new() { Requester = otherId }).AsTask();
        await Wait(started.Task);
        var switched = await Wait(selection.SelectAsync(tabs.Page.Tabs[0]));
        Require(switched is NavigationResult<object>.Committed, $"The switch while the request guarded gave {switched}.");
        gate.SetResult(true);
        var rejected = await Wait(late);
        Require(rejected is NavigationResult<Page>.Rejected { Reason: NavigationRejection.NotCurrent, By: var lateBy } && lateBy == otherId
                && tabs.Main.Current == tabs.Page,
            $"A request whose requester's region was hidden before the commit gave {rejected}.");
        tabs.Page.Guard = null;
        var shown = await Wait(tabs.Main.PushAsync(NavigationTarget.Own(new Page("from-shown")), new() { Requester = editorId }));
        Require(shown is NavigationResult<Page>.Committed, $"A request for an entry of the selected region gave {shown}.");
    }

    // Rules 5, 9 and 13: retiring the page asks every entry of every region, deepest first, region by region, whatever is
    // selected; it supersedes the switch pending at its admission, and a later switch is Rejected(Closed) when it commits.
    [Test]
    public async Task PageRetirementAsksEveryRegionAndClosesTheSelectionAsync()
    {
        await using var fixture = new Fixture();
        var tabs = await TabsAsync(fixture);
        var selection = tabs.Page.Selection;
        var asked = new Journal();
        var selecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tabs.Editor.Guard = async (departure, token) =>
        {
            await Answer(asked, "editor", departure, true);
            if (departure.Operation != NavigationOperation.Select) return true;
            selecting.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
            return true;
        };
        tabs.Detail.Guard = (departure, _) => Answer(asked, "detail", departure, true);
        tabs.Editor.ChildRoot.Guard = (departure, _) => Answer(asked, "editor-root", departure, true);
        for (var index = 0; index < 3; index++)
        {
            var name = $"root{index}";
            tabs.Page.Roots[index].Guard = (departure, _) => Answer(asked, name, departure, true);
        }
        tabs.Other.Guard = (departure, _) => Answer(asked, "other", departure, true);
        var retiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retire = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tabs.Page.Guard = async (departure, _) =>
        {
            await Answer(asked, "page", departure, true);
            retiring.TrySetResult();
            return await retire.Task;
        };

        var early = selection.SelectAsync(tabs.Page.Tabs[1]).AsTask();
        await Wait(selecting.Task);
        var back = tabs.Main.BackAsync().AsTask();
        Require(await Wait(early) is NavigationResult<object>.Superseded, "The page's retirement did not supersede the pending switch.");
        await Wait(retiring.Task);
        selecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var later = selection.SelectAsync(tabs.Page.Tabs[2]).AsTask();
        await Wait(selecting.Task);
        retire.SetResult(true);
        Require(await Wait(back) is NavigationResult<Page>.Committed { Current.Content.Name: "home" }, "The page did not retire.");
        Require(await Wait(later) is NavigationResult<object>.Rejected { Reason: NavigationRejection.Closed },
            "A switch admitted after the page's retirement was not closed.");
        Require(asked.Text == "detail Retain Select,editor Retain Select,detail Retire Back,editor-root Retire Back,editor Retire Back,"
            + "root0 Retire Back,other Retire Back,root1 Retire Back,root2 Retire Back,page Retire Back,detail Retain Select,editor Retain Select",
            $"The retirement asked {asked.Text}.");

        Require(AtOnce(selection.SelectAsync(tabs.Page.Tabs[1])) is NavigationResult<object>.Rejected { Reason: NavigationRejection.Closed }
                && AtOnce(selection.RemoveAsync(tabs.Page.Tabs[1])) is NavigationResult<object>.Rejected { Reason: NavigationRejection.Closed },
            "A closed selection admitted a request.");
        Require(Throws<ObjectDisposedException>(() => selection.Add(fixture.Navigator.CreateRegion<Page>(tabs.Page))),
            "A closed selection accepted a region.");
        Require(await Wait(tabs.Page.Tabs[2].PushAsync(NavigationTarget.Own(new Page("closed")))) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed }
                && tabs.Editor.Disposed == 1 && tabs.Detail.Disposed == 1 && tabs.Other.Disposed == 1,
            "The page's regions did not close, or their owned pages were not disposed.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(fixture.Navigator.UnretiredEntryCount == 1, $"{fixture.Navigator.UnretiredEntryCount} entries are tracked.");
    }

    // Rule 13: a window close clears the window's region, so every region of a tabbed page in it is asked as Retire in one
    // transition, also those never selected.
    [Test]
    public async Task WindowCloseAsksEveryRegionOfATabbedPageAsync()
    {
        await using var fixture = new Fixture();
        var tabs = await TabsAsync(fixture);
        var asked = new Journal();
        for (var index = 0; index < 3; index++)
        {
            var name = $"root{index}";
            tabs.Page.Roots[index].Guard = (departure, _) => Answer(asked, name, departure, true);
        }
        tabs.Other.Guard = (departure, _) => Answer(asked, "other", departure, false);
        var kept = await Wait(tabs.Main.ClearAsync(new() { Admission = NavigationAdmission.Supersede }));
        Require(kept is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard } && asked.Text == "root0 Retire Clear,other Retire Clear"
                && tabs.Main.Current == tabs.Page,
            $"A close vetoed by an unselected region gave {kept}, asked {asked.Text}.");
        tabs.Other.Guard = (departure, _) => Answer(asked, "other", departure, true);
        var closed = await Wait(tabs.Main.ClearAsync(new() { Admission = NavigationAdmission.Supersede }));
        Require(closed is NavigationResult<Page>.Committed && tabs.Main.Current is null
                && asked.Text.EndsWith(",root0 Retire Clear,other Retire Clear,root1 Retire Clear,root2 Retire Clear", StringComparison.Ordinal),
            $"The close gave {closed}, asked {asked.Text}.");
    }

    // Rule 9: a selection whose owner is no entry's content closes with the navigator; disposal asks no guard and ends a
    // pending switch as Closed.
    [Test]
    public async Task SelectionClosesWithTheNavigatorAsync()
    {
        var fixture = new Fixture();
        var asked = new Journal();
        var owner = new object();
        var first = new Page("first");
        var second = new Page("second");
        var regions = new[] { first, second }.Select(page => fixture.Navigator.CreateRegion<Page>(owner, NavigationTarget.Own(page))).ToArray();
        var selection = fixture.Navigator.CreateSelection(owner, regions);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Guard = async (departure, token) =>
        {
            await Answer(asked, "first", departure, true);
            started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
            return true;
        };
        second.Guard = (departure, _) => Answer(asked, "second", departure, true);
        var pending = selection.SelectAsync(regions[1]).AsTask();
        await Wait(started.Task);
        await fixture.DisposeAsync();
        Require(await Wait(pending) is NavigationResult<object>.Rejected { Reason: NavigationRejection.Closed } && asked.Text == "first Retain Select"
                && first.Disposed == 1 && second.Disposed == 1 && fixture.Navigator.UnretiredEntryCount == 0,
            $"Disposal asked {asked.Text} or did not close the pending switch.");
        Require(AtOnce(selection.SelectAsync(regions[1])) is NavigationResult<object>.Rejected { Reason: NavigationRejection.Closed }
                && !selection.IsBusy,
            "The selection admitted a request after disposal.");
        Require(Throws<ObjectDisposedException>(() => selection.Add(regions[0]))
                && Throws<ObjectDisposedException>(() => fixture.Navigator.CreateSelection(owner, [])),
            "A closed navigator accepted a selection change.");
    }

    // Rule 9: a push over the owner's entry follows each region's WhileParentRetained, whatever is selected.
    [Test]
    public async Task PushOverTheOwnerFollowsWhileParentRetainedAsync()
    {
        await using var fixture = new Fixture();
        var asked = new Journal();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var page = new TabsPage("page", fixture.Navigator, regionOptions: new NavigationRegionOptions(NavigationChildRetention.ResetToRoot));
        await Wait(main.PushAsync(NavigationTarget.Own<Page>(page)));
        var hidden = new Page("hidden");
        await Wait(page.Tabs[1].PushAsync(NavigationTarget.Own(hidden)));
        hidden.Guard = (departure, _) => Answer(asked, "hidden", departure, true);
        page.Guard = (departure, _) => Answer(asked, "page", departure, true);
        var pushed = await Wait(main.PushAsync(NavigationTarget.Own(new Page("over"))));
        Require(pushed is NavigationResult<Page>.Committed && asked.Text == "hidden Retire Push,page Retain Push"
                && Names(page.Tabs[1]) == "root1" && hidden.Disposed == 1 && page.Selection.SelectedIndex == 0,
            $"A push over the page gave {pushed}, asked {asked.Text}, left {Names(page.Tabs[1])}.");
    }

    // Rule 3: of a nested selection, a switch follows only the selected region, and a commit of the nested selection
    // while the outer switch asks supersedes the outer switch. Retiring the page still asks every nested region.
    [Test]
    public async Task NestedSelectionsFollowOnlyTheSelectedRegionAsync()
    {
        await using var fixture = new Fixture();
        var asked = new Journal();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var page = new TabsPage("page", fixture.Navigator, count: 2);
        await Wait(main.PushAsync(NavigationTarget.Own<Page>(page)));
        var inner = new TabsPage("inner", fixture.Navigator, count: 2, options: new() { SelectedIndex = 1 });
        await Wait(page.Tabs[0].PushAsync(NavigationTarget.Own<Page>(inner)));
        var shown = new Page("shown");
        var hidden = new Page("hidden");
        await Wait(inner.Tabs[1].PushAsync(NavigationTarget.Own(shown)));
        await Wait(inner.Tabs[0].PushAsync(NavigationTarget.Own(hidden)));
        shown.Guard = (departure, _) => Answer(asked, "shown", departure, true);
        hidden.Guard = (departure, _) => Answer(asked, "hidden", departure, true);
        inner.Guard = (departure, _) => Answer(asked, "inner", departure, true);

        var selected = await Wait(page.Selection.SelectAsync(page.Tabs[1]));
        Require(selected is NavigationResult<object>.Committed && asked.Text == "shown Retain Select,inner Retain Select",
            $"A switch over a nested selection gave {selected}, asked {asked.Text}.");
        Require(await Wait(page.Selection.SelectAsync(page.Tabs[0])) is NavigationResult<object>.Committed, "The switch back did not commit.");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.Guard = async (departure, _) =>
        {
            await Answer(asked, "inner", departure, true);
            started.TrySetResult();
            return await gate.Task;
        };
        var outer = page.Selection.SelectAsync(page.Tabs[1]).AsTask();
        await Wait(started.Task);
        var nested = await Wait(inner.Selection.SelectAsync(inner.Tabs[0]));
        Require(nested is NavigationResult<object>.Committed && inner.Selection.SelectedIndex == 0, $"The nested switch gave {nested}.");
        gate.SetResult(true);
        Require(await Wait(outer) is NavigationResult<object>.Superseded && page.Selection.SelectedIndex == 0,
            "A nested switch did not supersede the outer switch.");

        inner.Guard = (departure, _) => Answer(asked, "inner", departure, true);
        Require(await Wait(main.BackAsync()) is NavigationResult<Page>.Committed
                && asked.Text.EndsWith(",hidden Retire Back,shown Retire Back,inner Retire Back", StringComparison.Ordinal),
            $"Retiring the page did not ask every nested region: {asked.Text}.");
    }

    // Rules 10 and 11: Add appends synchronously and selects the first region of an empty selection without a transition;
    // RemoveAsync retires and closes one region and selects the next.
    [Test]
    public async Task AddAndRemoveAsync()
    {
        await using var fixture = new Fixture();
        var asked = new Journal();
        var owner = new object();
        var selection = fixture.Navigator.CreateSelection(owner, []);
        Require(selection.Regions.Count == 0 && selection.Selected is null && selection.SelectedIndex == -1 && !selection.IsBusy
                && ReferenceEquals(selection.Navigator, fixture.Navigator) && selection.Admission == NavigationAdmission.Supersede,
            "An empty selection did not start empty.");
        var pages = "abcd".Select(name => new Page(name.ToString())).ToArray();
        foreach (var page in pages)
        {
            var name = page.Name;
            page.Guard = (departure, _) => Answer(asked, name, departure, true);
        }
        var regions = pages.Select(page => fixture.Navigator.CreateRegion<Page>(owner, NavigationTarget.Own(page))).ToArray();
        var raised = new List<(string? Name, int Thread)>();
        selection.PropertyChanged += (_, args) => { lock (raised) raised.Add((args.PropertyName, Environment.CurrentManagedThreadId)); };
        string Raised()
        {
            lock (raised)
            {
                // The selection's state; IsTransitioning and IsBusy follow every request.
                var text = string.Join(",", raised.Select(item => item.Name)
                    .Where(name => name is not (nameof(NavigationSelection.IsTransitioning) or nameof(NavigationSelection.IsBusy))));
                raised.Clear();
                return text;
            }
        }

        var thread = Environment.CurrentManagedThreadId;
        selection.Add(regions[0]);
        bool OnCaller() { lock (raised) return raised.All(item => item.Thread == thread); }
        Require(OnCaller() && Raised() == "Regions,Selected,SelectedIndex" && selection.Selected == regions[0] && selection.SelectedIndex == 0 && asked.Text == "",
            "Adding to an empty selection did not select the region at once.");
        selection.Add(regions[1]);
        Require(OnCaller() && Raised() == "Regions" && selection.Selected == regions[0] && selection.Regions.SequenceEqual(regions[..2]),
            "Adding a second region changed the selection.");

        // Add supersedes no pending switch.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pages[0].Guard = async (departure, _) =>
        {
            await Answer(asked, "a", departure, true);
            started.TrySetResult();
            return await gate.Task;
        };
        var switching = selection.SelectAsync(regions[1]).AsTask();
        await Wait(started.Task);
        selection.Add(regions[2]);
        gate.SetResult(true);
        Require(await Wait(switching) is NavigationResult<object>.Committed && selection.SelectedIndex == 1 && selection.Regions.SequenceEqual(regions[..3]),
            "Add superseded the pending switch.");
        pages[0].Guard = (departure, _) => Answer(asked, "a", departure, true);
        await Wait(selection.WhenIdleAsync());
        _ = Raised();

        // A removal asks every entry of the region, deepest first, as Retire with Clear.
        var document = new ParentPage("document", fixture.Navigator);
        document.Guard = (departure, _) => Answer(asked, "document", departure, true);
        document.ChildRoot.Guard = (departure, _) => Answer(asked, "document-root", departure, true);
        await Wait(regions[1].PushAsync(NavigationTarget.Own<Page>(document)));
        var note = new Page("note");
        note.Guard = (departure, _) => Answer(asked, "note", departure, true);
        await Wait(document.Child.PushAsync(NavigationTarget.Own(note)));
        var retiring = new[] { regions[1].CurrentEntry!.Id, regions[1].History[0].Id, document.Child.CurrentEntry!.Id, document.Child.History[0].Id };
        pages[1].Guard = (_, _) => ValueTask.FromResult(false);
        var vetoed = await Wait(selection.RemoveAsync(regions[1]));
        Require(vetoed is NavigationResult<object>.Rejected { Reason: NavigationRejection.Guard } && selection.Regions.Count == 3
                && selection.SelectedIndex == 1 && Names(regions[1]) == "b,document" && Raised() == "",
            $"A vetoed removal gave {vetoed} or changed the selection.");
        var mark = asked.Text;
        pages[1].Guard = (departure, _) => Answer(asked, "b", departure, true);
        var removed = await Wait(selection.RemoveAsync(regions[1]));
        Require(removed is NavigationResult<object>.Committed { Current.Id: var current, Retired: var retired }
                && current == regions[2].CurrentEntry!.Id && retired.Select(id => id.Value).Order().SequenceEqual(retiring.Select(id => id.Value).Order()),
            $"Removing the selected region gave {removed}.");
        Require(asked.Text == mark + ",note Retire Clear,document-root Retire Clear,document Retire Clear,b Retire Clear",
            $"The removal asked {asked.Text[mark.Length..]}.");
        Require(selection.Regions.SequenceEqual([regions[0], regions[2]]) && selection.Selected == regions[2] && selection.SelectedIndex == 1
                && Raised() == "Regions,Selected" && regions[1].Current is null && document.Disposed == 1 && note.Disposed == 1 && pages[1].Disposed == 1,
            "The removal did not select the next region, or did not retire the removed region's entries.");
        Require(await Wait(regions[1].PushAsync(NavigationTarget.Own(new Page("late")))) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed }
                && Throws<ArgumentException>(() => _ = selection.SelectAsync(regions[1]).AsTask())
                && Throws<ArgumentException>(() => selection.Add(regions[1])),
            "The removed region was not closed and gone from the selection.");

        // A region before the selected one: the index moves, the selection stays.
        Require(await Wait(selection.RemoveAsync(regions[0])) is NavigationResult<object>.Committed && selection.Selected == regions[2]
                && selection.SelectedIndex == 0 && Raised() == "Regions,SelectedIndex",
            "Removing a region before the selected one changed the selection.");

        // The last region selected: the previous one becomes selected; the only region: none.
        selection.Add(regions[3]);
        Require(await Wait(selection.SelectAsync(regions[3])) is NavigationResult<object>.Committed, "The switch to the last region did not commit.");
        _ = Raised();
        Require(await Wait(selection.RemoveAsync(regions[3])) is NavigationResult<object>.Committed { Current.Id: var previous }
                && previous == regions[2].CurrentEntry!.Id && selection.Selected == regions[2] && selection.SelectedIndex == 0
                && Raised() == "Regions,Selected,SelectedIndex",
            "Removing the selected last region did not select the previous one.");

        // Removal supersedes the region's pending requests.
        var guarding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pages[2].Guard = async (departure, token) =>
        {
            await Answer(asked, "c", departure, true);
            if (departure.Operation != NavigationOperation.Push) return true;
            guarding.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
            return true;
        };
        var push = regions[2].PushAsync(NavigationTarget.Own(new Page("pending"))).AsTask();
        await Wait(guarding.Task);
        var last = await Wait(selection.RemoveAsync(regions[2]));
        Require(await Wait(push) is NavigationResult<Page>.Superseded
                && last is NavigationResult<object>.Committed { Current: null } && selection.Selected is null && selection.SelectedIndex == -1
                && selection.Regions.Count == 0,
            $"Removing the only region gave {last}, or did not supersede its pending push.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(fixture.Navigator.UnretiredEntryCount == 0, $"{fixture.Navigator.UnretiredEntryCount} entries are tracked after the removals.");
    }

    // Rule 1: CreateSelection, Add, SelectAsync and RemoveAsync check their arguments; no failed call leaves a trace.
    [Test]
    public async Task SelectionArgumentErrorsAsync()
    {
        await using var fixture = new Fixture();
        await using var other = new Fixture();
        var navigator = fixture.Navigator;
        var owner = new object();
        var a = navigator.CreateRegion<Page>(owner);
        var b = navigator.CreateRegion<Page>(owner);
        var foreign = other.Navigator.CreateRegion<Page>(owner);
        var stranger = navigator.CreateRegion<Page>(new object());
        Require(Throws<ArgumentNullException>(() => navigator.CreateSelection(null!, [a])) && Throws<ArgumentNullException>(() => navigator.CreateSelection(owner, null!)),
            "CreateSelection accepted a null argument.");
        Require(Throws<ArgumentException>(() => navigator.CreateSelection(owner, [a, foreign]))
                && Throws<ArgumentException>(() => navigator.CreateSelection(owner, [a, stranger]))
                && Throws<ArgumentException>(() => navigator.CreateSelection(owner, [a, a]))
                && Throws<ArgumentException>(() => navigator.CreateSelection(owner, [a, null!])),
            "CreateSelection accepted a region of another navigator or owner, a repeated region, or null.");
        Require(Throws<ArgumentOutOfRangeException>(() => navigator.CreateSelection(owner, [a, b], new() { SelectedIndex = 2 }))
                && Throws<ArgumentOutOfRangeException>(() => navigator.CreateSelection(owner, [a, b], new() { SelectedIndex = -1 })),
            "CreateSelection accepted a SelectedIndex out of range.");
        Require(navigator.CreateSelection(new object(), [], new() { SelectedIndex = 5 }).SelectedIndex == -1,
            "An empty selection did not ignore SelectedIndex.");

        var selection = navigator.CreateSelection(owner, [a, b], new() { SelectedIndex = 1, Admission = NavigationAdmission.RejectWhileBusy });
        Require(selection.Selected == b && selection.SelectedIndex == 1 && selection.Admission == NavigationAdmission.RejectWhileBusy,
            "The selection did not take its options, or a failed call kept a region.");
        Require(Throws<ArgumentException>(() => navigator.CreateSelection(owner, [a])) && Throws<ArgumentException>(() => selection.Add(a))
                && Throws<ArgumentException>(() => selection.Add(stranger)) && Throws<ArgumentException>(() => selection.Add(foreign))
                && Throws<ArgumentNullException>(() => selection.Add(null!)),
            "A region was taken twice, or Add accepted a foreign region.");
        Require(Throws<ArgumentException>(() => _ = selection.SelectAsync(stranger).AsTask()) && Throws<ArgumentException>(() => _ = selection.RemoveAsync(stranger).AsTask())
                && Throws<ArgumentNullException>(() => _ = selection.SelectAsync(null!).AsTask()) && Throws<ArgumentNullException>(() => _ = selection.RemoveAsync(null!).AsTask()),
            "SelectAsync or RemoveAsync accepted a region that isn't the selection's.");

        // Shared content can't own a selection; a retired owner's regions are closed, and its selection starts closed.
        var main = navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var shared = new Page("shared");
        await Wait(main.PushAsync(NavigationTarget.Share(shared)));
        Require(Throws<InvalidOperationException>(() => navigator.CreateSelection(shared, [])), "Shared content owned a selection.");
        var page = new ParentPage("page", navigator);
        await Wait(main.PushAsync(NavigationTarget.Own<Page>(page)));
        await Wait(main.BackAsync());
        Require(Throws<ArgumentException>(() => navigator.CreateSelection(page, [page.Child])), "A closed region was selectable.");
        var retired = navigator.CreateSelection(page, []);
        Require(Throws<ObjectDisposedException>(() => retired.Add(navigator.CreateRegion<Page>(page))), "A retired owner's selection did not start closed.");

        await navigator.DisposeAsync();
        Require(Throws<ObjectDisposedException>(() => navigator.CreateSelection(new object(), [])), "A closed navigator created a selection.");
    }

    // The outcome of a request that ended at admission, or null while it runs.
    private static NavigationResult<object>? AtOnce(ValueTask<NavigationResult<object>> request)
    {
        var task = request.AsTask();
        return task.IsCompleted ? task.Result : null;
    }

    // Three tabs, each a region of the page; tab 0 shows editor, whose own region shows detail, and tab 1 shows other.
    private sealed record TabsFixture(NavigationRegion<Page> Main, TabsPage Page, ParentPage Editor, NotedPage Detail, NotedPage Other);

    private static async Task<TabsFixture> TabsAsync(Fixture fixture, NavigationSelectionOptions? options = null)
    {
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var page = new TabsPage("page", fixture.Navigator, options: options);
        Require(await Wait(main.PushAsync(NavigationTarget.Own<Page>(page))) is NavigationResult<Page>.Committed, "The tabbed page was not pushed.");
        var editor = new ParentPage("editor", fixture.Navigator);
        await Wait(page.Tabs[0].PushAsync(NavigationTarget.Own<Page>(editor)));
        var detail = new NotedPage("detail");
        await Wait(editor.Child.PushAsync(NavigationTarget.Own<Page>(detail)));
        var other = new NotedPage("other");
        await Wait(page.Tabs[1].PushAsync(NavigationTarget.Own<Page>(other)));
        return new(main, page, editor, detail, other);
    }

    // A page whose tabs each keep their own stack: one region per tab, owned by the page, and a selection among them.
    private sealed class TabsPage : Page
    {
        public TabsPage(string name, RunicNavigator navigator, int count = 3, NavigationSelectionOptions? options = null,
            NavigationRegionOptions? regionOptions = null) : base(name)
        {
            Roots = [.. Enumerable.Range(0, count).Select(index => new Page($"root{index}"))];
            Tabs = [.. Roots.Select(root => navigator.CreateRegion<Page>(this, NavigationTarget.Borrow(root), regionOptions))];
            Selection = navigator.CreateSelection(this, Tabs, options);
        }

        public Page[] Roots { get; }
        public NavigationRegion<Page>[] Tabs { get; }
        public NavigationSelection Selection { get; }
    }

    // Records the entry notifications and arrival hooks a page receives.
    private sealed class NotedPage(string name) : Page(name), INavigationAware, INavigationArrived
    {
        public Journal Notifications { get; } = new();

        public void OnNavigatedTo(NavigationArrival arrival) => Notifications.Add($"to {arrival.Operation}");

        public void OnNavigatedFrom(NavigationExit exit) => Notifications.Add($"from {exit.Operation} {exit.Kind}");

        public ValueTask OnArrivedAsync(NavigationEntryContext entry, NavigationArrival arrival, CancellationToken cancellationToken)
        {
            Notifications.Add($"arrived {arrival.Operation}");
            return ValueTask.CompletedTask;
        }
    }
}
