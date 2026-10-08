using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Runic.Navigation.Tests;

// W240-005a: hook scheduling, the await helper and closed contexts (design record W240-001 §4.4, §4.5,
// §8.1 amendment A6 and §11). SyncContextModelContext stands in for a UI dispatcher on Linux.
internal static partial class NavigationTests
{
    public static async Task RunThreadingAsync()
    {
        await ScheduledHooksRunOnTheModelThreadAsync();
        await HooksWithoutSchedulerStayOffTheModelThreadAsync(admitOnModelThread: true);
        await HooksWithoutSchedulerStayOffTheModelThreadAsync(admitOnModelThread: false);
        await DecoratorHidesTheSchedulerAsync();
        await ScheduledDisposalAsync(asyncDisposal: false);
        await ScheduledDisposalAsync(asyncDisposal: true);
        await ScheduledDisposalFallsBackToThePoolAfterCloseAsync();
        await ForgetRunsOnThePoolAsync(scheduling: true);
        await ForgetRunsOnThePoolAsync(scheduling: false);
        await ScheduledHookFailuresAsync(synchronous: true);
        await ScheduledHookFailuresAsync(synchronous: false);
        await ScheduledHookCancelledBeforeStartAsync();
        await ContextClosedBeforeHookStartsAsync();
        foreach (var hook in new[] { "factory", "initialize", "resume" })
            await ContextClosedBeforeLaterHookStartsAsync(hook);
        await ContextClosedBeforeCommitTurnAsync(scheduling: false);
        await ContextClosedBeforeCommitTurnAsync(scheduling: true);
        await InitializeWaitStartsWithDisposalAsync();
        await ScheduledHookThrowingObjectDisposedFailsAsync(guard: true);
        await ScheduledHookThrowingObjectDisposedFailsAsync(guard: false);
        await ScheduledDisposeThrowingObjectDisposedRunsOnceAsync();
        await SupersededWhileHookQueuedAsync();
        await BlockedModelThreadDisposalCompletesAsync();
        await LateFactoryContentDisposedThroughSchedulerAsync();
        await AfterUserCodeLeavesTheModelThreadAsync();
        foreach (var scheduling in new[] { false, true })
        {
            await CompletionOnModelThreadNeverNestsCommitAsync(scheduling, fromHandler: false);
            await CompletionOnModelThreadNeverNestsCommitAsync(scheduling, fromHandler: true);
            await AwaitingCallersResumeOnTheirContextAsync(scheduling);
        }
        await CommitContinuationsNeverRunInlineAsync();
        await SyncDisposeDoesNotBlockTheModelThreadAsync();
        await ScheduledScopedDisposalOrderAsync();
        await BlockedModelThreadScopedDisposalCompletesAsync();
        await ScopedFactoryCancelledBeforeStartAsync();
        await ContextClosedBeforeScopedFactoryStartsAsync();
        await NestedPumpHandlersAreNotTheHookAsync();
        await YieldStyleContinuationsAreTheHookAsync();
    }

    // Factories, guards, initialize and resume run on the model thread, each as its own operation:
    // never inline in a caller on that thread, never inside a turn. Their awaits resume there, and
    // the hook marker moves into the operation, so reentrancy is still detected.
    private static async Task ScheduledHooksRunOnTheModelThreadAsync()
    {
        using var context = new SchedulingSyncContextModelContext();
        await using var fixture = new Fixture(context);
        var probe = new Probe(context);
        var home = new ProbePage("home", probe);
        var region = fixture.Navigator.CreateRegion<ProbePage>(fixture.Root, NavigationTarget.Borrow(home));
        var commits = new ConcurrentQueue<(bool OnThread, int Depth)>();
        region.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(region.Current)) commits.Enqueue((context.IsOnThread, context.CurrentTurnDepth));
        };
        NavigationResult<ProbePage>? nested = null;
        InitProbePage? detail = null;
        var (callerOperation, push) = await Wait(context.RunOnThreadAsync(() =>
        {
            SyncContextModelContext.InCallerFrame = true;
            try
            {
                var pending = region.PushAsync(NavigationTarget.Create<ProbePage>(_ =>
                {
                    probe.Record("factory");
                    return detail = new InitProbePage("detail", probe)
                    {
                        OnInitialize = async () => nested = await region.PushAsync(NavigationTarget.Own(new ProbePage("nested", probe))),
                    };
                })).AsTask();
                return (context.CurrentOperation, pending);
            }
            finally { SyncContextModelContext.InCallerFrame = false; }
        }));
        var pushed = await Wait(push);
        Require(pushed is NavigationResult<ProbePage>.Committed && region.Current == detail, $"The scheduled push gave {pushed}.");
        Require(nested is NavigationResult<ProbePage>.Rejected { Reason: NavigationRejection.Reentrant },
            $"A push into its own region from a scheduled initialize hook gave {nested}.");
        var back = await Wait(context.RunOnThreadAsync(() => region.BackAsync().AsTask()));
        Require(back is NavigationResult<ProbePage>.Committed && region.Current == home, $"The scheduled Back gave {back}.");
        await Wait(fixture.Navigator.WhenIdleAsync().AsTask());

        string[] hooks = ["factory", "guard:home", "guard-resumed:home", "initialize:detail", "initialize-resumed:detail",
            "guard:detail", "guard-resumed:detail", "resume:home", "dispose:detail"];
        foreach (var hook in hooks)
        {
            var site = probe.Single(hook);
            Require(site is { OnModelThread: true, TurnDepth: 0, InCallerFrame: false } && site.Operation != 0 && site.Operation != callerOperation,
                $"{hook} ran at {site} (caller operation {callerOperation}).");
        }
        string[] starts = ["factory", "guard:home", "initialize:detail", "guard:detail", "resume:home", "dispose:detail"];
        Require(starts.Select(hook => probe.Single(hook).Operation).Distinct().Count() == starts.Length,
            "Two hooks shared one scheduled operation.");
        Require(detail!.LeaseHeldAtDispose && !RunicModelContextRegistry.Shared.TryGet(detail, out _),
            "The lease was not released after the scheduled disposal.");
        Require(commits.Count == 2 && commits.All(commit => commit is (true, 1)),
            $"Commits did not run in one turn on the model thread: {string.Join(", ", commits)}.");
        Require(context.HookRequests == starts.Length, $"Expected {starts.Length} scheduled operations, got {context.HookRequests}.");
    }

    // Without a scheduler, no hook and no owned disposal runs on the model thread, whether the
    // request is admitted there or on the pool, and when DisposeAsync is called there.
    private static async Task HooksWithoutSchedulerStayOffTheModelThreadAsync(bool admitOnModelThread)
    {
        using var context = new SyncContextModelContext();
        await using var fixture = new Fixture(context);
        var probe = new Probe(context);
        var home = new ProbePage("home", probe);
        var region = fixture.Navigator.CreateRegion<ProbePage>(fixture.Root, NavigationTarget.Borrow(home));
        Task<T> Admit<T>(Func<Task<T>> request) => admitOnModelThread ? context.RunOnThreadAsync(request) : Task.Run(request);

        var detail = new InitProbePage("detail", probe);
        Require(await Wait(Admit(() => region.PushAsync(NavigationTarget.Create<ProbePage>(_ =>
        {
            probe.Record("factory");
            return detail;
        })).AsTask())) is NavigationResult<ProbePage>.Committed, "The push did not commit.");
        Require(await Wait(Admit(() => region.BackAsync().AsTask())) is NavigationResult<ProbePage>.Committed, "Back did not commit.");
        var last = new ProbePage("last", probe);
        Require(await Wait(Admit(() => region.PushAsync(NavigationTarget.Own(last)).AsTask())) is NavigationResult<ProbePage>.Committed,
            "The second push did not commit.");
        await Wait(context.RunOnThreadAsync(() => fixture.Navigator.DisposeAsync().AsTask()).Unwrap());

        Require(last.Disposed == 1 && detail.Disposed == 1, "Owned content was not disposed.");
        Require(probe.Sites.Count >= 9 && probe.Sites.All(site => !site.OnModelThread),
            $"A hook ran on the model thread without a scheduler: {string.Join(", ", probe.Sites.Where(site => site.OnModelThread))}.");
    }

    // A decorator that doesn't forward IRunicModelHookScheduler hides it: hooks run on the pool.
    private static async Task DecoratorHidesTheSchedulerAsync()
    {
        using var context = new SchedulingSyncContextModelContext();
        await using var fixture = new Fixture(new ForwardingContext(context));
        var probe = new Probe(context);
        var region = fixture.Navigator.CreateRegion<ProbePage>(fixture.Root, NavigationTarget.Borrow(new ProbePage("home", probe)));
        var detail = new InitProbePage("detail", probe);
        await Wait(context.RunOnThreadAsync(() => region.PushAsync(NavigationTarget.Own<ProbePage>(detail)).AsTask()));
        await Wait(region.BackAsync().AsTask());
        await Wait(fixture.Navigator.WhenIdleAsync().AsTask());
        Require(detail.Disposed == 1 && probe.Sites.All(site => !site.OnModelThread) && context.HookRequests == 0,
            "A decorator that doesn't forward the scheduler still scheduled hooks.");
    }

    // A9: owned Dispose/DisposeAsync runs on the model thread as its own operation, outside turns,
    // before the lease is released: on retirement after a commit and on navigator disposal.
    private static async Task ScheduledDisposalAsync(bool asyncDisposal)
    {
        using var context = new SchedulingSyncContextModelContext();
        await using var fixture = new Fixture(context);
        var probe = new Probe(context);
        var region = fixture.Navigator.CreateRegion<ProbePage>(fixture.Root, NavigationTarget.Borrow(new ProbePage("home", probe)));
        ProbePage Page(string name) => asyncDisposal ? new AsyncProbePage(name, probe) : new ProbePage(name, probe);
        var first = Page("first");
        var second = Page("second");
        await Wait(region.PushAsync(NavigationTarget.Own(first)).AsTask());
        await Wait(region.BackAsync().AsTask());
        await Wait(region.PushAsync(NavigationTarget.Own(second)).AsTask());
        await Wait(fixture.Navigator.DisposeAsync().AsTask());

        foreach (var page in new[] { first, second })
        {
            var site = probe.Single($"dispose:{page.Name}");
            Require(page.Disposed == 1 && page.LeaseHeldAtDispose && site is { OnModelThread: true, TurnDepth: 0 } && site.Operation != 0,
                $"{page.Name} was disposed at {site} (lease held: {page.LeaseHeldAtDispose}).");
            Require(!RunicModelContextRegistry.Shared.TryGet(page, out _), $"The lease of {page.Name} was not released.");
            if (asyncDisposal)
                Require(probe.Single($"dispose-resumed:{page.Name}").OnModelThread, "An asynchronous disposal did not resume on the model thread.");
        }
        Require(fixture.Navigator.UnretiredEntryCount == 0, "Entries were left after disposal.");
    }

    // A9: once the context is closed, owned disposal falls back to the thread pool, so cleanup still runs.
    private static async Task ScheduledDisposalFallsBackToThePoolAfterCloseAsync()
    {
        using var context = new SchedulingSyncContextModelContext();
        await using var fixture = new Fixture(context, closeTimeout: TimeSpan.FromMinutes(1));
        var probe = new Probe(context);
        var region = fixture.Navigator.CreateRegion<ProbePage>(fixture.Root, NavigationTarget.Borrow(new ProbePage("home", probe)));
        var owned = new AsyncProbePage("owned", probe);
        await Wait(region.PushAsync(NavigationTarget.Own<ProbePage>(owned)).AsTask());
        await context.DisposeAsync();
        var watch = Stopwatch.StartNew();
        await Wait(fixture.Navigator.DisposeAsync().AsTask());
        var site = probe.Single("dispose:owned");
        Require(owned.Disposed == 1 && owned.LeaseHeldAtDispose && !site.OnModelThread,
            $"After the context closed, owned content was disposed at {site}.");
        Require(watch.Elapsed < TimeSpan.FromSeconds(10) && fixture.Navigator.UnretiredEntryCount == 0 && fixture.Logs.Count(1068) == 0,
            $"Disposal with a closed context took {watch.Elapsed} and logged {fixture.Logs.Count(1068)} close timeouts.");
    }

    private static async Task ForgetRunsOnThePoolAsync(bool scheduling)
    {
        using var context = scheduling ? new SchedulingSyncContextModelContext() : new SyncContextModelContext();
        await using var fixture = new Fixture(context);
        var presentation = new ThreadPresentation(context);
        using var attachment = fixture.Navigator.AttachPresentation(presentation);
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        await Wait(context.RunOnThreadAsync(() => region.PushAsync(NavigationTarget.Own(new Page("owned"))).AsTask()));
        await Wait(context.RunOnThreadAsync(() => region.BackAsync().AsTask()));
        await Wait(fixture.Navigator.WhenIdleAsync().AsTask());
        Require(presentation.Calls.Count == 1 && presentation.Calls.All(onThread => !onThread),
            $"Forget ran {presentation.Calls.Count} times, on the model thread: {string.Join(",", presentation.Calls)}.");
    }

    // A scheduled guard that throws synchronously, or whose task faults, fails the transition with that exception.
    private static async Task ScheduledHookFailuresAsync(bool synchronous)
    {
        using var context = new SchedulingSyncContextModelContext();
        await using var fixture = new Fixture(context);
        var home = new Page("home")
        {
            Guard = synchronous
                ? (_, _) => throw new InvalidOperationException("guard")
                : async (_, _) =>
                {
                    await Task.Yield();
                    throw new InvalidOperationException("guard");
                },
        };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var result = await Wait(region.PushAsync(NavigationTarget.Own(new Page("next"))).AsTask());
        Require(result is NavigationResult<Page>.Failed { Phase: NavigationPhase.Guarding, Error: InvalidOperationException { Message: "guard" } }
            && fixture.Logs.Single(1060).Exception is InvalidOperationException,
            $"A {(synchronous ? "synchronously throwing" : "faulted")} scheduled guard gave {result}.");
    }

    // An operation cancelled before it starts gives the transition's cancellation outcome; the hook never runs.
    private static async Task ScheduledHookCancelledBeforeStartAsync()
    {
        using var context = new SchedulingSyncContextModelContext();
        await using var fixture = new Fixture(context);
        var guardRuns = 0;
        var home = new Page("home") { Guard = (_, _) => { Interlocked.Increment(ref guardRuns); return ValueTask.FromResult(true); } };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        using var gate = new ManualResetEventSlim();
        context.Post(() => gate.Wait(Timeout));
        using var cancellation = new CancellationTokenSource();
        var next = new Page("next");
        var push = region.PushAsync(NavigationTarget.Own(next), cancellationToken: cancellation.Token).AsTask();
        await Until(() => context.HookRequests == 1, "The guard was not scheduled.");
        cancellation.Cancel();
        gate.Set();
        var result = await Wait(push);
        await Wait(fixture.Navigator.WhenIdleAsync().AsTask());
        Require(result is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Cancelled } && guardRuns == 0 && next.Disposed == 1,
            $"A guard cancelled before it started gave {result} and ran {guardRuns} times.");
    }

    // A6: a context that closes before a scheduled hook starts gives Rejected(Closed), logs no failure,
    // and starts closing the navigator, whose disposal then doesn't wait for the close timeout.
    private static async Task ContextClosedBeforeHookStartsAsync()
    {
        using var context = new SchedulingSyncContextModelContext();
        await using var fixture = new Fixture(context, closeTimeout: TimeSpan.FromMinutes(1));
        var guardRuns = 0;
        var home = new Page("home") { Guard = (_, _) => { Interlocked.Increment(ref guardRuns); return ValueTask.FromResult(true); } };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        using var gate = new ManualResetEventSlim();
        context.Post(() => gate.Wait(Timeout));
        var next = new Page("next");
        var push = region.PushAsync(NavigationTarget.Own(next)).AsTask();
        await Until(() => context.HookRequests == 1, "The guard was not scheduled.");
        await context.DisposeAsync();
        gate.Set();
        var result = await Wait(push);
        Require(result is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed } && guardRuns == 0,
            $"A context closed before the guard started gave {result}; the guard ran {guardRuns} times.");
        Require(fixture.Logs.Count(1060) == 0 && fixture.Logs.Count(1062) == 0, "A closed context was logged as a failure.");
        var late = await Wait(region.BackAsync().AsTask());
        Require(late is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed }, $"A request after the close gave {late}.");
        var watch = Stopwatch.StartNew();
        await Wait(fixture.Navigator.DisposeAsync().AsTask());
        Require(watch.Elapsed < TimeSpan.FromSeconds(10) && next.Disposed == 1 && fixture.Navigator.UnretiredEntryCount == 0,
            $"Disposal after the context closed took {watch.Elapsed}.");
    }

    // A6 for the later hooks of a transition: the guard passed, then the context closes while the
    // factory, initialize or resume operation is queued. The hook never runs.
    private static async Task ContextClosedBeforeLaterHookStartsAsync(string hook)
    {
        using var context = new SchedulingSyncContextModelContext();
        await using var fixture = new Fixture(context, closeTimeout: TimeSpan.FromMinutes(1));
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        if (hook == "resume")
            Require(await Wait(region.PushAsync(NavigationTarget.Own(new Page("detail")))) is NavigationResult<Page>.Committed, "The setup push did not commit.");
        using var gate = new ManualResetEventSlim();
        // The departing guard blocks the model's thread right after it returns, so the next hook queues behind it.
        region.Current!.Guard = (_, _) =>
        {
            context.Post(() => gate.Wait(Timeout));
            return ValueTask.FromResult(true);
        };
        var factoryRuns = 0;
        var init = new InitPage("init");
        var before = context.HookRequests;
        var request = hook switch
        {
            "factory" => region.PushAsync(NavigationTarget.Create<Page>(_ =>
            {
                Interlocked.Increment(ref factoryRuns);
                return new Page("created");
            })).AsTask(),
            "initialize" => region.PushAsync(NavigationTarget.Own<Page>(init)).AsTask(),
            _ => region.BackAsync().AsTask(),
        };
        await Until(() => context.HookRequests == before + 2, $"The {hook} was not scheduled.");
        await context.DisposeAsync();
        gate.Set();
        var result = await Wait(request);
        await Wait(fixture.Navigator.WhenIdleAsync().AsTask());
        var ran = hook switch { "factory" => factoryRuns, "initialize" => init.Initialized, _ => home.Resumed };
        Require(result is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed } && ran == 0
            && fixture.Logs.Count(1061) == 0 && fixture.Navigator.IsClosed,
            $"A context closed before the {hook} started gave {result}; the {hook} ran {ran} times.");
        if (hook == "initialize") Require(init.Disposed == 1, $"The uninitialized content was disposed {init.Disposed} times.");
    }

    // A6: the commit turn can't start because the context closed, with or without a scheduler.
    private static async Task ContextClosedBeforeCommitTurnAsync(bool scheduling)
    {
        using var context = scheduling ? new SchedulingSyncContextModelContext() : new SyncContextModelContext();
        await using var fixture = new Fixture(context);
        using var gate = new ManualResetEventSlim();
        // The guard blocks the model's thread once it returned, so the commit turn queues behind it.
        var home = new Page("home")
        {
            Guard = (_, _) =>
            {
                context.Post(() => gate.Wait(Timeout));
                return ValueTask.FromResult(true);
            },
        };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var next = new Page("next");
        var push = region.PushAsync(NavigationTarget.Own(next)).AsTask();
        await Until(() => context.InvokeRequests == 1, "The commit turn was not requested.");
        await context.DisposeAsync();
        gate.Set();
        var result = await Wait(push);
        await Wait(fixture.Navigator.WhenIdleAsync().AsTask());
        Require(result is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed } && fixture.Logs.Count(1062) == 0
            && region.Current?.Name == "home" && next.Disposed == 1,
            $"A context closed before the commit turn gave {result} and logged {fixture.Logs.Count(1062)} commit failures.");
    }

    // A model context that closed (A6) long before DisposeAsync doesn't shorten disposal's wait for a
    // running initialize hook: retirement still waits for it, and no initialize timeout is logged.
    // Today disposal's transition wait covers the hook, so InitializeWait isn't reached here; the
    // test pins the behavior against a change that starts disposal's waits when the context closes.
    private static async Task InitializeWaitStartsWithDisposalAsync()
    {
        var time = new FakeTimeProvider();
        using var context = new SyncContextModelContext();
        await using var fixture = new Fixture(context, time, TimeSpan.FromSeconds(10));
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var other = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Borrow(new Page("other")));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initDone = 0;
        var disposedDuringInit = 0;
        var page = new InitPage("slow")
        {
            Initialize = async (_, _) =>
            {
                started.TrySetResult();
                await gate.Task;
                Volatile.Write(ref initDone, 1);
            },
            OnDispose = () =>
            {
                if (Volatile.Read(ref initDone) == 0) Interlocked.Increment(ref disposedDuringInit);
                return Task.CompletedTask;
            },
        };
        var push = region.PushAsync(NavigationTarget.Own<Page>(page)).AsTask();
        await Wait(started.Task);

        // The context closes; the next turn finds it closed, so the navigator starts closing (A6).
        await context.DisposeAsync();
        var closed = await Wait(other.PushAsync(NavigationTarget.Own(new Page("late"))).AsTask());
        Require(closed is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed } && fixture.Navigator.IsClosed,
            $"A push on the closed context gave {closed}.");

        // Long past the close timeout, disposal starts its own deadline.
        time.Advance(TimeSpan.FromSeconds(30));
        var disposal = fixture.Navigator.DisposeAsync().AsTask();
        await Task.Delay(100);
        Require(page.Disposed == 0 && !disposal.IsCompleted, "Disposal disposed content under a running initialize hook.");
        gate.SetResult();
        await Wait(disposal);
        await Wait(push);
        Require(page.Disposed == 1 && disposedDuringInit == 0 && fixture.Logs.Count(1069) == 0,
            $"Content was disposed {page.Disposed} times ({disposedDuringInit} under the hook); {fixture.Logs.Count(1069)} initialize timeouts were logged.");
    }

    // A scheduled guard or initialize that throws ObjectDisposedException itself fails the transition
    // like any other exception: the operation started, so the context is not taken as closed.
    private static async Task ScheduledHookThrowingObjectDisposedFailsAsync(bool guard)
    {
        using var context = new SchedulingSyncContextModelContext();
        await using var fixture = new Fixture(context);
        var home = new Page("home");
        if (guard) home.Guard = (_, _) => throw new ObjectDisposedException("guard");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var next = new InitPage("next");
        if (!guard) next.Initialize = (_, _) => throw new ObjectDisposedException("initialize");
        var result = await Wait(region.PushAsync(NavigationTarget.Own<Page>(next)));
        var (phase, eventId) = guard ? (NavigationPhase.Guarding, 1060) : (NavigationPhase.Preparing, 1061);
        Require(result is NavigationResult<Page>.Failed { Error: ObjectDisposedException } failed && failed.Phase == phase
            && fixture.Logs.Single(eventId).Exception is ObjectDisposedException,
            $"A scheduled {(guard ? "guard" : "initialize")} that threw ObjectDisposedException gave {result}.");
        Require(!fixture.Navigator.IsClosed, "The navigator started closing after a hook threw ObjectDisposedException.");
    }

    // With a scheduler, an owned Dispose that throws ObjectDisposedException ran: it is logged (1064)
    // and not retried on the thread pool.
    private static async Task ScheduledDisposeThrowingObjectDisposedRunsOnceAsync()
    {
        using var context = new SchedulingSyncContextModelContext();
        await using var fixture = new Fixture(context);
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var owned = new Page("owned") { OnDispose = () => throw new ObjectDisposedException("content") };
        await Wait(region.PushAsync(NavigationTarget.Own(owned)));
        await Wait(region.BackAsync());
        await Wait(fixture.Navigator.WhenIdleAsync().AsTask());
        await Wait(fixture.Navigator.DisposeAsync().AsTask());
        Require(owned.Disposed == 1 && fixture.Logs.Single(1064).Exception is ObjectDisposedException && !context.Closed,
            $"An owned Dispose that threw ObjectDisposedException ran {owned.Disposed} times and logged {fixture.Logs.Count(1064)} failures.");
    }

    // A request superseded while its guard operation is still queued ends Superseded; the queued
    // guard never runs, and the next request's guard runs once.
    private static async Task SupersededWhileHookQueuedAsync()
    {
        using var context = new SchedulingSyncContextModelContext();
        await using var fixture = new Fixture(context);
        var guardRuns = 0;
        var home = new Page("home") { Guard = (_, _) => { Interlocked.Increment(ref guardRuns); return ValueTask.FromResult(true); } };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        using var gate = new ManualResetEventSlim();
        context.Post(() => gate.Wait(Timeout));
        var first = new Page("first");
        var second = new Page("second");
        var superseded = region.PushAsync(NavigationTarget.Own(first)).AsTask();
        await Until(() => context.HookRequests == 1, "The first guard was not scheduled.");
        var winner = region.PushAsync(NavigationTarget.Own(second)).AsTask();
        gate.Set();
        var (lost, won) = (await Wait(superseded), await Wait(winner));
        await Wait(fixture.Navigator.WhenIdleAsync().AsTask());
        Require(lost is NavigationResult<Page>.Superseded && won is NavigationResult<Page>.Committed && region.Current == second
            && guardRuns == 1 && first.Disposed == 1 && second.Disposed == 0,
            $"Superseding a queued guard gave {lost} and {won}; the guard ran {guardRuns} times.");
    }

    // A host that blocks the model's thread on DisposeAsync (WPF OnExit calling GetResult) still gets
    // a completed disposal, bounded by the close timeout: owned disposal that can't start on the model's
    // thread runs on the pool instead, once. The abandoned operation does nothing when the thread frees.
    private static async Task BlockedModelThreadDisposalCompletesAsync()
    {
        using var context = new SchedulingSyncContextModelContext();
        await using var fixture = new Fixture(context, closeTimeout: TimeSpan.FromMilliseconds(300));
        var probe = new Probe(context);
        var region = fixture.Navigator.CreateRegion<ProbePage>(fixture.Root, NavigationTarget.Borrow(new ProbePage("home", probe)));
        var owned = new ProbePage("owned", probe);
        Require(await Wait(region.PushAsync(NavigationTarget.Own(owned))) is NavigationResult<ProbePage>.Committed, "The push did not commit.");
        var (completed, elapsed) = await context.RunOnThreadAsync(() =>
        {
            var watch = Stopwatch.StartNew();
            // Like GetAwaiter().GetResult(), but a hang fails the test instead of blocking it forever.
            var completed = fixture.Navigator.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            return (completed, watch.Elapsed);
        }).WaitAsync(Timeout);
        // The queued clearing turn and the abandoned disposal operation run now.
        await Wait(context.RunOnThreadAsync(() => true));
        await Wait(fixture.Navigator.DisposeAsync().AsTask());
        var site = probe.Single("dispose:owned");
        Require(completed && owned.Disposed == 1 && !site.OnModelThread && fixture.Navigator.UnretiredEntryCount == 0,
            $"Disposal with a blocked model thread took {elapsed}; owned content was disposed {owned.Disposed} times, at {site}.");
    }

    // Content that a factory returns after disposal already retired its pending entry is disposed
    // through the scheduler on the model's thread: disposal is over, so nothing bounds the wait.
    private static async Task LateFactoryContentDisposedThroughSchedulerAsync()
    {
        using var context = new SchedulingSyncContextModelContext();
        await using var fixture = new Fixture(context, closeTimeout: TimeSpan.FromMilliseconds(200));
        var probe = new Probe(context);
        var region = fixture.Navigator.CreateRegion<ProbePage>(fixture.Root, NavigationTarget.Borrow(new ProbePage("home", probe)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var hold = new ManualResetEventSlim();
        ProbePage? late = null;
        var push = region.PushAsync(NavigationTarget.Create<ProbePage>(_ =>
        {
            entered.TrySetResult();
            release.Wait(Timeout);
            // Keeps the model's thread busy until the disposal operation is queued, so the navigator
            // decides how long to wait for it while it hasn't started.
            context.Post(() => hold.Wait(Timeout));
            return late = new ProbePage("late", probe);
        })).AsTask();
        await Wait(entered.Task);
        var requests = context.HookRequests;
        await Wait(fixture.Navigator.DisposeAsync().AsTask());
        release.Set();
        await Until(() => context.HookRequests > requests, "The late content's disposal was not scheduled.");
        await Task.Delay(100);
        hold.Set();
        var result = await Wait(push);
        await Until(() => late is { Disposed: 1 }, "The late factory content was not disposed.");
        var site = probe.Single("dispose:late");
        Require(result is NavigationResult<ProbePage>.Rejected { Reason: NavigationRejection.Closed }
            && site is { OnModelThread: true, TurnDepth: 0 } && site.Operation != 0,
            $"Late factory content gave {result} and was disposed at {site}.");
    }

    // The helper continues on the thread pool when the awaited task completes on the model thread, also on failure.
    private static async Task AfterUserCodeLeavesTheModelThreadAsync()
    {
        using var context = new SyncContextModelContext();
        await using var fixture = new Fixture(context);

        static async Task<(int Value, bool OnThread)> Observe(ConfiguredValueTaskAwaitable<int> after, SyncContextModelContext context)
        {
            var value = await after;
            return (value, context.IsOnThread);
        }

        var completed = new TaskCompletionSource<int>();
        var observed = Observe(fixture.Navigator.AfterUserCode(completed.Task), context);
        await context.InvokeAsync(() => Inline(() => completed.SetResult(7)));
        Require(await Wait(observed) is (7, false), "The helper continued on the model thread after a result.");

        var faulted = new TaskCompletionSource<int>();
        var failing = Observe(fixture.Navigator.AfterUserCode(faulted.Task), context);
        var onThread = (Task<bool>)failing.ContinueWith(_ => context.IsOnThread, TaskContinuationOptions.ExecuteSynchronously);
        await context.InvokeAsync(() => Inline(() => faulted.SetException(new InvalidOperationException("hook"))));
        Require(!await Wait(onThread) && failing.Exception?.InnerException is InvalidOperationException { Message: "hook" },
            "The helper continued on the model thread after a failure, or changed the exception.");
    }

    // H2: a guard completed on the model thread (inside a turn, or inside another commit's
    // PropertyChanged handler) never lets the engine run its commit nested in that frame.
    private static async Task CompletionOnModelThreadNeverNestsCommitAsync(bool scheduling, bool fromHandler)
    {
        using var context = scheduling ? new SchedulingSyncContextModelContext() : new SyncContextModelContext();
        await using var fixture = new Fixture(context);
        // Continuations of this source run inline in the completing frame.
        var release = new TaskCompletionSource<bool>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var home = new Page("home")
        {
            Guard = (_, _) =>
            {
                entered.TrySetResult();
                return new ValueTask<bool>(release.Task);
            },
        };
        var region = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Borrow(home));
        var depths = new ConcurrentQueue<int>();
        region.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(region.Current)) depths.Enqueue(context.CurrentTurnDepth);
        };
        var push = region.PushAsync(NavigationTarget.Own(new Page("next"))).AsTask();
        await Wait(entered.Task);
        if (fromHandler)
        {
            var other = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Borrow(new Page("other")));
            other.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(other.Current)) Inline(() => release.TrySetResult(true));
            };
            Require(await Wait(other.PushAsync(NavigationTarget.Own(new Page("trigger"))).AsTask()) is NavigationResult<Page>.Committed,
                "The other region's push did not commit.");
        }
        else await Wait(context.InvokeAsync(() => Inline(() => release.SetResult(true))));
        var result = await Wait(push);
        Require(result is NavigationResult<Page>.Committed && depths.ToArray() is [1] && context.MaxTurnDepth == 1,
            $"A guard completed on the model thread ({(scheduling ? "scheduled" : "pool")}, {(fromHandler ? "handler" : "turn")}) " +
            $"gave {result} with commit depths [{string.Join(",", depths)}] and max depth {context.MaxTurnDepth}.");
    }

    private static async Task AwaitingCallersResumeOnTheirContextAsync(bool scheduling)
    {
        using var context = scheduling ? new SchedulingSyncContextModelContext() : new SyncContextModelContext();
        await using var fixture = new Fixture(context);
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var resumed = await Wait(context.RunOnThreadAsync(async () =>
        {
            await region.PushAsync(NavigationTarget.Own(new Page("next")));
            var afterPush = context.IsOnThread;
            await region.BackAsync();
            return afterPush && context.IsOnThread;
        }));
        Require(resumed, "A caller awaiting navigation on the model thread did not resume there.");
    }

    // A context whose turn results complete inline: nothing after the commit runs on the model thread.
    private static async Task CommitContinuationsNeverRunInlineAsync()
    {
        using var context = new SyncContextModelContext(inlineCompletions: true);
        await using var fixture = new Fixture(context);
        var probe = new Probe(context);
        var region = fixture.Navigator.CreateRegion<ProbePage>(fixture.Root, NavigationTarget.Borrow(new ProbePage("home", probe)));
        var owned = new AsyncProbePage("owned", probe);
        await Wait(region.PushAsync(NavigationTarget.Own<ProbePage>(owned)).AsTask());
        await Wait(region.BackAsync().AsTask());
        await Wait(fixture.Navigator.WhenIdleAsync().AsTask());
        Require(owned.Disposed == 1 && probe.Sites.All(site => !site.OnModelThread) && context.MaxTurnDepth == 1,
            $"Work after an inline-completed commit ran on the model thread: {string.Join(", ", probe.Sites.Where(site => site.OnModelThread))}.");
    }

    // M2: Dispose() on the model thread returns at once; scheduled disposal then runs there.
    private static async Task SyncDisposeDoesNotBlockTheModelThreadAsync()
    {
        using var context = new SchedulingSyncContextModelContext();
        await using var fixture = new Fixture(context);
        var probe = new Probe(context);
        var region = fixture.Navigator.CreateRegion<ProbePage>(fixture.Root, NavigationTarget.Borrow(new ProbePage("home", probe)));
        var owned = new ProbePage("owned", probe);
        await Wait(region.PushAsync(NavigationTarget.Own(owned)).AsTask());
        var (elapsed, disposedAtReturn) = await Wait(context.RunOnThreadAsync(() =>
        {
            var watch = Stopwatch.StartNew();
            fixture.Navigator.Dispose();
            return (watch.Elapsed, owned.Disposed);
        }));
        Require(elapsed < TimeSpan.FromSeconds(1) && disposedAtReturn == 0, $"Dispose() blocked for {elapsed}.");
        await Until(() => owned.Disposed == 1 && fixture.Navigator.UnretiredEntryCount == 0, "Dispose() did not finish the disposal.");
        Require(probe.Single("dispose:owned").OnModelThread, "Owned content was not disposed on the model thread.");
        fixture.Navigator.Dispose();
        await Wait(fixture.Navigator.DisposeAsync().AsTask());
        Require(owned.Disposed == 1 && region.Current is null, "Repeated disposal disposed content twice.");
    }

    // A guard that pumps messages, like one showing a modal dialog, dispatches input handlers nested
    // in its frame and on its ExecutionContext. Those handlers are not the hook's code: their requests
    // are admitted. The hook's own code, before and after the pump, after an await on the model
    // thread and on the pool, is still rejected as Reentrant.
    private static async Task NestedPumpHandlersAreNotTheHookAsync()
    {
        using var context = new SchedulingSyncContextModelContext(freshContexts: true);
        await using var fixture = new Fixture(context);
        var probe = new Probe(context);
        NavigationRegion<ProbePage>? region = null;
        Task<NavigationResult<ProbePage>>? fromHandler = null;
        var own = new List<Task<NavigationResult<ProbePage>>>();
        Task<NavigationResult<ProbePage>> Push(string name) => region!.PushAsync(NavigationTarget.Own(new ProbePage(name, probe))).AsTask();
        var guards = 0;
        var home = new PumpingGuardPage(probe, async () =>
        {
            // The handler's push departs home too; only the first departure pumps.
            if (Interlocked.Increment(ref guards) > 1) return true;
            own.Add(Push("before-pump"));
            // A posted window message, dispatched by the nested pump below.
            context.Post(() => fromHandler = Push("from-handler"));
            context.PumpUntil(() => fromHandler is not null);
            own.Add(Push("after-pump"));
            await Task.Yield();
            Require(context.IsOnThread, "The guard did not resume on the model thread.");
            own.Add(Push("after-await"));
            await Task.Delay(1).ConfigureAwait(false);
            own.Add(Push("on-pool"));
            return true;
        });
        region = fixture.Navigator.CreateRegion<ProbePage>(fixture.Root, NavigationTarget.Borrow(home));
        var detail = await Wait(context.RunOnThreadAsync(() => Push("detail")));
        Require(fromHandler is not null, "The nested handler did not run.");
        var handled = await Wait(fromHandler!);
        Require(handled is not NavigationResult<ProbePage>.Rejected { Reason: NavigationRejection.Reentrant },
            $"A request from an input handler dispatched by a hook's nested pump gave {handled}.");
        foreach (var task in own)
        {
            var result = await Wait(task);
            Require(result is NavigationResult<ProbePage>.Rejected { Reason: NavigationRejection.Reentrant },
                $"A request from the guard's own code gave {result}.");
        }
        Require(own.Count == 4, $"The guard made {own.Count} requests.");
        Require(detail is NavigationResult<ProbePage>.Committed or NavigationResult<ProbePage>.Superseded,
            $"The guarded push gave {detail}.");
        await Wait(fixture.Navigator.WhenIdleAsync().AsTask());
        Require(region.Current is ProbePage { Name: "from-handler" }, $"The region shows {region.Current}.");
    }

    // A hook's continuation that resumes in a new operation on the model thread, not through the
    // hook's SynchronizationContext (await Dispatcher.Yield(), BeginInvoke, ObserveOn(DispatcherScheduler)),
    // is still the hook's code: it carries the hook's ExecutionContext outside any nested pump.
    private static async Task YieldStyleContinuationsAreTheHookAsync()
    {
        using var context = new SchedulingSyncContextModelContext(freshContexts: true);
        await using var fixture = new Fixture(context);
        var probe = new Probe(context);
        NavigationRegion<ProbePage>? region = null;
        Task<NavigationResult<ProbePage>>? fromContinuation = null;
        var guards = 0;
        var home = new PumpingGuardPage(probe, async () =>
        {
            if (Interlocked.Increment(ref guards) > 1) return true;
            var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Like Dispatcher.BeginInvoke: a new operation with a fresh context, on the captured ExecutionContext.
            var captured = ExecutionContext.Capture()!;
            context.Post(() => ExecutionContext.Run(captured, _ =>
            {
                fromContinuation = region!.PushAsync(NavigationTarget.Own(new ProbePage("from-continuation", probe))).AsTask();
                resumed.SetResult();
            }, null));
            await resumed.Task;
            return true;
        });
        region = fixture.Navigator.CreateRegion<ProbePage>(fixture.Root, NavigationTarget.Borrow(home));
        var detail = await Wait(context.RunOnThreadAsync(() => region.PushAsync(NavigationTarget.Own(new ProbePage("detail", probe))).AsTask()));
        Require(fromContinuation is not null, "The continuation did not run.");
        var continued = await Wait(fromContinuation!);
        Require(continued is NavigationResult<ProbePage>.Rejected { Reason: NavigationRejection.Reentrant },
            $"A request from a hook's continuation in a new operation gave {continued}.");
        Require(detail is NavigationResult<ProbePage>.Committed, $"The guarded push gave {detail}.");
        Require(region.Current is ProbePage { Name: "detail" }, $"The region shows {region.Current}.");
    }

    private sealed class PumpingGuardPage(Probe probe, Func<ValueTask<bool>> guard) : ProbePage("home", probe), INavigationDepartureGuard
    {
        ValueTask<bool> INavigationDepartureGuard.CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) => guard();
    }

    // ---- Threading fixtures -------------------------------------------

    // Completes a task source so that awaiting continuations run inline in this frame. The runtime
    // doesn't inline ConfigureAwait(false) continuations under a custom SynchronizationContext,
    // but a model thread may complete user tasks without one; the engine must not rely on it.
    private static void Inline(Action complete)
    {
        var saved = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { complete(); }
        finally { SynchronizationContext.SetSynchronizationContext(saved); }
    }

    private sealed record HookSite(string Hook, bool OnModelThread, int Operation, int TurnDepth, bool InCallerFrame);

    private sealed class Probe(SyncContextModelContext context)
    {
        public ConcurrentQueue<HookSite> Sites { get; } = new();

        public void Record(string hook) => Sites.Enqueue(new(hook, context.IsOnThread, context.CurrentOperation,
            context.CurrentTurnDepth, SyncContextModelContext.InCallerFrame));

        public HookSite Single(string hook) => Sites.SingleOrDefault(site => site.Hook == hook)
            ?? throw new InvalidOperationException($"Expected one {hook}: {string.Join(", ", Sites.Select(site => site.Hook))}");
    }

    private class ProbePage(string name, Probe probe) : INavigationDepartureGuard, INavigationResume, IDisposable
    {
        private int _disposed;

        public string Name { get; } = name;
        public int Disposed => Volatile.Read(ref _disposed);
        public bool LeaseHeldAtDispose { get; private set; }
        protected Probe Probe { get; } = probe;

        public async ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken)
        {
            Probe.Record($"guard:{Name}");
            await Task.Yield();
            Probe.Record($"guard-resumed:{Name}");
            return true;
        }

        public ValueTask ResumeAsync(NavigationResume resume, CancellationToken cancellationToken)
        {
            Probe.Record($"resume:{Name}");
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            Probe.Record($"dispose:{Name}");
            MarkDisposed();
        }

        protected void MarkDisposed()
        {
            LeaseHeldAtDispose = RunicModelContextRegistry.Shared.TryGet(this, out _);
            Interlocked.Increment(ref _disposed);
        }

        public override string ToString() => Name;
    }

    private sealed class InitProbePage(string name, Probe probe) : ProbePage(name, probe), INavigationInitialize
    {
        public Func<ValueTask>? OnInitialize { get; init; }

        public async ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken)
        {
            Probe.Record($"initialize:{Name}");
            await Task.Yield();
            Probe.Record($"initialize-resumed:{Name}");
            if (OnInitialize is not null) await OnInitialize();
        }
    }

    private sealed class AsyncProbePage(string name, Probe probe) : ProbePage(name, probe), IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Probe.Record($"dispose:{Name}");
            MarkDisposed();
            await Task.Yield();
            Probe.Record($"dispose-resumed:{Name}");
        }
    }

    // Counts the entry scopes a navigator creates and disposes. Each scope is its own provider, so a
    // factory can link its scope to the content it creates, and the scope's disposal is recorded.
    private sealed class ScopeCountingServices(Probe? probe = null) : IServiceProvider, IServiceScopeFactory
    {
        private int _created;
        private int _disposed;

        public int Created => Volatile.Read(ref _created);
        public int Disposed => Volatile.Read(ref _disposed);
        public ConcurrentQueue<CountingScope> Scopes { get; } = new();

        public object? GetService(Type serviceType) => serviceType == typeof(IServiceScopeFactory) ? this : null;

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _created);
            var scope = new CountingScope(this);
            Scopes.Enqueue(scope);
            return scope;
        }

        public sealed class CountingScope(ScopeCountingServices owner) : IServiceScope, IServiceProvider
        {
            private int _disposed;

            public object? Content { get; set; }
            public int Disposed => Volatile.Read(ref _disposed);
            public bool LeaseHeldAtDispose { get; private set; }
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType) => owner.GetService(serviceType);

            public void Dispose()
            {
                owner.Probe?.Record("dispose:scope");
                LeaseHeldAtDispose = Content is not null && RunicModelContextRegistry.Shared.TryGet(Content, out _);
                Interlocked.Increment(ref _disposed);
                Interlocked.Increment(ref owner._disposed);
            }
        }

        private Probe? Probe => probe;

        // A factory target whose content is linked to its entry scope.
        public static INavigationTarget<ProbePage> Target(string name, Probe probe) => NavigationTarget.Create<ProbePage>(services =>
        {
            var page = new ProbePage(name, probe);
            ((CountingScope)services).Content = page;
            return page;
        });
    }

    private static int SiteIndex(Probe probe, string hook) =>
        probe.Sites.Select((site, index) => (site, index)).Single(pair => pair.site.Hook == hook).index;

    // Under a scheduler, retirement disposes the content, then its entry scope, then releases the
    // lease: the content and the scope each as their own operation on the model's thread, outside turns.
    private static async Task ScheduledScopedDisposalOrderAsync()
    {
        using var context = new SchedulingSyncContextModelContext();
        var probe = new Probe(context);
        var services = new ScopeCountingServices(probe);
        await using var fixture = new Fixture(context, services: services, entryScopes: true);
        var region = fixture.Navigator.CreateRegion<ProbePage>(fixture.Root, NavigationTarget.Borrow(new ProbePage("home", probe)));
        Require(await Wait(region.PushAsync(ScopeCountingServices.Target("owned", probe))) is NavigationResult<ProbePage>.Committed,
            "The scoped push did not commit.");
        var owned = (ProbePage)region.Current!;
        await Wait(fixture.Navigator.DisposeAsync().AsTask());

        var content = probe.Single("dispose:owned");
        var scope = probe.Single("dispose:scope");
        var counted = services.Scopes.Single();
        Require(services is { Created: 1, Disposed: 1 } && owned.Disposed == 1 && counted.Disposed == 1,
            $"{services.Created} scopes were created and {services.Disposed} disposed; the content was disposed {owned.Disposed} times.");
        Require(SiteIndex(probe, "dispose:owned") < SiteIndex(probe, "dispose:scope")
            && owned.LeaseHeldAtDispose && counted.LeaseHeldAtDispose && !RunicModelContextRegistry.Shared.TryGet(owned, out _),
            $"Disposal order was not content, scope, lease (lease held: content {owned.LeaseHeldAtDispose}, scope {counted.LeaseHeldAtDispose}).");
        Require(content is { OnModelThread: true, TurnDepth: 0 } && scope is { OnModelThread: true, TurnDepth: 0 }
            && content.Operation != 0 && scope.Operation != 0 && content.Operation != scope.Operation,
            $"The content was disposed at {content} and the scope at {scope}.");
    }

    // A host that blocks the model's thread on DisposeAsync while a scoped entry is live: disposal
    // completes within about twice the close timeout, and the content and then its scope are each
    // disposed once on the pool.
    private static async Task BlockedModelThreadScopedDisposalCompletesAsync()
    {
        var closeTimeout = TimeSpan.FromMilliseconds(300);
        using var context = new SchedulingSyncContextModelContext();
        var probe = new Probe(context);
        var services = new ScopeCountingServices(probe);
        await using var fixture = new Fixture(context, closeTimeout: closeTimeout, services: services, entryScopes: true);
        var region = fixture.Navigator.CreateRegion<ProbePage>(fixture.Root, NavigationTarget.Borrow(new ProbePage("home", probe)));
        Require(await Wait(region.PushAsync(ScopeCountingServices.Target("owned", probe))) is NavigationResult<ProbePage>.Committed,
            "The scoped push did not commit.");
        var owned = (ProbePage)region.Current!;
        var (completed, elapsed) = await context.RunOnThreadAsync(() =>
        {
            var watch = Stopwatch.StartNew();
            // Like GetAwaiter().GetResult(), but a hang fails the test instead of blocking it forever.
            var completed = fixture.Navigator.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            return (completed, watch.Elapsed);
        }).WaitAsync(Timeout);
        // The queued clearing turn and the abandoned disposal operations run now.
        await Wait(context.RunOnThreadAsync(() => true));
        await Wait(fixture.Navigator.DisposeAsync().AsTask());

        var content = probe.Single("dispose:owned");
        var scope = probe.Single("dispose:scope");
        Require(completed && elapsed < 2 * closeTimeout + TimeSpan.FromMilliseconds(500),
            $"Disposal with a blocked model thread took {elapsed} (completed: {completed}).");
        Require(owned.Disposed == 1 && services is { Created: 1, Disposed: 1 } && !content.OnModelThread && !scope.OnModelThread
            && SiteIndex(probe, "dispose:owned") < SiteIndex(probe, "dispose:scope") && fixture.Navigator.UnretiredEntryCount == 0,
            $"The content was disposed {owned.Disposed} times at {content}; {services.Disposed} scopes were disposed, at {scope}.");
    }

    // A factory operation cancelled before it starts: the push is Rejected(Cancelled), the factory never
    // runs, and the entry scope created for it is disposed once.
    private static async Task ScopedFactoryCancelledBeforeStartAsync()
    {
        using var context = new SchedulingSyncContextModelContext();
        var services = new ScopeCountingServices();
        await using var fixture = new Fixture(context, services: services, entryScopes: true);
        using var gate = new ManualResetEventSlim();
        // The guard blocks the model's thread right after it returns, so the factory operation queues behind it.
        var home = new Page("home")
        {
            Guard = (_, _) =>
            {
                context.Post(() => gate.Wait(Timeout));
                return ValueTask.FromResult(true);
            },
        };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        using var cancellation = new CancellationTokenSource();
        var factoryRuns = 0;
        var push = region.PushAsync(NavigationTarget.Create<Page>(_ =>
        {
            Interlocked.Increment(ref factoryRuns);
            return new Page("created");
        }), cancellationToken: cancellation.Token).AsTask();
        await Until(() => context.HookRequests == 2, "The factory was not scheduled.");
        cancellation.Cancel();
        gate.Set();
        var result = await Wait(push);
        await Wait(fixture.Navigator.WhenIdleAsync().AsTask());
        await Until(() => services.Disposed > 0, "The entry scope was not disposed.");
        Require(result is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Cancelled } && factoryRuns == 0
            && services is { Created: 1, Disposed: 1 },
            $"A factory cancelled before it started gave {result}, ran {factoryRuns} times, and {services.Disposed} of {services.Created} scopes were disposed.");
    }

    // A6 with an entry scope: the context closes before the factory operation starts. The push is
    // Rejected(Closed), the factory never runs, and the entry scope is disposed once, on the pool.
    private static async Task ContextClosedBeforeScopedFactoryStartsAsync()
    {
        using var context = new SchedulingSyncContextModelContext();
        var probe = new Probe(context);
        var services = new ScopeCountingServices(probe);
        await using var fixture = new Fixture(context, closeTimeout: TimeSpan.FromMinutes(1), services: services, entryScopes: true);
        using var gate = new ManualResetEventSlim();
        // The guard blocks the model's thread right after it returns, so the factory operation queues behind it.
        var home = new Page("home")
        {
            Guard = (_, _) =>
            {
                context.Post(() => gate.Wait(Timeout));
                return ValueTask.FromResult(true);
            },
        };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var factoryRuns = 0;
        var push = region.PushAsync(NavigationTarget.Create<Page>(_ =>
        {
            Interlocked.Increment(ref factoryRuns);
            return new Page("created");
        })).AsTask();
        await Until(() => context.HookRequests == 2, "The factory was not scheduled.");
        await context.DisposeAsync();
        gate.Set();
        var result = await Wait(push);
        await Wait(fixture.Navigator.WhenIdleAsync().AsTask());
        await Until(() => services.Disposed > 0, "The entry scope was not disposed.");
        Require(result is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed } && factoryRuns == 0
            && services is { Created: 1, Disposed: 1 } && !probe.Single("dispose:scope").OnModelThread
            && fixture.Logs.Count(1061) == 0 && fixture.Logs.Count(1064) == 0,
            $"A context closed before the factory started gave {result}, ran the factory {factoryRuns} times, and disposed {services.Disposed} of {services.Created} scopes.");
    }

    private sealed class ThreadPresentation(SyncContextModelContext context) : INavigationPresentation
    {
        public ConcurrentQueue<bool> Calls { get; } = new();
        public void Forget(object content) => Calls.Enqueue(context.IsOnThread);
    }

    // Forwards IRunicModelContext only, hiding any scheduler of the inner context.
    private sealed class ForwardingContext(IRunicModelContext inner) : IRunicModelContext
    {
        public bool IsExecuting => inner.IsExecuting;

        public event Action<Exception>? UnhandledTurnException
        {
            add => inner.UnhandledTurnException += value;
            remove => inner.UnhandledTurnException -= value;
        }

        public bool TryPost(Action turn) => inner.TryPost(turn);
        public ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default) => inner.InvokeAsync(turn, cancellationToken);
        public ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default) => inner.InvokeAsync(turn, cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    // A model context on one dedicated thread with a SynchronizationContext, like a UI dispatcher:
    // IsExecuting means "on that thread, and not closed", turns on that thread run inline, and turns
    // from other threads are posted. Every posted item is one operation with its own number.
    private class SyncContextModelContext : IRunicModelContext, IDisposable
    {
        [ThreadStatic] public static bool InCallerFrame;
        [ThreadStatic] private static int CurrentOperationId;

        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private readonly bool _inlineCompletions;
        private readonly bool _freshContexts;
        private int _nextOperation;
        private int _turnDepth;
        private int _maxTurnDepth;
        private int _invokeRequests;
        private volatile bool _closed;

        public SyncContextModelContext(bool inlineCompletions = false, bool freshContexts = false)
        {
            _inlineCompletions = inlineCompletions;
            _freshContexts = freshContexts;
            _thread = new Thread(Run) { IsBackground = true, Name = "Model thread" };
            _thread.Start();
        }

        public event Action<Exception>? UnhandledTurnException;

        public bool Closed => _closed;
        public bool IsOnThread => Thread.CurrentThread == _thread;
        public bool IsExecuting => !_closed && IsOnThread;
        public int CurrentOperation => IsOnThread ? CurrentOperationId : 0;
        public int CurrentTurnDepth => IsOnThread ? _turnDepth : 0;
        public int MaxTurnDepth => Volatile.Read(ref _maxTurnDepth);
        public int InvokeRequests => Volatile.Read(ref _invokeRequests);

        private void Run()
        {
            SynchronizationContext.SetSynchronizationContext(new ThreadContext(this));
            foreach (var operation in _queue.GetConsumingEnumerable()) operation();
        }

        // A nested message pump on the model thread, like a modal dialog shown by a hook: dispatches
        // queued operations inside the caller's frame, on its ExecutionContext, until done.
        public void PumpUntil(Func<bool> done)
        {
            if (!IsOnThread) throw new InvalidOperationException("Pump on the model thread.");
            var caller = SynchronizationContext.Current;
            var deadline = DateTime.UtcNow + Timeout;
            try
            {
                while (!done())
                {
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("The nested pump timed out.");
                    if (_queue.TryTake(out var operation, TimeSpan.FromMilliseconds(10))) operation();
                }
            }
            finally { SynchronizationContext.SetSynchronizationContext(caller); }
        }

        // Queues one operation (not a turn).
        public void Post(Action action)
        {
            try
            {
                _queue.Add(() =>
                {
                    // Like a UI dispatcher, which installs a fresh context for every message it dispatches.
                    if (_freshContexts) SynchronizationContext.SetSynchronizationContext(new ThreadContext(this));
                    CurrentOperationId = Interlocked.Increment(ref _nextOperation);
                    try { action(); }
                    catch (Exception error) { UnhandledTurnException?.Invoke(error); }
                    finally { CurrentOperationId = 0; }
                });
            }
            catch (InvalidOperationException) { }
        }

        public Task<T> RunOnThreadAsync<T>(Func<T> action)
        {
            var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try { source.TrySetResult(action()); }
                catch (Exception error) { source.TrySetException(error); }
            });
            return source.Task;
        }

        public Task<T> RunOnThreadAsync<T>(Func<Task<T>> action) => RunOnThreadAsync<Task<T>>(action).Unwrap();

        public bool TryPost(Action turn)
        {
            if (_closed) return false;
            Post(() =>
            {
                if (_closed) UnhandledTurnException?.Invoke(new ObjectDisposedException(nameof(SyncContextModelContext)));
                else RunTurn(() => { turn(); return true; });
            });
            return true;
        }

        public ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default) =>
            new(InvokeAsync(() => { turn(); return true; }, cancellationToken).AsTask());

        public ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invokeRequests);
            if (_closed) return ValueTask.FromException<T>(new ObjectDisposedException(nameof(SyncContextModelContext)));
            if (IsOnThread)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new(RunTurn(turn));
            }
            var source = new TaskCompletionSource<T>(_inlineCompletions ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                if (_closed) source.TrySetException(new ObjectDisposedException(nameof(SyncContextModelContext)));
                else if (cancellationToken.IsCancellationRequested) source.TrySetCanceled(cancellationToken);
                else
                {
                    T result;
                    try { result = RunTurn(turn); }
                    catch (Exception error)
                    {
                        source.TrySetException(error);
                        return;
                    }
                    if (_inlineCompletions) Inline(() => source.TrySetResult(result));
                    else source.TrySetResult(result);
                }
            });
            return new(source.Task);
        }

        private T RunTurn<T>(Func<T> turn)
        {
            var depth = ++_turnDepth;
            if (depth > _maxTurnDepth) Volatile.Write(ref _maxTurnDepth, depth);
            try { return turn(); }
            finally { _turnDepth--; }
        }

        // Closes the context; the thread keeps running posted continuations until Dispose.
        public ValueTask DisposeAsync()
        {
            _closed = true;
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            _closed = true;
            _queue.CompleteAdding();
        }

        private sealed class ThreadContext(SyncContextModelContext owner) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback callback, object? state) => owner.Post(() => callback(state));
            public override void Send(SendOrPostCallback callback, object? state) => throw new NotSupportedException();
            public override SynchronizationContext CreateCopy() => this;
        }
    }

    // The scheduling variant: each hook is its own posted operation, never inline, never a turn.
    private sealed class SchedulingSyncContextModelContext(bool freshContexts = false)
        : SyncContextModelContext(freshContexts: freshContexts), IRunicModelHookScheduler
    {
        private int _hookRequests;

        public int HookRequests => Volatile.Read(ref _hookRequests);

        public Task<T> RunHookAsync<T>(Func<Task<T>> hook, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _hookRequests);
            if (Closed) return Task.FromException<T>(new ObjectDisposedException(nameof(SchedulingSyncContextModelContext)));
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);
            var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                if (Closed)
                {
                    source.TrySetException(new ObjectDisposedException(nameof(SchedulingSyncContextModelContext)));
                    return;
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    source.TrySetCanceled(cancellationToken);
                    return;
                }
                Task<T> task;
                try { task = hook(); }
                catch (Exception error)
                {
                    source.TrySetException(error);
                    return;
                }
                task.ContinueWith(static (completed, state) =>
                {
                    var target = (TaskCompletionSource<T>)state!;
                    if (completed.IsFaulted) target.TrySetException(completed.Exception!.InnerExceptions);
                    else if (completed.IsCanceled) target.TrySetCanceled();
                    else target.TrySetResult(completed.Result);
                }, source, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            });
            return source.Task;
        }
    }
}
