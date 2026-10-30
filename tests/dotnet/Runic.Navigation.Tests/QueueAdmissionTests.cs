using TUnit.Core;

namespace Runic.Navigation.Tests;

// #741: NavigationAdmission.Queue. A request waits for the region's earlier requests, in admission order, and supersedes
// none of them, so result entries of a dialog region stack; a Back retires retained result entries whose request ended.
internal sealed partial class NavigationTests
{
    private static readonly NavigationRegionOptions Queued = new() { Admission = NavigationAdmission.Queue };

    // A second PushForResult while the first is still preparing waits, then stacks above it; the returns resume in order.
    [Test]
    public async Task QueueStacksResultEntriesInOrderAsync()
    {
        await using var fixture = new Fixture();
        using var gates = new Gates();
        var dialog = fixture.Navigator.CreateRegion<Page>(fixture.Root, options: Queued);
        Require(dialog.Admission == NavigationAdmission.Queue, "The region did not take the Queue policy.");
        List<string> shown = [];
        dialog.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(dialog.Current)) shown.Add(dialog.Current?.Name ?? "-");
        };
        var initializing = gates.Signal();
        var initialized = gates.Signal();
        var confirm = new InitPage("confirm")
        {
            Initialize = async (_, _) =>
            {
                initializing.TrySetResult();
                await initialized.Task;
            },
        };
        var first = dialog.PushForResult<bool>(NavigationTarget.Own<Page>(confirm));
        await Wait(initializing.Task);
        var error = new InitPage("error");
        var second = dialog.PushForResult<bool>(NavigationTarget.Own<Page>(error));
        Require(dialog.IsTransitioning && dialog.IsBusy && !second.Transition.IsCompleted,
            "The queued request did not wait for the first one.");
        initialized.SetResult();
        Require(await Wait(first.Transition) is NavigationResult<Page>.Committed, "The first push did not commit.");
        Require(await Wait(second.Transition) is NavigationResult<Page>.Committed && Names(dialog) == "confirm,error",
            $"The queued push did not stack above the first: {Names(dialog)}.");
        Require(!first.Completion.IsCompleted && confirm.Departures is [{ Kind: NavigationDepartureKind.Retain, Operation: NavigationOperation.Push }],
            "The first request ended, or its entry was not asked as retained.");

        Require(await Wait(error.Entry!.CompleteAsync(true)) is NavigationResult<object>.Committed && dialog.Current == confirm,
            "Completing the top entry did not resume the first.");
        Require(await Wait(second.Completion) is NavigationCompletion<bool>.Completed { Value: true }, "The second request did not complete.");
        Require(await Wait(confirm.Entry!.CompleteAsync(false)) is NavigationResult<object>.Committed && dialog.Current is null,
            "Completing the first entry did not empty the region.");
        Require(await Wait(first.Completion) is NavigationCompletion<bool>.Completed { Value: false }, "The first request did not complete.");
        Require(string.Join(",", shown) == "confirm,error,confirm,-" && fixture.Logs.Count(1066) == 0,
            $"The region presented {string.Join(",", shown)}, or a request was superseded.");
    }

    // The token cancels a queued request at once; the requests queued behind it continue in order.
    [Test]
    public async Task QueueCancelsAWaitingRequestThroughItsTokenAsync()
    {
        await using var fixture = new Fixture();
        using var gates = new Gates();
        var dialog = fixture.Navigator.CreateRegion<Page>(fixture.Root, options: Queued);
        var initializing = gates.Signal();
        var initialized = gates.Signal();
        var first = dialog.PushForResult<bool>(NavigationTarget.Own<Page>(new InitPage("first")
        {
            Initialize = async (_, _) =>
            {
                initializing.TrySetResult();
                await initialized.Task;
            },
        }));
        await Wait(initializing.Task);
        using var cancel = new CancellationTokenSource();
        var dropped = new InitPage("dropped");
        var second = dialog.PushForResult<bool>(NavigationTarget.Own<Page>(dropped), cancellationToken: cancel.Token);
        var third = dialog.PushForResult<bool>(NavigationTarget.Own<Page>(new InitPage("third")));
        await cancel.CancelAsync();
        Require(await Wait(second.Transition) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Cancelled }
                && await Wait(second.Completion) is NavigationCompletion<bool>.Dismissed && !first.Transition.IsCompleted,
            "Cancelling a queued request did not end it at once.");
        initialized.SetResult();
        Require(await Wait(first.Transition) is NavigationResult<Page>.Committed && await Wait(third.Transition) is NavigationResult<Page>.Committed
                && Names(dialog) == "first,third", $"The requests around the cancelled one did not commit in order: {Names(dialog)}.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(dropped.Initialized == 0 && dropped.Disposed == 1, "The cancelled request's content ran or was not released.");
    }

    // What a queued request expects of the region is checked when it starts, against the state its predecessors left.
    [Test]
    public async Task QueueChecksExpectationsWhenTheRequestStartsAsync()
    {
        await using var fixture = new Fixture();
        using var gates = new Gates();
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home), Queued);
        var homeId = region.CurrentEntry!.Id;
        var guarding = gates.Signal();
        var allow = gates.Answer();
        home.Guard = async (_, _) =>
        {
            guarding.TrySetResult();
            return await allow.Task;
        };
        var push = region.PushAsync(NavigationTarget.Own(new Page("detail"))).AsTask();
        await Wait(guarding.Task);
        // A Back with one entry would be NoHistory at admission; queued, it pops the entry the push leaves.
        var back = region.BackAsync().AsTask();
        // Expects home, which the push covers before this request starts.
        var stale = region.PushAsync(NavigationTarget.Own(new Page("stale")), new NavigationRequestOptions(homeId)).AsTask();
        Require(!back.IsCompleted && !stale.IsCompleted, "A queued request was decided at admission.");
        home.Guard = null;
        allow.SetResult(true);
        Require(await Wait(push) is NavigationResult<Page>.Committed, "The push did not commit.");
        Require(await Wait(back) is NavigationResult<Page>.Committed { Current.Content: var resumed } && resumed == home,
            "The queued Back did not pop the pushed entry.");
        Require(await Wait(stale) is NavigationResult<Page>.Committed && Names(region) == "home,stale",
            $"A request expecting home after the Back resumed it was rejected: {Names(region)}.");

        // Behind a push, a request expecting the current entry is NotCurrent when it starts.
        var current = region.CurrentEntry!.Id;
        var gate = gates.Signal();
        var slow = new InitPage("slow") { Initialize = async (_, _) => await gate.Task };
        var covering = region.PushAsync(NavigationTarget.Own<Page>(slow)).AsTask();
        var expecting = region.BackAsync(new NavigationRequestOptions(current)).AsTask();
        gate.SetResult();
        Require(await Wait(covering) is NavigationResult<Page>.Committed
                && await Wait(expecting) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.NotCurrent }
                && Names(region) == "home,stale,slow",
            $"A queued request whose expected entry was covered was not NotCurrent: {Names(region)}.");
    }

    // A request admitted with Supersede still supersedes queued requests, as a window close's clear does.
    [Test]
    public async Task SupersedeRequestSupersedesQueuedRequestsAsync()
    {
        await using var fixture = new Fixture();
        using var gates = new Gates();
        var dialog = fixture.Navigator.CreateRegion<Page>(fixture.Root, options: Queued);
        var initializing = gates.Signal();
        var first = dialog.PushForResult<bool>(NavigationTarget.Own<Page>(new InitPage("first")
        {
            Initialize = async (_, token) =>
            {
                initializing.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            },
        }));
        await Wait(initializing.Task);
        var second = dialog.PushForResult<bool>(NavigationTarget.Own<Page>(new InitPage("second")));
        var clear = await Wait(dialog.ClearAsync(new NavigationRequestOptions { Admission = NavigationAdmission.Supersede }));
        Require(clear is NavigationResult<Page>.Committed && dialog.Current is null
                && await Wait(first.Transition) is NavigationResult<Page>.Superseded
                && await Wait(second.Transition) is NavigationResult<Page>.Superseded
                && await Wait(first.Completion) is NavigationCompletion<bool>.Dismissed
                && await Wait(second.Completion) is NavigationCompletion<bool>.Dismissed,
            "A Supersede request did not supersede the queued requests.");

        // Per request: Queue in a region that supersedes.
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var gate = gates.Signal();
        var slow = main.PushAsync(NavigationTarget.Own<Page>(new InitPage("slow") { Initialize = async (_, _) => await gate.Task })).AsTask();
        var next = main.PushAsync(NavigationTarget.Own(new Page("next")), new NavigationRequestOptions { Admission = NavigationAdmission.Queue }).AsTask();
        gate.SetResult();
        Require(await Wait(slow) is NavigationResult<Page>.Committed && await Wait(next) is NavigationResult<Page>.Committed
                && Names(main) == "home,slow,next", $"A per-request Queue did not wait: {Names(main)}.");
    }

    // When a queued request starts, it supersedes the pending requests of the child regions its plan closes, as a
    // request does at admission.
    [Test]
    public async Task QueuedRequestSupersedesChildRequestsWhenItStartsAsync()
    {
        await using var fixture = new Fixture();
        using var gates = new Gates();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")), Queued);
        var parent = new ParentPage("parent", fixture.Navigator);
        Require(await Wait(main.PushAsync(NavigationTarget.Own<Page>(parent))) is NavigationResult<Page>.Committed, "The parent was not pushed.");
        var guarding = gates.Signal();
        var answer = gates.Answer();
        parent.Guard = async (departure, _) =>
        {
            if (departure.Kind != NavigationDepartureKind.Retain) return true;
            guarding.TrySetResult();
            return await answer.Task;
        };
        var vetoed = main.PushAsync(NavigationTarget.Own(new Page("vetoed"))).AsTask();
        await Wait(guarding.Task);
        var back = main.BackAsync().AsTask();
        // Admitted after the Back: the Back did not supersede it at its admission.
        var childStarted = gates.Signal();
        var childPush = parent.Child.PushAsync(NavigationTarget.Own<Page>(new InitPage("child")
        {
            Initialize = async (_, token) =>
            {
                childStarted.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            },
        })).AsTask();
        await Wait(childStarted.Task);
        answer.SetResult(false);
        Require(await Wait(vetoed) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard }, "The guard did not veto the push.");
        Require(await Wait(back) is NavigationResult<Page>.Committed && Names(main) == "home"
                && await Wait(childPush) is NavigationResult<Page>.Superseded,
            $"The queued Back did not supersede the child's pending push: {Names(main)}.");
    }

    // Admission inside a model turn never blocks; queued requests commit in turns of their own, in order, with hooks
    // scheduled on the model context (IRunicModelHookScheduler).
    [Test]
    public async Task QueueAdmitsInsideAModelTurnAsync()
    {
        await using var fixture = new Fixture();
        var dialog = fixture.Navigator.CreateRegion<Page>(fixture.Root, options: Queued);
        var context = (RunicModelContext)fixture.Context;
        List<string> initializedInTurn = [];
        InitPage Prompt(string name) => new(name)
        {
            Initialize = (_, _) =>
            {
                lock (initializedInTurn) initializedInTurn.Add($"{name}:{context.IsExecuting}");
                return ValueTask.CompletedTask;
            },
        };
        var requests = await Wait(context.InvokeAsync(() =>
            Enumerable.Range(0, 3).Select(index => dialog.PushForResult<int>(NavigationTarget.Own<Page>(Prompt($"p{index}")))).ToArray()));
        foreach (var request in requests)
            Require(await Wait(request.Transition) is NavigationResult<Page>.Committed, "A request admitted in a turn did not commit.");
        Require(Names(dialog) == "p0,p1,p2" && string.Join(",", initializedInTurn) == "p0:True,p1:True,p2:True",
            $"The queued requests did not commit in order with scheduled hooks: {Names(dialog)}; {string.Join(",", initializedInTurn)}.");
        for (var index = 2; index >= 0; index--)
        {
            var top = (InitPage)dialog.Current!;
            Require(await Wait(top.Entry!.CompleteAsync(index)) is NavigationResult<object>.Committed, "A return was not committed.");
            Require(await Wait(requests[index].Completion) is NavigationCompletion<int>.Completed { Value: var value } && value == index,
                $"Request {index} did not complete with its value.");
        }
        Require(dialog.Current is null, "The region did not end empty.");
    }

    // A retained result entry whose caller cancelled is not resumed by the Back of the entry above it: it retires too.
    [Test]
    public async Task BackRetiresRetainedDismissedResultEntriesAsync()
    {
        await using var fixture = new Fixture();
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        using var cancel = new CancellationTokenSource();
        var asked = new InitPage("asked");
        var request = region.PushForResult<bool>(NavigationTarget.Own<Page>(asked), cancellationToken: cancel.Token);
        Require(await Wait(request.Transition) is NavigationResult<Page>.Committed, "The result push did not commit.");
        var cover = new Page("cover");
        Require(await Wait(region.PushAsync(NavigationTarget.Own(cover))) is NavigationResult<Page>.Committed, "The cover push did not commit.");
        await cancel.CancelAsync();
        Require(await Wait(request.Completion) is NavigationCompletion<bool>.Dismissed, "Cancelling did not dismiss the request.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(Names(region) == "home,asked,cover", $"The covered entry did not stay while covered: {Names(region)}.");
        var back = await Wait(region.BackAsync());
        Require(back is NavigationResult<Page>.Committed { Current.Content: var resumed, Retired.Count: 2 } && resumed == home
                && Names(region) == "home" && home.Resumed == 1 && asked.Resumed == 0,
            $"The Back resumed the dismissed entry: {back}, {Names(region)}.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(asked.Disposed == 1 && asked.Departures.Any(departure => departure.Kind == NavigationDepartureKind.Retire),
            "The dismissed entry was not asked and retired.");

        // In a dialog region, the return of the top entry may leave the region empty past a dismissed entry; a plain Back
        // keeps the bottom entry.
        var dialog = fixture.Navigator.CreateRegion<Page>(fixture.Root, options: Queued);
        using var cancelBottom = new CancellationTokenSource();
        var bottom = dialog.PushForResult<bool>(NavigationTarget.Own<Page>(new InitPage("bottom")), cancellationToken: cancelBottom.Token);
        var topPage = new InitPage("top");
        var top = dialog.PushForResult<bool>(NavigationTarget.Own<Page>(topPage));
        Require(await Wait(top.Transition) is NavigationResult<Page>.Committed && Names(dialog) == "bottom,top", "The dialogs did not stack.");
        await cancelBottom.CancelAsync();
        Require(await Wait(bottom.Completion) is NavigationCompletion<bool>.Dismissed, "The bottom request was not dismissed.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(await Wait(topPage.Entry!.CompleteAsync(true)) is NavigationResult<object>.Committed && dialog.Current is null,
            $"The return did not retire the dismissed entry below it: {Names(dialog)}.");
    }
}
