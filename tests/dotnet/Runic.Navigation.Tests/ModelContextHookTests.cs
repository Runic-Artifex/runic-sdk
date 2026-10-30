using TUnit.Core;

namespace Runic.Navigation.Tests;

// #714: RunicModelContext schedules the navigator's hooks, so they run on the model turn on every host, as they do
// on a UI dispatcher (Runic.Navigation.Wpf's DispatcherModelContext).
internal sealed partial class NavigationTests
{
    // The IRunicModelHookScheduler contract on RunicModelContext: never inline, in a turn of its own under the turn's
    // SynchronizationContext, awaits resume in later turns, no turn held across an await, and the four exclusive outcomes.
    [Test]
    public async Task RunicModelContextSchedulesHooksAsync()
    {
        await using var context = new RunicModelContext();
        Require((object)context is IRunicModelHookScheduler, "RunicModelContext does not schedule navigator hooks.");
        var scheduler = (IRunicModelHookScheduler)(object)context;

        // Never inline, also when requested from a turn of the context.
        var sites = new List<(string Site, bool InTurn, bool Current, bool HasContext)>();
        void Record(string site) => sites.Add((site, context.IsExecuting, ReferenceEquals(RunicModelContext.Current, context),
            SynchronizationContext.Current is not null));
        var between = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ranInline = true;
        var hook = await context.InvokeAsync(() =>
        {
            var started = scheduler.RunHookAsync(async () =>
            {
                Record("start");
                await Task.Yield();
                Record("yield");
                // No turn is held across the hook's awaits: another turn runs while it waits.
                await between.Task;
                Record("between");
                return 5;
            }, CancellationToken.None);
            ranInline = sites.Count != 0;
            return started;
        });
        await Wait(context.InvokeAsync(between.SetResult));
        Require(await Wait(hook) == 5 && !ranInline, $"The hook ran inline in its caller: {ranInline}.");
        Require(sites.Select(site => site.Site).SequenceEqual(["start", "yield", "between"])
                && sites.All(site => site is { InTurn: true, Current: true, HasContext: true }),
            $"The hook did not run in turns of the context: {string.Join(", ", sites)}.");

        // The task completes asynchronously: its continuations never run in the hook's turn.
        var continuationInTurn = true;
        var quick = scheduler.RunHookAsync(() => Task.FromResult(1), CancellationToken.None);
        var observed = quick.ContinueWith(_ => continuationInTurn = context.IsExecuting, TaskContinuationOptions.ExecuteSynchronously);
        await Wait(observed);
        Require(!continuationInTurn, "A continuation of the hook's task ran in its turn.");

        // A synchronous throw is the hook's own outcome.
        var thrown = scheduler.RunHookAsync<int>(() => throw new FormatException(), CancellationToken.None);
        Require(await FaultsWith<FormatException>(thrown), $"A throwing hook gave {thrown.Status}.");

        // Cancelled before the operation starts: the hook never runs.
        using var hold = new ManualResetEventSlim();
        Require(context.TryPost(hold.Wait), "TryPost refused an open context.");
        using var cancellation = new CancellationTokenSource();
        var cancelledRan = false;
        var cancelled = scheduler.RunHookAsync(() =>
        {
            cancelledRan = true;
            return Task.FromResult(1);
        }, cancellation.Token);
        cancellation.Cancel();
        hold.Set();
        await Wait(context.InvokeAsync(() => { }));
        Require(await FaultsWith<OperationCanceledException>(cancelled) && cancelled.IsCanceled && !cancelledRan,
            $"A hook cancelled before it started gave {cancelled.Status}, ran: {cancelledRan}.");

        // Closed before it starts: ObjectDisposedException, and it never runs.
        hold.Reset();
        Require(context.TryPost(hold.Wait), "TryPost refused an open context.");
        var closedRan = false;
        var closed = scheduler.RunHookAsync(() =>
        {
            closedRan = true;
            return Task.FromResult(1);
        }, CancellationToken.None);
        var disposal = context.DisposeAsync().AsTask();
        hold.Set();
        await Wait(disposal);
        Require(await FaultsWith<ObjectDisposedException>(closed) && !closedRan, $"A hook pending at close gave {closed.Status}, ran: {closedRan}.");
        var late = scheduler.RunHookAsync(() => Task.FromResult(1), CancellationToken.None);
        Require(await FaultsWith<ObjectDisposedException>(late), $"A closed context accepted a hook: {late.Status}.");
    }

    // The issue's case: with the host-neutral RunicModelContext, factories, guards, initialize, resume and arrival hooks
    // and owned disposal run in turns, before and after their awaits, so a page sets its state directly in them.
    [Test]
    public async Task NavigatorHooksRunInModelTurnsAsync()
    {
        await using var fixture = new Fixture();
        var context = (RunicModelContext)fixture.Context;
        var sites = new List<(string Site, bool InTurn)>();
        void Record(string site)
        {
            var inTurn = context.IsExecuting && ReferenceEquals(RunicModelContext.Current, context);
            lock (sites) sites.Add((site, inTurn));
        }

        var home = new Page("home")
        {
            Guard = async (_, token) =>
            {
                Record("guard");
                await Task.Delay(1, token);
                Record("guard-resumed");
                return true;
            },
            Resume = async (_, token) =>
            {
                Record("resume");
                await Task.Delay(1, token);
                Record("resume-resumed");
            },
        };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        ArrivingPage? detail = null;
        var pushed = await Wait(region.PushAsync(NavigationTarget.Create<Page>(_ =>
        {
            Record("factory");
            return detail = new ArrivingPage("detail", Record)
            {
                Initialize = async (_, token) =>
                {
                    Record("initialize");
                    await Task.Delay(1, token);
                    Record("initialize-resumed");
                },
                OnDispose = () =>
                {
                    Record("dispose");
                    return Task.CompletedTask;
                },
            };
        })));
        Require(pushed is NavigationResult<Page>.Committed, $"The push gave {pushed}.");
        await Wait(detail!.Arrived.Task);
        // The arrival hook set the page's state in its turn; a later turn sees it.
        Require(await Wait(context.InvokeAsync(() => detail.Status)) == "ready", $"The arrival hook left the status {detail.Status}.");
        Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Committed, "Back did not commit.");
        await Wait(fixture.Navigator.WhenIdleAsync());

        string[] expected =
        [
            "guard", "guard-resumed", "factory", "initialize", "initialize-resumed", "arrived", "arrived-resumed",
            "resume", "resume-resumed", "dispose",
        ];
        lock (sites)
        {
            Require(expected.All(site => sites.Any(seen => seen.Site == site)),
                $"Not every hook ran: {string.Join(", ", sites)}.");
            Require(sites.All(site => site.InTurn), $"A hook ran outside a turn of the model context: {string.Join(", ", sites.Where(site => !site.InTurn))}.");
        }
    }

    // DismissAsync from a pending entry's own initialize hook cancels the hook's token before it returns, also when the
    // hook runs in a turn (W240-001 §6.2).
    [Test]
    public async Task DismissFromAScheduledInitializeCancelsAtOnceAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var page = new InitPage("pending");
        var inTurn = false;
        var tokenCancelled = false;
        page.Initialize = (entry, token) =>
        {
            inTurn = fixture.Context.IsExecuting;
            var dismissed = entry.DismissAsync(CancellationToken.None);
            tokenCancelled = dismissed.IsCompleted && token.IsCancellationRequested;
            return ValueTask.CompletedTask;
        };
        var pushed = await Wait(region.PushAsync(NavigationTarget.Own<Page>(page)));
        Require(pushed is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Cancelled } && inTurn && tokenCancelled,
            $"Dismissing from a scheduled initialize gave {pushed}, in a turn: {inTurn}, token cancelled: {tokenCancelled}.");
    }

    private static async Task<bool> FaultsWith<TException>(Task task) where TException : Exception
    {
        try
        {
            await task.WaitScaledAsync(Timeout);
            return false;
        }
        catch (TException) { return true; }
    }

    private sealed class ArrivingPage(string name, Action<string> record) : Page(name), INavigationInitialize, INavigationArrived
    {
        public Func<NavigationEntryContext, CancellationToken, ValueTask>? Initialize { get; init; }
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Status { get; private set; } = "new";

        public ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken) =>
            Initialize?.Invoke(entry, cancellationToken) ?? ValueTask.CompletedTask;

        public async ValueTask OnArrivedAsync(NavigationEntryContext entry, NavigationArrival arrival, CancellationToken cancellationToken)
        {
            record("arrived");
            Status = "loading";
            await Task.Delay(1, cancellationToken);
            Status = "ready";
            record("arrived-resumed");
            Arrived.TrySetResult();
        }
    }
}
