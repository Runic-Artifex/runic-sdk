using System.ComponentModel;
using TUnit.Core;

namespace Runic.Navigation.Tests;

// W315-020 point 4 (#350): NavigationAdmission.RejectWhileBusy, NavigationRejection.Busy, IsBusy, the region's
// WhenIdleAsync and the per-request admission override.
internal sealed partial class NavigationTests
{
    private static readonly NavigationRegionOptions RejectWhileBusy = new() { Admission = NavigationAdmission.RejectWhileBusy };

    // Busy spans the request's whole task: guards, preparation and the retirement of the departed entries, after
    // IsTransitioning turned false in the commit turn.
    [Test]
    public async Task RejectWhileBusyRejectsInEveryPhaseAsync()
    {
        await using var fixture = new Fixture();
        // Released first on every exit, so a failed assertion can't leave a hook or a disposal waiting forever.
        using var gates = new Gates();
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home), RejectWhileBusy);
        Require(region.Admission == NavigationAdmission.RejectWhileBusy && !region.IsBusy
            && ((INavigationRegion)region).Admission == NavigationAdmission.RejectWhileBusy,
            "The region did not take its admission policy.");

        // Guarding.
        var guarding = gates.Signal();
        var allow = gates.Answer();
        home.Guard = async (_, _) =>
        {
            guarding.TrySetResult();
            return await allow.Task;
        };
        var first = new Page("first");
        var push = region.PushAsync(NavigationTarget.Own(first)).AsTask();
        Require(region.IsBusy && region.IsTransitioning, "A request did not make the region busy at admission.");
        await Wait(guarding.Task);
        var refused = new Page("refused");
        var busy = await Wait(region.PushAsync(NavigationTarget.Own(refused)));
        Require(busy is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Busy, By: null } && refused.Disposed == 0,
            $"A request while a guard ran gave {busy}.");
        allow.SetResult(true);
        Require(await Wait(push) is NavigationResult<Page>.Committed && region.Current == first,
            "The first request did not commit after the refused one.");
        home.Guard = null;

        // Preparing.
        var initializing = gates.Signal();
        var initialized = gates.Signal();
        var slow = new InitPage("slow")
        {
            Initialize = async (_, _) =>
            {
                initializing.TrySetResult();
                await initialized.Task;
            },
        };
        push = region.PushAsync(NavigationTarget.Own<Page>(slow)).AsTask();
        await Wait(initializing.Task);
        busy = await Wait(region.BackAsync());
        Require(busy is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Busy }, $"A Back while initialize ran gave {busy}.");
        initialized.SetResult();
        Require(await Wait(push) is NavigationResult<Page>.Committed && region.Current == slow, "The slow push did not commit.");

        // Retirement of the departed entry: the commit decided, IsTransitioning is false, the request still runs.
        var disposing = gates.Signal();
        var disposed = gates.Signal();
        slow.OnDispose = async () =>
        {
            disposing.TrySetResult();
            await disposed.Task;
        };
        var back = region.BackAsync().AsTask();
        await Wait(disposing.Task);
        Require(region.Current == first && !region.IsTransitioning && region.IsBusy && !back.IsCompleted,
            "The region was not busy while the departed entry retired.");
        busy = await Wait(region.PushAsync(NavigationTarget.Own(new Page("late"))));
        Require(busy is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Busy }, $"A request during retirement gave {busy}.");
        disposed.SetResult();
        Require(await Wait(back) is NavigationResult<Page>.Committed && !region.IsBusy, "The Back did not complete idle.");
        Require(await Wait(region.PushAsync(NavigationTarget.Own(new Page("next")))) is NavigationResult<Page>.Committed,
            "A request after the region went idle was not admitted.");
        Require(fixture.Logs.All(1065).Count(entry => Equals(entry.State["Reason"], NavigationRejection.Busy)) == 3,
            "Busy rejections were not logged as event 1065.");
    }

    // The default policy is unchanged: a later request supersedes, and it is never Busy.
    [Test]
    public async Task SupersedeIsTheDefaultAdmissionAsync()
    {
        await using var fixture = new Fixture();
        // Released first on every exit, so a failed assertion can't leave a hook or a disposal waiting forever.
        using var gates = new Gates();
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        Require(region.Admission == NavigationAdmission.Supersede && new NavigationRegionOptions().Admission == NavigationAdmission.Supersede
            && new NavigationRequestOptions().Admission is null, "Supersede is not the default.");
        var guarding = gates.Signal();
        home.Guard = async (_, token) =>
        {
            guarding.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
            return true;
        };
        var first = region.PushAsync(NavigationTarget.Own(new Page("first"))).AsTask();
        await Wait(guarding.Task);
        home.Guard = null;
        var second = region.PushAsync(NavigationTarget.Own(new Page("second"))).AsTask();
        Require(await Wait(first) is NavigationResult<Page>.Superseded && await Wait(second) is NavigationResult<Page>.Committed
            && Names(region) == "home,second", "The default policy did not supersede.");
    }

    // A request made as soon as the awaited request completes, even inline in the continuation that completes its
    // task, never finds the region busy because of that request.
    [Test]
    public async Task NotBusyOnceTheAwaitedRequestCompletesAsync()
    {
        await using var fixture = new Fixture();
        // Released first on every exit, so a failed assertion can't leave a hook or a disposal waiting forever.
        using var gates = new Gates();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")), RejectWhileBusy);
        for (var round = 0; round < 50; round++)
        {
            var page = new Page($"p{round}");
            var push = region.PushAsync(NavigationTarget.Own(page)).AsTask();
            // Runs synchronously on the thread that sets the request's result: the earliest possible moment.
            var next = push.ContinueWith(_ => region.BackAsync().AsTask(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
            Require(await Wait(push) is NavigationResult<Page>.Committed, $"Round {round}: the push did not commit.");
            var back = await Wait(next);
            Require(back is NavigationResult<Page>.Committed, $"Round {round}: a Back from the push's continuation gave {back}.");
        }
    }

    // IsBusy is raised once true and once false, each inside a model turn, after IsTransitioning turned false.
    [Test]
    public async Task IsBusyIsRaisedInTurnsAsync()
    {
        await using var fixture = new Fixture();
        // Released first on every exit, so a failed assertion can't leave a hook or a disposal waiting forever.
        using var gates = new Gates();
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home), RejectWhileBusy);
        var raised = new List<(string Property, bool Value, bool InTurn)>();
        region.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(region.IsBusy) or nameof(region.IsTransitioning))
                lock (raised) raised.Add((args.PropertyName, args.PropertyName == nameof(region.IsBusy) ? region.IsBusy : region.IsTransitioning,
                    fixture.Context.IsExecuting));
        };
        var guarding = gates.Signal();
        var allow = gates.Answer();
        home.Guard = async (_, _) =>
        {
            guarding.TrySetResult();
            return await allow.Task;
        };
        var page = new Page("page");
        var disposing = gates.Signal();
        page.OnDispose = () => disposing.Task;
        var push = region.PushAsync(NavigationTarget.Own(page)).AsTask();
        await Wait(guarding.Task);
        await Until(() => { lock (raised) return raised.Count == 2; }, "Admission did not raise IsTransitioning and IsBusy.");
        allow.SetResult(true);
        await Wait(push);
        // The change to idle is posted, so it can trail the task: a request admitted before that turn runs keeps the
        // region busy without a false in between. Let it run.
        await Wait(fixture.Context.InvokeAsync(() => { }));
        home.Guard = null;
        var back = region.BackAsync().AsTask();
        await Until(() => page.Disposed == 1, "The Back did not start retiring the page.");
        disposing.SetResult();
        await Wait(back);
        await Wait(fixture.Context.InvokeAsync(() => { }));
        (string, bool, bool)[] snapshot;
        lock (raised) snapshot = [.. raised];
        (string, bool, bool)[] expected =
        [
            ("IsTransitioning", true, true), ("IsBusy", true, true), ("IsTransitioning", false, true), ("IsBusy", false, true),
            ("IsTransitioning", true, true), ("IsBusy", true, true), ("IsTransitioning", false, true), ("IsBusy", false, true),
        ];
        Require(snapshot.SequenceEqual(expected), $"The region raised {string.Join("; ", snapshot)}.");
    }

    // A Back that can join the pending Back joins it rather than being refused (one question, one result); any other
    // request during that Back is Busy.
    [Test]
    public async Task BackJoinsUnderRejectWhileBusyAsync()
    {
        await using var fixture = new Fixture();
        // Released first on every exit, so a failed assertion can't leave a hook or a disposal waiting forever.
        using var gates = new Gates();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")), RejectWhileBusy);
        var document = new Page("document");
        await Wait(region.PushAsync(NavigationTarget.Own(document)));
        var asked = 0;
        var guarding = gates.Signal();
        var allow = gates.Answer();
        document.Guard = async (_, _) =>
        {
            Interlocked.Increment(ref asked);
            guarding.TrySetResult();
            return await allow.Task;
        };
        var first = region.BackAsync().AsTask();
        await Wait(guarding.Task);
        var second = region.BackAsync().AsTask();
        var push = await Wait(region.PushAsync(NavigationTarget.Own(new Page("other"))));
        Require(push is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Busy }, $"A push during the Back gave {push}.");
        allow.SetResult(true);
        var results = await Wait(Task.WhenAll(first, second));
        Require(results.All(result => result is NavigationResult<Page>.Committed) && asked == 1 && Names(region) == "home"
            && fixture.Logs.Count(1074) == 1, $"The second Back did not join the first: {string.Join(", ", results.Select(r => r.ToString()))}.");
    }

    // The request's Admission overrides the region's, in both directions.
    [Test]
    public async Task RequestAdmissionOverridesTheRegionAsync()
    {
        await using var fixture = new Fixture();
        // Released first on every exit, so a failed assertion can't leave a hook or a disposal waiting forever.
        using var gates = new Gates();
        var home = new Page("home");
        var strict = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home), RejectWhileBusy);
        var guarding = gates.Signal();
        home.Guard = async (_, token) =>
        {
            guarding.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
            return true;
        };
        var first = strict.PushAsync(NavigationTarget.Own(new Page("first"))).AsTask();
        await Wait(guarding.Task);
        home.Guard = null;
        var supersede = new NavigationRequestOptions { Admission = NavigationAdmission.Supersede };
        var wins = await Wait(strict.PushAsync(NavigationTarget.Own(new Page("wins")), supersede));
        Require(wins is NavigationResult<Page>.Committed && await Wait(first) is NavigationResult<Page>.Superseded && Names(strict) == "home,wins",
            $"A Supersede request in a RejectWhileBusy region gave {wins}.");

        var other = new Page("other");
        var lenient = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Borrow(other));
        var blocking = gates.Signal();
        var allow = gates.Answer();
        other.Guard = async (_, _) =>
        {
            blocking.TrySetResult();
            return await allow.Task;
        };
        var running = lenient.PushAsync(NavigationTarget.Own(new Page("running"))).AsTask();
        await Wait(blocking.Task);
        var refused = await Wait(lenient.PushAsync(NavigationTarget.Own(new Page("refused")),
            new NavigationRequestOptions { Admission = NavigationAdmission.RejectWhileBusy }));
        Require(refused is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Busy }, $"A RejectWhileBusy request gave {refused}.");
        allow.SetResult(true);
        Require(await Wait(running) is NavigationResult<Page>.Committed, "The refused request superseded the running one.");
    }

    // A return from a result entry is not exempt: the call can be repeated.
    [Test]
    public async Task ReturnsAreRefusedWhileBusyAsync()
    {
        await using var fixture = new Fixture();
        // Released first on every exit, so a failed assertion can't leave a hook or a disposal waiting forever.
        using var gates = new Gates();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")), RejectWhileBusy);
        var dialog = new InitPage("dialog");
        var request = region.PushForResult<int>(NavigationTarget.Own<Page>(dialog));
        Require(await Wait(request.Transition) is NavigationResult<Page>.Committed, "The result entry was not pushed.");
        var guarding = gates.Signal();
        var allow = gates.Answer();
        dialog.Guard = async (departure, _) =>
        {
            if (departure.Kind == NavigationDepartureKind.Retire) return true;
            guarding.TrySetResult();
            return await allow.Task;
        };
        var push = region.PushAsync(NavigationTarget.Own(new Page("over"))).AsTask();
        await Wait(guarding.Task);
        var complete = await Wait(dialog.Entry!.CompleteAsync(42));
        Require(complete is NavigationResult<object>.Rejected { Reason: NavigationRejection.Busy } && !request.Completion.IsCompleted,
            $"CompleteAsync while the region was busy gave {complete}.");
        allow.SetResult(false);
        await Wait(push);
        Require(await Wait(dialog.Entry!.CompleteAsync(42)) is NavigationResult<object>.Committed
            && await Wait(request.Completion) is NavigationCompletion<int>.Completed { Value: 42 },
            "Repeating CompleteAsync once the region was idle did not complete the request.");
    }

    // A parent's plan still supersedes and closes a busy child region: the child's policy covers requests made to it.
    [Test]
    public async Task ParentPlanSupersedesABusyChildAsync()
    {
        await using var fixture = new Fixture();
        // Released first on every exit, so a failed assertion can't leave a hook or a disposal waiting forever.
        using var gates = new Gates();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")), RejectWhileBusy);
        var parent = new BusyParentPage(fixture.Navigator);
        await Wait(main.PushAsync(NavigationTarget.Own<Page>(parent)));
        var guarding = gates.Signal();
        parent.ChildRoot.Guard = async (departure, token) =>
        {
            if (departure.Kind == NavigationDepartureKind.Retire) return true;
            guarding.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
            return true;
        };
        var child = parent.Child.PushAsync(NavigationTarget.Own(new Page("child"))).AsTask();
        await Wait(guarding.Task);
        Require(parent.Child.IsBusy && !main.IsBusy, "Busy is not per region.");
        var back = await Wait(main.BackAsync());
        var childResult = await Wait(child);
        Require(back is NavigationResult<Page>.Committed && childResult is NavigationResult<Page>.Superseded && Names(main) == "home",
            $"The parent Back gave {back} and the busy child's request {childResult}.");
    }

    // The region's WhenIdleAsync waits for its own requests, retirement included, and not for other regions.
    [Test]
    public async Task RegionWhenIdleWaitsForItsRequestsAsync()
    {
        await using var fixture = new Fixture();
        // Released first on every exit, so a failed assertion can't leave a hook or a disposal waiting forever.
        using var gates = new Gates();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var other = new Page("other");
        var otherRegion = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Borrow(other));
        await Wait(region.WhenIdleAsync());

        var otherGuarding = gates.Signal();
        var otherAllow = gates.Answer();
        other.Guard = async (_, _) =>
        {
            otherGuarding.TrySetResult();
            return await otherAllow.Task;
        };
        var otherPush = otherRegion.PushAsync(NavigationTarget.Own(new Page("x"))).AsTask();
        await Wait(otherGuarding.Task);
        await Wait(region.WhenIdleAsync());

        var page = new Page("page");
        await Wait(region.PushAsync(NavigationTarget.Own(page)));
        var disposed = gates.Signal();
        page.OnDispose = () => disposed.Task;
        var back = region.BackAsync().AsTask();
        await Until(() => page.Disposed == 1, "The Back did not start retiring the page.");
        var idle = region.WhenIdleAsync().AsTask();
        var navigatorIdle = fixture.Navigator.WhenIdleAsync().AsTask();
        await Task.Delay(50);
        Require(!idle.IsCompleted && !navigatorIdle.IsCompleted && !back.IsCompleted, "WhenIdleAsync completed during retirement.");
        using (var cancel = new CancellationTokenSource())
        {
            var cancelled = ((INavigationRegion)region).WhenIdleAsync(cancel.Token).AsTask();
            cancel.Cancel();
            Require(await Throws<OperationCanceledException>(cancelled), "Cancelling WhenIdleAsync did not stop waiting.");
        }
        disposed.SetResult();
        await Wait(idle);
        // The region is idle before the request's result is set, so the Back may still be completing.
        Require(!region.IsBusy && !navigatorIdle.IsCompleted,
            "The region's WhenIdleAsync did not complete with its request, independent of the other region.");
        Require(await Wait(back) is NavigationResult<Page>.Committed, "The Back did not commit.");
        otherAllow.SetResult(true);
        await Wait(otherPush);
        await Wait(navigatorIdle);
    }

    private static async Task<bool> Throws<T>(Task task) where T : Exception
    {
        try
        {
            await task.WaitAsync(Timeout);
            return false;
        }
        catch (T) { return true; }
    }

    // The signals a test holds hooks and disposals on, all released when the test ends.
    private sealed class Gates : IDisposable
    {
        private readonly List<Action> _release = [];

        public TaskCompletionSource Signal()
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _release.Add(() => signal.TrySetResult());
            return signal;
        }

        public TaskCompletionSource<bool> Answer()
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _release.Add(() => answer.TrySetResult(true));
            return answer;
        }

        public void Dispose()
        {
            foreach (var release in _release) release();
        }
    }

    private sealed class BusyParentPage : Page
    {
        public BusyParentPage(RunicNavigator navigator) : base("parent")
        {
            ChildRoot = new Page("root");
            Child = navigator.CreateRegion<Page>(this, NavigationTarget.Borrow(ChildRoot), RejectWhileBusy);
        }

        public Page ChildRoot { get; }
        public NavigationRegion<Page> Child { get; }
    }
}
