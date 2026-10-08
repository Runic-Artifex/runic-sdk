namespace Runic.Navigation.Tests;

// W240-007/014: two Backs before the first commits. A second Back to the same destination joins the pending one:
// one pop, one guard prompt, the same result for both callers. Backs after a commit are stack semantics. Each join
// logs event 1074, so the tests count joins rather than infer them from equal-looking results.
internal static partial class NavigationTests
{
    private const int BackJoined = 1074;

    public static async Task RunBackRacesAsync()
    {
        await DoubleBackBeforeCommitPopsOnceAsync();
        await BackWhileGuardAsksJoinsAsync(confirm: true);
        await BackWhileGuardAsksJoinsAsync(confirm: false);
        await BackWhileGuardAsksJoinsAsync(confirm: true, withTokens: true);
        await BackWhileGuardAsksJoinsAsync(confirm: false, withTokens: true);
        await BackCallerCancellationAsync(firstWithToken: true, secondWithToken: true, cancelFirst: true);
        await BackCallerCancellationAsync(firstWithToken: true, secondWithToken: true, cancelFirst: false);
        await BackCallerCancellationAsync(firstWithToken: true, secondWithToken: false, cancelFirst: true);
        await BackCallerCancellationAsync(firstWithToken: false, secondWithToken: true, cancelFirst: false);
        await CancellingAllBackCallersClosesPromptAsync();
        await BackCancellationAfterCommitDoesNotChangeOutcomeAsync();
        await BackAfterPushDoesNotJoinAsync();
        await BackAfterChildRegionChangedDoesNotJoinAsync();
        await ChildBackAfterSupersessionDoesNotJoinAsync();
        await BackAfterReturnDoesNotJoinAsync();
    }

    private static async Task DoubleBackBeforeCommitPopsOnceAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var a = new Page("a");
        var b = new Page("b");
        await Wait(region.PushAsync(NavigationTarget.Own(a)));
        await Wait(region.PushAsync(NavigationTarget.Own(b)));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Resume = async (_, _) => await gate.Task;

        var first = region.BackAsync().AsTask();
        Require(region.IsTransitioning, "IsTransitioning was not set when Back was admitted.");
        var second = region.BackAsync().AsTask();
        gate.SetResult();
        var firstResult = await Wait(first);
        var secondResult = await Wait(second);
        Require(firstResult is NavigationResult<Page>.Committed { Current.Content: var current } && current == a
            && Joined(fixture, firstResult, secondResult),
            $"Two Backs before the commit did not share one commit to a: {firstResult}, {secondResult}, {fixture.Logs.Count(BackJoined)} joins.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(Names(region) == "home,a" && a.Resumed == 1 && b.Disposed == 1,
            $"Two Backs before the commit left {Names(region)}, {a.Resumed} resumes, {b.Disposed} disposals.");

        // After the commit, the next Back is a new request: stack semantics, never an empty region or an exception.
        Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Committed && Names(region) == "home", "The next Back did not pop.");
        Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.NoHistory }
            && Names(region) == "home", "A Back without history did not reject.");
    }

    // The guard asks in a dialog region (a non-modal confirm). A second Back while it asks joins: the prompt stays
    // open, answering it settles both requests, and the history stays intact.
    private static async Task BackWhileGuardAsksJoinsAsync(bool confirm, bool withTokens = false)
    {
        await using var fixture = new Fixture();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var dialog = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        await Wait(main.PushAsync(NavigationTarget.Own(new Page("a"))));
        var document = new Page("b");
        await Wait(main.PushAsync(NavigationTarget.Own(document)));
        var prompts = 0;
        var discards = 0;
        document.Guard = LeaveConfirmation.InDialog(dialog, () =>
        {
            Interlocked.Increment(ref prompts);
            return NavigationTarget.Own<Page>(new InitPage("confirm"));
        }, () => true, () => discards++).CanDepartAsync;

        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var first = main.BackAsync(cancellationToken: withTokens ? firstCancellation.Token : default).AsTask();
        await Until(() => dialog.Current is InitPage { Entry: not null }, "The prompt was not shown.");
        var prompt = (InitPage)dialog.Current!;
        var second = main.BackAsync(cancellationToken: withTokens ? secondCancellation.Token : default).AsTask();
        await Task.Delay(50);
        Require(prompts == 1 && dialog.Current == prompt && !prompt.Entry!.Retirement.IsCancellationRequested,
            $"A second Back while the guard asks reopened the prompt ({prompts} prompts).");

        var answer = confirm ? await Wait(prompt.Entry!.CompleteAsync(true)) : await Wait(prompt.Entry!.DismissAsync());
        Require(answer is NavigationResult<object>.Committed, $"Answering the prompt gave {answer}.");
        var firstResult = await Wait(first);
        Require(Joined(fixture, firstResult, await Wait(second)), "The second Back did not join the first.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        if (confirm)
            Require(firstResult is NavigationResult<Page>.Committed && Names(main) == "home,a" && discards == 1 && document.Disposed == 1,
                $"A confirmed double Back gave {firstResult}, {Names(main)}, {discards} discards.");
        else
            Require(firstResult is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard } && Names(main) == "home,a,b"
                && discards == 0 && document.Disposed == 0, $"A cancelled double Back gave {firstResult}, {Names(main)}, {discards} discards.");
        Require(dialog.Current is null && prompts == 1, "The prompt did not close.");
    }

    private static async Task BackCallerCancellationAsync(bool firstWithToken, bool secondWithToken, bool cancelFirst)
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var a = new Page("a");
        await Wait(region.PushAsync(NavigationTarget.Own(a)));
        var document = new Page("b");
        await Wait(region.PushAsync(NavigationTarget.Own(document)));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asks = 0;
        CancellationToken guardToken = default;
        document.Guard = async (_, token) =>
        {
            guardToken = token;
            Interlocked.Increment(ref asks);
            entered.TrySetResult();
            await gate.Task.WaitAsync(token);
            return true;
        };
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var first = region.BackAsync(cancellationToken: firstWithToken ? firstCancellation.Token : default).AsTask();
        await Wait(entered.Task);
        var second = region.BackAsync(cancellationToken: secondWithToken ? secondCancellation.Token : default).AsTask();
        (cancelFirst ? firstCancellation : secondCancellation).Cancel();
        Require(await Wait(cancelFirst ? first : second) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Cancelled }
            && !guardToken.IsCancellationRequested && !(cancelFirst ? second : first).IsCompleted,
            "Cancelling one caller cancelled the shared Back or did not settle that caller promptly.");
        // An already-cancelled request must not join or supersede the live request.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Require(await Wait(region.BackAsync(cancellationToken: cancelled.Token)) is NavigationResult<Page>.Rejected
            { Reason: NavigationRejection.Cancelled } && fixture.Logs.Count(BackJoined) == 1,
            "An already-cancelled Back disturbed the pending Back.");
        gate.SetResult();
        Require(await Wait(cancelFirst ? second : first) is NavigationResult<Page>.Committed && Names(region) == "home,a"
            && asks == 1 && document.Disposed == 1, "The remaining caller did not commit the shared Back exactly once.");
    }

    private static async Task CancellingAllBackCallersClosesPromptAsync()
    {
        await using var fixture = new Fixture();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var dialog = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        var document = new Page("document");
        await Wait(main.PushAsync(NavigationTarget.Own(document)));
        var discards = 0;
        document.Guard = LeaveConfirmation.InDialog(dialog, () => NavigationTarget.Own<Page>(new InitPage("confirm")),
            () => true, () => discards++).CanDepartAsync;
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var first = main.BackAsync(cancellationToken: firstCancellation.Token).AsTask();
        await Until(() => dialog.Current is InitPage { Entry: not null }, "The prompt was not shown.");
        var prompt = (InitPage)dialog.Current!;
        var second = main.BackAsync(cancellationToken: secondCancellation.Token).AsTask();
        firstCancellation.Cancel();
        Require(await Wait(first) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Cancelled }
            && dialog.Current == prompt && !prompt.Entry!.Retirement.IsCancellationRequested,
            "Cancelling the first Back closed the prompt for the second caller.");
        secondCancellation.Cancel();
        Require(await Wait(second) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Cancelled },
            "The last Back caller did not cancel.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(main.Current == document && dialog.Current is null && discards == 0 && document.Disposed == 0,
            "Cancelling every Back caller left a prompt open or discarded the document.");
    }

    private static async Task BackCancellationAfterCommitDoesNotChangeOutcomeAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var retiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var document = new Page("document") { OnDispose = async () => { retiring.SetResult(); await release.Task; } };
        await Wait(region.PushAsync(NavigationTarget.Own(document)));
        using var cancellation = new CancellationTokenSource();
        var back = region.BackAsync(cancellationToken: cancellation.Token).AsTask();
        await Wait(retiring.Task);
        try
        {
            cancellation.Cancel();
            Require(!back.IsCompleted && Names(region) == "home", "Cancellation changed a committed Back during retirement.");
        }
        finally { release.SetResult(); }
        Require(await Wait(back) is NavigationResult<Page>.Committed, "A committed Back became Cancelled.");
    }

    // A Push supersedes a pending Back; a Back after it supersedes the Push rather than joining anything.
    private static async Task BackAfterPushDoesNotJoinAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var a = new Page("a");
        await Wait(region.PushAsync(NavigationTarget.Own(a)));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Guard = async (_, token) =>
        {
            await gate.Task.WaitAsync(token);
            return true;
        };

        var back = region.BackAsync().AsTask();
        var push = region.PushAsync(NavigationTarget.Own(new Page("b"))).AsTask();
        var again = region.BackAsync().AsTask();
        gate.SetResult();
        Require(await Wait(back) is NavigationResult<Page>.Superseded && await Wait(push) is NavigationResult<Page>.Superseded
            && await Wait(again) is NavigationResult<Page>.Committed && Names(region) == "home" && fixture.Logs.Count(BackJoined) == 0,
            $"Back, Push, Back left {Names(region)} with {fixture.Logs.Count(BackJoined)} joins.");
    }

    // The departing page owns a child region. A child push commits while the first Back's guard asks, so the
    // first Back's plan is stale and it can't commit. A second Back must not join it: it makes its own plan and
    // commits once the user confirms, instead of both ending Superseded with nothing popped.
    private static async Task BackAfterChildRegionChangedDoesNotJoinAsync()
    {
        await using var fixture = new Fixture();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var document = new ParentPage("doc", fixture.Navigator);
        await Wait(main.PushAsync(NavigationTarget.Own<Page>(document)));
        var asks = 0;
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        document.Guard = async (_, token) =>
        {
            Interlocked.Increment(ref asks);
            await answer.Task.WaitAsync(token);
            return true;
        };

        var first = main.BackAsync().AsTask();
        await Until(() => Volatile.Read(ref asks) == 1, "The guard did not ask.");
        Require(await Wait(document.Child.PushAsync(NavigationTarget.Own(new Page("pane")))) is NavigationResult<Page>.Committed,
            "The child push did not commit while the guard asked.");
        var second = main.BackAsync().AsTask();
        answer.SetResult();
        var firstResult = await Wait(first);
        var secondResult = await Wait(second);
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(firstResult is NavigationResult<Page>.Superseded && secondResult is NavigationResult<Page>.Committed
            && Names(main) == "home" && document.Disposed == 1 && fixture.Logs.Count(BackJoined) == 0,
            $"Back after a child region changed gave {firstResult}, {secondResult}, {Names(main)}, {fixture.Logs.Count(BackJoined)} joins.");
    }

    // A parent Back supersedes a pending child Back, because it closes the child region. A second child Back
    // must not join the superseded one (it would end Superseded with it): it is admitted on its own and is
    // rejected as Closed when the parent's Back closes the region.
    private static async Task ChildBackAfterSupersessionDoesNotJoinAsync()
    {
        await using var fixture = new Fixture();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var document = new ParentPage("doc", fixture.Navigator);
        await Wait(main.PushAsync(NavigationTarget.Own<Page>(document)));
        await Wait(document.Child.PushAsync(NavigationTarget.Own(new Page("pane"))));
        var resumed = 0;
        document.ChildRoot.Resume = async (_, token) =>
        {
            Interlocked.Increment(ref resumed);
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
        };

        var childBack = document.Child.BackAsync().AsTask();
        await Until(() => Volatile.Read(ref resumed) == 1, "The child Back did not start resuming.");
        var parentBack = main.BackAsync().AsTask();
        var secondChildBack = document.Child.BackAsync().AsTask();
        var childResult = await Wait(childBack);
        var secondResult = await Wait(secondChildBack);
        var parentResult = await Wait(parentBack);
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(childResult is NavigationResult<Page>.Superseded && secondResult is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed }
            && parentResult is NavigationResult<Page>.Committed && Names(main) == "home" && fixture.Logs.Count(BackJoined) == 0,
            $"A child Back after supersession gave {childResult}, {secondResult}, parent {parentResult}, {Names(main)}, "
            + $"{fixture.Logs.Count(BackJoined)} joins.");
    }

    // A result entry's return (CompleteAsync) is a Back that carries the result. A plain Back while it is pending
    // supersedes it rather than joining it, so the plain caller never receives the return's outcome.
    private static async Task BackAfterReturnDoesNotJoinAsync()
    {
        await using var fixture = new Fixture();
        var home = new Page("home");
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var confirm = new InitPage("confirm");
        var request = main.PushForResult<bool>(NavigationTarget.Own<Page>(confirm));
        Require(await Wait(request.Transition) is NavigationResult<Page>.Committed, "The result entry was not pushed.");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        home.Resume = async (_, token) => await gate.Task.WaitAsync(token);

        var complete = confirm.Entry!.CompleteAsync(true).AsTask();
        var back = main.BackAsync().AsTask();
        gate.SetResult();
        var completeResult = await Wait(complete);
        var backResult = await Wait(back);
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(completeResult is NavigationResult<object>.Superseded && backResult is NavigationResult<Page>.Committed
            && Names(main) == "home" && fixture.Logs.Count(BackJoined) == 0,
            $"A Back during a return gave {completeResult}, {backResult}, {Names(main)}, {fixture.Logs.Count(BackJoined)} joins.");
        Require(await Wait(request.Completion) is NavigationCompletion<bool>.Dismissed, "The superseded return delivered its result.");
    }

    // Joined callers share one outcome. The join is logged once, and a commit hands both the same Retired list.
    private static bool Joined(Fixture fixture, NavigationResult<Page> first, NavigationResult<Page> second) =>
        fixture.Logs.Count(BackJoined) == 1 && (first, second) switch
        {
            (NavigationResult<Page>.Committed a, NavigationResult<Page>.Committed b) => ReferenceEquals(a.Retired, b.Retired),
            (NavigationResult<Page>.Rejected a, NavigationResult<Page>.Rejected b) => a.Reason == b.Reason && Equals(a.By, b.By),
            _ => first.GetType() == second.GetType(),
        };
}
