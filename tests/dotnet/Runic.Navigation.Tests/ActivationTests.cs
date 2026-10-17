using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core;

namespace Runic.Navigation.Tests;

// W315-020 point 1 (#350): INavigationActivated runs after the commit, the retirement of the departed entries and the
// presentation yield; the request awaits it and reports how it ended.
internal sealed partial class NavigationTests
{
    // commit → OnNavigatedTo → retirement of the departed entry → presentation yield → hook → result. The region is no
    // longer transitioning but still busy while the hook runs, and the navigator is not idle.
    [Test]
    public async Task ActivationRunsAfterRetirementAndPresentationAsync()
    {
        var journal = new Journal();
        var context = new YieldingContext(journal);
        await using var fixture = new Fixture(context);
        using var gates = new Gates();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var old = new Page("old", journal);
        await Wait(region.PushAsync(NavigationTarget.Own(old)));
        var started = gates.Signal();
        var finish = gates.Signal();
        var observed = "";
        var page = new ActivatedPage("new", journal)
        {
            Activate = async (entry, arrival, token) =>
            {
                observed = $"{region.IsTransitioning},{region.IsBusy},{entry.State},{arrival.Operation},{context.IsExecuting},"
                    + $"{RunicNavigator.HookCancellationToken == token}";
                started.TrySetResult();
                await finish.Task;
            },
        };
        var replace = region.ReplaceAsync(NavigationTarget.Own<Page>(page)).AsTask();
        await Wait(started.Task);
        var idle = fixture.Navigator.WhenIdleAsync().AsTask();
        var regionIdle = region.WhenIdleAsync().AsTask();
        await Task.Delay(30);
        Require(observed == "False,True,Active,Replace,False,True" && !replace.IsCompleted && !idle.IsCompleted && !regionIdle.IsCompleted,
            $"While the hook ran: {observed}, request completed: {replace.IsCompleted}, idle: {idle.IsCompleted}.");
        finish.SetResult();
        var result = await Wait(replace);
        Require(result is NavigationResult<Page>.Committed { Activation: NavigationActivationState.Completed, ActivationError: null }
            && result.Outcome.Activation == NavigationActivationState.Completed && journal.Text == "to:new,old,yield,activate:new", $"The activation gave {result}, journal {journal.Text}.");
        await Wait(idle);
        await Wait(regionIdle);
    }

    // A throwing hook is logged as 1076 and reported on the commit, which stands; ThrowIfFailed rethrows it.
    [Test]
    public async Task ActivationFailureIsReportedOnTheCommitAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var page = new ActivatedPage("failing") { Activate = (_, _, _) => throw new InvalidOperationException("load failed") };
        var result = await Wait(region.PushAsync(NavigationTarget.Own<Page>(page)));
        Require(result is NavigationResult<Page>.Committed { Activation: NavigationActivationState.Failed, ActivationError: InvalidOperationException { Message: "load failed" } }
            && result.IsCommitted && region.Current == page && Names(region) == "home,failing", $"A failing activation gave {result}.");
        var logged = fixture.Logs.Single(1076);
        Require(logged.Level == LogLevel.Error && logged.Exception is InvalidOperationException && Equals(logged.State["EntryType"], nameof(ActivatedPage))
            && Equals(logged.State["Operation"], "Push"), $"Event 1076 was {logged.Message}.");
        Require(Throws<InvalidOperationException>(() => result.ThrowIfFailed()), "ThrowIfFailed did not rethrow the activation error.");
        Require(result.Outcome == new NavigationOutcome(NavigationOutcomeKind.Committed) { Activation = NavigationActivationState.Failed },
            $"The outcome of a failing activation was {result.Outcome}.");
        var plain = await Wait(region.PushAsync(NavigationTarget.Own(new Page("plain"))));
        Require(plain is NavigationResult<Page>.Committed { Activation: NavigationActivationState.None } && ReferenceEquals(plain.ThrowIfFailed(), plain)
            && plain.Outcome == new NavigationOutcome(NavigationOutcomeKind.Committed), $"A commit without activation hooks gave {plain}.");
    }

    // A later commit that retains (Push) or retires (Back) the entry cancels its hook: the first request returns
    // Committed { Cancelled }. The caller's token after the commit doesn't cancel it.
    [Test]
    public async Task ActivationIsCancelledWhenTheEntryStopsBeingCurrentAsync()
    {
        await using var fixture = new Fixture();
        using var gates = new Gates();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var started = gates.Signal();
        var loading = new ActivatedPage("loading")
        {
            Activate = async (_, _, token) =>
            {
                started.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            },
        };
        using var caller = new CancellationTokenSource();
        var push = region.PushAsync(NavigationTarget.Own<Page>(loading), cancellationToken: caller.Token).AsTask();
        await Wait(started.Task);
        caller.Cancel();
        await Task.Delay(30);
        Require(!push.IsCompleted, "Cancelling the caller's token after the commit cancelled the activation.");
        var over = await Wait(region.PushAsync(NavigationTarget.Own(new Page("over"))));
        var retained = await Wait(push);
        Require(over is NavigationResult<Page>.Committed && retained is NavigationResult<Page>.Committed { Activation: NavigationActivationState.Cancelled }
            && retained.Outcome.Activation == NavigationActivationState.Cancelled, "A push that retained the activating entry did not cancel its hook.");

        // Back to it: the hook runs again (it is current again); a Back that retires it cancels it.
        started = gates.Signal();
        var back = region.BackAsync().AsTask();
        await Wait(started.Task);
        Require(loading.Activations == 2 && loading.LastArrival == new NavigationResume(region.CurrentEntry!.Id, NavigationOperation.Back),
            "Resuming the entry did not run its hook again with Back.");
        var retire = await Wait(region.BackAsync());
        Require(retire is NavigationResult<Page>.Committed && await Wait(back) is NavigationResult<Page>.Committed { Activation: NavigationActivationState.Cancelled }
            && loading.Disposed == 1, "A Back that retired the activating entry did not cancel its hook.");
    }

    // Disposal cancels a running activation and stays bounded when the hook ignores its token.
    [Test]
    public async Task DisposalCancelsActivationAsync()
    {
        var time = new CountingTimeProvider();
        var fixture = new Fixture(time: time, closeTimeout: TimeSpan.FromSeconds(10));
        using var gates = new Gates();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var started = gates.Signal();
        var stuck = gates.Signal();
        CancellationToken hookToken = default;
        var page = new ActivatedPage("stuck")
        {
            Activate = async (_, _, token) =>
            {
                hookToken = token;
                started.TrySetResult();
                await stuck.Task; // ignores its token until the test ends
            },
        };
        var push = region.PushAsync(NavigationTarget.Own<Page>(page)).AsTask();
        await Wait(started.Task);
        var timers = time.Timers;
        var disposal = fixture.Navigator.DisposeAsync().AsTask();
        await Until(() => hookToken.IsCancellationRequested, "Disposal did not cancel the activation's token.");
        await Until(() => time.Timers > timers, "Disposal did not start its close timeout.");
        time.Advance(TimeSpan.FromSeconds(11));
        await Until(() => disposal.IsCompleted || time.Timers > timers + 1, "Disposal did not move past the transition wait.");
        time.Advance(TimeSpan.FromSeconds(11));
        await Wait(disposal);
        Require(page.Disposed == 1, "Disposal did not dispose the activating page.");
        gates.Dispose();
        Require(await Wait(push) is NavigationResult<Page>.Committed { Activation: NavigationActivationState.Completed or NavigationActivationState.Cancelled },
            "The request did not complete after its hook returned.");
        await fixture.Context.DisposeAsync();
    }

    // A hook may navigate: its own region (redirect, give up), with no Reentrant and no deadlock, also in a region that
    // rejects requests while busy, and a child region its page owns.
    [Test]
    public async Task ActivationHookMayNavigateAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")), RejectWhileBusy);
        NavigationResult<object>? gaveUp = null;
        var failing = new ActivatedPage("failing") { Activate = async (entry, _, token) => gaveUp = await entry.BackAsync(token) };
        var push = await Wait(region.PushAsync(NavigationTarget.Own<Page>(failing)));
        Require(push is NavigationResult<Page>.Committed && gaveUp is NavigationResult<object>.Committed && Names(region) == "home" && failing.Disposed == 1,
            $"A hook that went back from its entry gave {gaveUp}; the push {push}.");

        NavigationResult<Page>? redirected = null;
        var outsider = new NavigationResult<Page>?[1];
        var redirecting = new ActivatedPage("redirecting")
        {
            Activate = async (_, _, token) =>
            {
                redirected = await region.ReplaceAsync(NavigationTarget.Own(new Page("target")), cancellationToken: token);
                // A request from outside the hook (without its execution context) is still refused while the region is busy.
                Task<NavigationResult<Page>> other;
                using (ExecutionContext.SuppressFlow()) other = Task.Run(() => region.PushAsync(NavigationTarget.Own(new Page("outsider"))).AsTask());
                outsider[0] = await other;
            },
        };
        var redirect = await Wait(region.PushAsync(NavigationTarget.Own<Page>(redirecting)));
        Require(redirect is NavigationResult<Page>.Committed && redirected is NavigationResult<Page>.Committed && Names(region) == "home,target"
            && outsider[0] is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Busy },
            $"A redirecting hook gave {redirected}, the outsider {outsider[0]}, the push {redirect}.");

        var parent = new ActivatedParentPage(fixture.Navigator);
        NavigationResult<Page>? childPush = null;
        parent.Activate = async (_, _, token) => childPush = await parent.Child.PushAsync(NavigationTarget.Own(new Page("child")), cancellationToken: token);
        Require(await Wait(region.PushAsync(NavigationTarget.Own<Page>(parent))) is NavigationResult<Page>.Committed
            && childPush is NavigationResult<Page>.Committed && Names(parent.Child) == "root,child",
            $"A hook's push into its page's child region gave {childPush}.");
    }

    // A ResetToRoot child root that a push makes current again is activated; ClearHistory and Clear activate nothing.
    [Test]
    public async Task ActivationOfChildRootsAndNotForClearsAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var parent = new ActivatedParentPage(fixture.Navigator, NavigationChildRetention.ResetToRoot);
        await Wait(region.PushAsync(NavigationTarget.Own<Page>(parent)));
        await Wait(parent.Child.PushAsync(NavigationTarget.Own(new Page("above"))));
        var rootActivations = parent.ChildRoot.Activations;
        var push = await Wait(region.PushAsync(NavigationTarget.Own(new Page("over"))));
        Require(push is NavigationResult<Page>.Committed { Activation: NavigationActivationState.Completed }
            && parent.ChildRoot.Activations == rootActivations + 1
            && parent.ChildRoot.LastArrival?.Operation == NavigationOperation.Push,
            $"The child root that the push made current again was not activated: {push}.");

        var activations = parent.Activations;
        await Wait(region.BackAsync());
        Require(parent.Activations == activations + 1, "Back did not activate the resumed parent.");
        var cleared = await Wait(region.ClearHistoryAsync());
        Require(cleared is NavigationResult<Page>.Committed { Activation: NavigationActivationState.None } && parent.Activations == activations + 1,
            "ClearHistory activated the current entry.");
    }

    // Typed input reaches INavigationActivated<TInput>, alone or with INavigationInitialize<TInput>; the typed hook wins
    // over the plain one for an input entry, and the plain one runs for other entries. Content that takes the input in no
    // hook fails Preparing.
    [Test]
    public async Task TypedActivationReceivesTheInputAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<object>(fixture.Root, NavigationTarget.Borrow<object>(new Page("home")));
        var only = await Wait(region.PushAsync<ActivatedInputPage, string>("note-1"));
        Require(only is NavigationResult<object>.Committed { Activation: NavigationActivationState.Completed }
            && region.Current is ActivatedInputPage { Input: "note-1", Plain: 0 } page && page.Arrival?.Operation == NavigationOperation.Push,
            $"An activation-only input page gave {only}.");

        var both = await Wait(region.ReplaceAsync(NavigationTarget.Create<InitActivatedInputPage, int>(_ => new InitActivatedInputPage(), 7)));
        Require(both is NavigationResult<object>.Committed && region.Current is InitActivatedInputPage { Initialized: 7, Activated: 7 },
            "A page with typed initialize and activation did not receive the input in both.");

        var plain = await Wait(region.PushAsync(NavigationTarget.Own<object>(new ActivatedInputPage())));
        Require(plain is NavigationResult<object>.Committed && region.Current is ActivatedInputPage { Input: null, Plain: 1 },
            "A page without input did not run its plain activation hook.");

        var markerOnly = await Wait(region.PushAsync<MarkerOnlyPage, string>("lost"));
        Require(markerOnly is NavigationResult<object>.Failed { Phase: NavigationPhase.Preparing, Error: ArgumentException },
            $"Content that takes the input in no hook gave {markerOnly}.");
    }

    // An initial target's hook starts after CreateRegion returned and is tracked: the region is busy and the navigator
    // not idle until it ends; a failure is logged.
    [Test]
    public async Task InitialTargetActivationIsTrackedAsync()
    {
        await using var fixture = new Fixture();
        using var gates = new Gates();
        var finish = gates.Signal();
        var started = gates.Signal();
        var inCaller = new AsyncLocal<bool>();
        var ranInCaller = true;
        var root = new ActivatedPage("root")
        {
            Activate = async (_, arrival, _) =>
            {
                ranInCaller = inCaller.Value;
                started.TrySetResult();
                await finish.Task;
                throw new InvalidOperationException("root load failed");
            },
        };
        inCaller.Value = true;
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow<Page>(root));
        inCaller.Value = false;
        Require(region.IsBusy && !region.IsTransitioning, "The initial target's activation did not keep the region busy.");
        await Wait(started.Task);
        var idle = fixture.Navigator.WhenIdleAsync().AsTask();
        await Task.Delay(30);
        Require(!idle.IsCompleted && !ranInCaller && root.LastArrival?.Operation == NavigationOperation.Reset,
            "The initial activation was not tracked, or ran in CreateRegion's caller.");
        finish.SetResult();
        await Wait(idle);
        await Wait(region.WhenIdleAsync());
        Require(!region.IsBusy && fixture.Logs.Count(1076) == 1, "The initial activation's failure was not logged, or the region stayed busy.");
    }

    private sealed class ActivatedPage(string name, Journal? journal = null) : Page(name, journal), INavigationActivated, INavigationAware
    {
        private readonly Journal? _journal = journal;
        private int _activations;

        public Func<NavigationEntryContext, NavigationResume, CancellationToken, Task>? Activate { get; set; }
        public int Activations => Volatile.Read(ref _activations);
        public NavigationResume? LastArrival { get; private set; }

        public async ValueTask OnActivatedAsync(NavigationEntryContext entry, NavigationResume arrival, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _activations);
            LastArrival = arrival;
            _journal?.Add($"activate:{Name}");
            if (Activate is { } activate) await activate(entry, arrival, cancellationToken);
        }

        public void OnNavigatedTo(NavigationResume arrival) => _journal?.Add($"to:{Name}");

        public void OnNavigatedFrom(NavigationExit exit)
        {
        }
    }

    private sealed class ActivatedParentPage : Page, INavigationActivated
    {
        private int _activations;

        public ActivatedParentPage(RunicNavigator navigator, NavigationChildRetention policy = NavigationChildRetention.Keep) : base("parent")
        {
            ChildRoot = new ActivatedPage("root");
            Child = navigator.CreateRegion<Page>(this, NavigationTarget.Borrow<Page>(ChildRoot), new NavigationRegionOptions(policy));
        }

        public ActivatedPage ChildRoot { get; }
        public NavigationRegion<Page> Child { get; }
        public Func<NavigationEntryContext, NavigationResume, CancellationToken, Task>? Activate { get; set; }
        public int Activations => Volatile.Read(ref _activations);

        public async ValueTask OnActivatedAsync(NavigationEntryContext entry, NavigationResume arrival, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _activations);
            if (Activate is { } activate) await activate(entry, arrival, cancellationToken);
        }
    }

    private sealed class ActivatedInputPage : INavigationActivated<string>, INavigationActivated
    {
        public string? Input { get; private set; }
        public int Plain { get; private set; }
        public NavigationResume? Arrival { get; private set; }

        public ValueTask OnActivatedAsync(NavigationEntryContext entry, NavigationResume arrival, string input, CancellationToken cancellationToken)
        {
            Input = input;
            Arrival = arrival;
            return ValueTask.CompletedTask;
        }

        public ValueTask OnActivatedAsync(NavigationEntryContext entry, NavigationResume arrival, CancellationToken cancellationToken)
        {
            Plain++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InitActivatedInputPage : INavigationInitialize<int>, INavigationActivated<int>
    {
        public int Initialized { get; private set; }
        public int Activated { get; private set; }

        public ValueTask InitializeAsync(NavigationEntryContext entry, int input, CancellationToken cancellationToken)
        {
            Initialized = input;
            return ValueTask.CompletedTask;
        }

        public ValueTask OnActivatedAsync(NavigationEntryContext entry, NavigationResume arrival, int input, CancellationToken cancellationToken)
        {
            Activated = input;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MarkerOnlyPage : INavigationInput<string>, IDisposable
    {
        public void Dispose() { }
    }

    // A RunicModelContext that also yields to its presentation, recording when.
    private sealed class YieldingContext(Journal journal) : IRunicModelContext, IRunicModelPresentationYield
    {
        private readonly RunicModelContext _inner = new();

        public bool IsExecuting => _inner.IsExecuting;

        public event Action<Exception>? UnhandledTurnException
        {
            add => _inner.UnhandledTurnException += value;
            remove => _inner.UnhandledTurnException -= value;
        }

        public bool TryPost(Action turn) => _inner.TryPost(turn);
        public ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default) => _inner.InvokeAsync(turn, cancellationToken);
        public ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default) => _inner.InvokeAsync(turn, cancellationToken);

        public async ValueTask YieldToPresentationAsync(CancellationToken cancellationToken)
        {
            await _inner.InvokeAsync(() => journal.Add("yield"), cancellationToken);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
