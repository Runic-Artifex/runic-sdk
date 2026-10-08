namespace Runic.Navigation.Tests;

// W240-007: two Backs before the first commits. A second plain Back to the same destination joins the pending one:
// one pop, one guard prompt, the same result for both callers. Backs after a commit are stack semantics.
internal static partial class NavigationTests
{
    public static async Task RunBackRacesAsync()
    {
        await DoubleBackBeforeCommitPopsOnceAsync();
        await BackWhileGuardAsksJoinsAsync(confirm: true);
        await BackWhileGuardAsksJoinsAsync(confirm: false);
        await BackWithTokenSupersedesAsync();
        await BackAfterPushDoesNotJoinAsync();
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
        Require(region.IsTransitioning && region.CanGoBack, "IsTransitioning was not set when Back was admitted.");
        var second = region.BackAsync().AsTask();
        gate.SetResult();
        var firstResult = await Wait(first);
        var secondResult = await Wait(second);
        Require(firstResult is NavigationResult<Page>.Committed { Current.Content: var current } && current == a
            && SameOutcome(firstResult, secondResult),
            $"Two Backs before the commit gave {firstResult} and {secondResult}.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(Names(region) == "home,a" && a.Resumed == 1 && b.Disposed == 1, $"Two Backs before the commit left {Names(region)}.");

        // After the commit, the next Back is a new request: stack semantics, never an empty region or an exception.
        Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Committed && Names(region) == "home", "The next Back did not pop.");
        Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.NoHistory }
            && Names(region) == "home", "A Back without history did not reject.");
    }

    // The guard asks in a dialog region (a non-modal confirm). A second Back while it asks joins: the prompt stays
    // open, answering it settles both requests, and the history stays intact.
    private static async Task BackWhileGuardAsksJoinsAsync(bool confirm)
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

        var first = main.BackAsync().AsTask();
        await Until(() => dialog.Current is InitPage { Entry: not null }, "The prompt was not shown.");
        var prompt = (InitPage)dialog.Current!;
        var second = main.BackAsync().AsTask();
        await Task.Delay(50);
        Require(prompts == 1 && dialog.Current == prompt && !prompt.Entry!.Retirement.IsCancellationRequested,
            $"A second Back while the guard asks reopened the prompt ({prompts} prompts).");

        var answer = confirm ? await Wait(prompt.Entry!.CompleteAsync(true)) : await Wait(prompt.Entry!.DismissAsync());
        Require(answer is NavigationResult<object>.Committed, $"Answering the prompt gave {answer}.");
        var firstResult = await Wait(first);
        Require(SameOutcome(firstResult, await Wait(second)), "The joined Back got another result.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        if (confirm)
            Require(firstResult is NavigationResult<Page>.Committed && Names(main) == "home,a" && discards == 1 && document.Disposed == 1,
                $"A confirmed double Back gave {firstResult}, {Names(main)}, {discards} discards.");
        else
            Require(firstResult is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard } && Names(main) == "home,a,b"
                && discards == 0 && document.Disposed == 0, $"A cancelled double Back gave {firstResult}, {Names(main)}, {discards} discards.");
        Require(dialog.Current is null && prompts == 1, "The prompt did not close.");
    }

    // A Back with a cancellable token never joins and is never joined: it supersedes, as before, so cancelling
    // it can't cancel another caller's Back.
    private static async Task BackWithTokenSupersedesAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var a = new Page("a");
        await Wait(region.PushAsync(NavigationTarget.Own(a)));
        await Wait(region.PushAsync(NavigationTarget.Own(new Page("b"))));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Resume = async (_, token) => await gate.Task.WaitAsync(token);

        var first = region.BackAsync().AsTask();
        using var cancellation = new CancellationTokenSource();
        var second = region.BackAsync(cancellationToken: cancellation.Token).AsTask();
        Require(await Wait(first) is NavigationResult<Page>.Superseded, "A Back with a token joined the pending Back.");
        var third = region.BackAsync().AsTask();
        Require(await Wait(second) is NavigationResult<Page>.Superseded, "A plain Back joined a Back with a token.");
        gate.SetResult();
        Require(await Wait(third) is NavigationResult<Page>.Committed && Names(region) == "home,a", $"The last Back left {Names(region)}.");
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
            && await Wait(again) is NavigationResult<Page>.Committed && Names(region) == "home",
            $"Back, Push, Back left {Names(region)}.");
    }

    // A joined Back maps the same outcome: the same kind, entry and reason.
    private static bool SameOutcome(NavigationResult<Page> first, NavigationResult<Page> second) => (first, second) switch
    {
        (NavigationResult<Page>.Committed a, NavigationResult<Page>.Committed b) => a.Current?.Id == b.Current?.Id && a.Retired.SequenceEqual(b.Retired),
        (NavigationResult<Page>.Rejected a, NavigationResult<Page>.Rejected b) => a.Reason == b.Reason,
        _ => first.GetType() == second.GetType(),
    };
}
