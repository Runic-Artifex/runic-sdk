using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// W230-002 slice 1: the experimental navigator core (design record W230-001 §4-§6, §10, §13).
internal static class NavigationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static async Task RunAsync()
    {
        await PushBackAndHistoryAsync();
        await OperationsAsync();
        await ExpectedCurrentAndLateCompletionAsync();
        await InitializeOnceAndResumeOnReturnAsync();
        await CancellationWhileWaitingAsync();
        await CancellationWhileGuardingAsync();
        await CancellationWhilePreparingAsync();
        await CancellationBeforeCommitTurnAsync();
        await PreCancelledRequestDoesNotSupersedeAsync();
        await CloseDuringPreparationIsRejectedAsync();
        await CloseChangesStateInsideTurnsAsync();
        await CloseTimeoutBoundsDisposalAsync();
        await CancellationCallbackFailureDoesNotAbortDisposalAsync();
        await RetirementWaitsForInitializeAsync();
        await InitializeWaitsShareOneDeadlineAsync();
        await SharedClearingDeadlineAsync();
        await RetiredEntryNeverInitializesAsync();
        await GuardFailureDuringCloseIsRejectedAsync();
        await CreateRegionDisposesFactoryInstanceAsync();
        await SupersessionBeforeCommitAsync();
        await NoSupersessionAfterCommitStartsAsync();
        await HookFailuresAsync();
        await GuardsRunForRetainAsync();
        await ContextDisposedDuringCommitAsync();
        await ReentrantHooksAsync();
        await AdmissionInsideTurnAsync();
        await TransitioningRaisedInTurnsAsync();
        await ThrowingHandlerDoesNotSkipOthersAsync();
        await ChildPoliciesAsync();
        await ParentSupersedesChildTransitionsAsync();
        await ChildCommitSupersedesParentAsync();
        await RetirementDoesNotAwaitChildTransitionAsync();
        await RetirementOrderAsync();
        await BorrowedContentIsNeverDisposedAsync();
        await OwnershipChecksAsync();
        await TargetValidationAsync();
        await InitializePrecedenceAsync();
        await DisposeMidTransitionAsync();
        await DisposeWaitsForRunningCleanupAsync();
        await CleanupFailureIsLoggedAsync();
        await OverrunIsLoggedAsync();
        await ServiceRegistrationAsync();
        await ResultCompletedAsync();
        await ResultFromEmptyRegionAsync();
        await ResultDismissedWhenNotCommittedAsync();
        await ResultDismissedOnRetirementAsync();
        await ResultTypeChecksAsync();
        await ResultCompleteRejectedKeepsRequestOpenAsync();
        await ResultCallerCancelledAfterCommitAsync();
        await ResultCallerCancelledBackRejectedAsync();
        await ResultCallerCancelledThenPushRetiresAsync();
        await ResultDismissedOnCloseAsync();
        await SiblingGuardAwaitsResultAsync(close: false);
        await SiblingGuardAwaitsResultAsync(close: true);
        await ResultRaceAsync(seed: 230_003);
        await ResultRaceAsync(seed: 61, disposeDuringRace: true);
        await RandomizedRaceAsync(seed: 230_002);
        await RandomizedRaceAsync(seed: 61);
        await RandomizedRaceAsync(seed: 230_002, disposeDuringRace: true);
        await RandomizedRaceAsync(seed: 61, disposeDuringRace: true);
        await RandomizedRaceAsync(seed: 230_002, disposeDuringRace: true, patient: true);
        await RandomizedRaceAsync(seed: 61, disposeDuringRace: true, patient: true);
    }

    // ---- Basic operations ------------------------------------------------

    private static async Task PushBackAndHistoryAsync()
    {
        await using var fixture = new Fixture();
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        Require(region.Current == home && region.CurrentEntry is { Id.Value: 1, State: NavigationEntryState.Active, Ownership: NavigationOwnership.Borrowed }
            && !region.CanGoBack && region.History.Count == 0 && !region.IsTransitioning,
            "The initial target did not commit synchronously.");

        var detail = new InitPage("detail");
        var pushed = await Wait(region.PushAsync(NavigationTarget.Own<Page>(detail)));
        Require(pushed is NavigationResult<Page>.Committed { Current: { Id.Value: 2, Ownership: NavigationOwnership.Owned }, Retired.Count: 0 },
            $"Push did not commit: {pushed}");
        Require(region.Current == detail && region.CanGoBack && region.History is [{ State: NavigationEntryState.Retained } retained]
            && retained.Content == home && detail.Initialized == 1,
            "Push did not retain the previous entry and initialize the new one.");

        var back = await Wait(region.BackAsync());
        Require(back is NavigationResult<Page>.Committed { Current.Content: var current, Retired: [{ Value: 2 }] } && current == home,
            $"Back did not commit: {back}");
        Require(detail.Disposed == 1 && !detail.DisposedInTurn && home.Resumed == 1 && region.History.Count == 0,
            "Back did not retire the owned entry and resume the retained one.");
        Require(fixture.Navigator.TrackedEntryCount == 1, $"{fixture.Navigator.TrackedEntryCount} entries are tracked after Back.");
        Require(!RunicModelContextRegistry.Shared.TryGet(detail, out _), "The retired entry's model-context lease was not released.");
    }

    private static async Task OperationsAsync()
    {
        await using var fixture = new Fixture();
        var a = new Page("a");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(a));
        var b = new Page("b");
        var c = new Page("c");
        var d = new Page("d");
        await Wait(region.PushAsync(NavigationTarget.Own(b)));
        var bId = region.CurrentEntry!.Id;
        await Wait(region.PushAsync(NavigationTarget.Own(c)));
        await Wait(region.PushAsync(NavigationTarget.Own(d)));
        Require(Names(region) == "a,b,c,d", $"Pushes produced {Names(region)}.");

        var backTo = await Wait(region.BackToAsync(bId));
        Require(backTo is NavigationResult<Page>.Committed { Retired: [{ Value: 4 }, { Value: 3 }] } && Names(region) == "a,b"
            && d.Disposed == 1 && c.Disposed == 1 && b.Resumed == 1,
            $"BackTo did not retire top-down: {backTo} {Names(region)}");
        var same = await Wait(region.BackToAsync(bId));
        Require(same is NavigationResult<Page>.Committed { Retired.Count: 0 } && Names(region) == "a,b", "BackTo the current entry changed history.");
        var missing = await Wait(region.BackToAsync(new NavigationEntryId(99)));
        Require(missing is NavigationResult<Page>.Rejected { Reason: NavigationRejection.EntryNotFound }, $"BackTo a missing entry gave {missing}.");

        var e = new Page("e");
        var replaced = await Wait(region.ReplaceAsync(NavigationTarget.Own(e)));
        Require(replaced is NavigationResult<Page>.Committed { Retired: [{ Value: 2 }] } && Names(region) == "a,e" && b.Disposed == 1,
            $"Replace did not retire only the current entry: {Names(region)}");

        var f = new Page("f");
        var reset = await Wait(region.ResetAsync(NavigationTarget.Own(f)));
        Require(reset is NavigationResult<Page>.Committed { Retired.Count: 2 } && Names(region) == "f" && e.Disposed == 1 && a.Disposed == 0,
            $"Reset did not retire every entry: {Names(region)}");

        var g = new Page("g");
        await Wait(region.PushAsync(NavigationTarget.Own(g)));
        var cleared = await Wait(region.ClearHistoryAsync());
        Require(cleared is NavigationResult<Page>.Committed { Retired: [{ Value: 6 }] } && Names(region) == "g" && f.Disposed == 1 && !region.CanGoBack,
            $"ClearHistory did not keep only the current entry: {Names(region)}");

        var noHistory = await Wait(region.BackAsync());
        Require(noHistory is NavigationResult<Page>.Rejected { Reason: NavigationRejection.NoHistory }, $"Back without history gave {noHistory}.");

        var empty = await Wait(region.ClearAsync());
        Require(empty is NavigationResult<Page>.Committed { Current: null } && region.Current is null && region.CurrentEntry is null
            && g.Disposed == 1 && fixture.Navigator.TrackedEntryCount == 0,
            "Clear did not empty the region.");
        var fromEmpty = await Wait(region.PushAsync(NavigationTarget.Own(new Page("h"))));
        Require(fromEmpty is NavigationResult<Page>.Committed && Names(region) == "h", "A push into an empty region failed.");
    }

    private static async Task ExpectedCurrentAndLateCompletionAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var first = new InitPage("first");
        await Wait(region.PushAsync(NavigationTarget.Own<Page>(first)));
        var stale = region.History[0].Id;
        var notCurrent = await Wait(region.PushAsync(NavigationTarget.Own(new Page("x")), new NavigationRequestOptions(stale)));
        Require(notCurrent is NavigationResult<Page>.Rejected { Reason: NavigationRejection.NotCurrent }, $"A stale ExpectedCurrent gave {notCurrent}.");

        var second = new InitPage("second");
        await Wait(region.PushAsync(NavigationTarget.Own<Page>(second), new NavigationRequestOptions(region.CurrentEntry!.Id)));
        // A retained entry completing late is rejected; the current one goes back.
        var late = await Wait(first.Entry!.BackAsync());
        Require(late is NavigationResult<object>.Rejected { Reason: NavigationRejection.NotCurrent } && region.Current == second,
            $"A retained entry's late Back gave {late}.");
        var own = await Wait(second.Entry!.BackAsync());
        Require(own is NavigationResult<object>.Committed { Current.Content: var content } && content == first && region.Current == first,
            $"The current entry's Back gave {own}.");
        var retired = await Wait(second.Entry!.BackAsync());
        Require(retired is NavigationResult<object>.Rejected { Reason: NavigationRejection.NotCurrent } && second.Entry.Retirement.IsCancellationRequested,
            "A retired entry's late Back was not rejected.");
    }

    private static async Task InitializeOnceAndResumeOnReturnAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var editor = new InitPage("editor");
        await Wait(region.PushAsync(NavigationTarget.Own<Page>(editor)));
        var review = new Page("review");
        await Wait(region.PushAsync(NavigationTarget.Own(review)));

        // A vetoed and a failed Back leave history unchanged; a retry resumes again.
        var veto = true;
        review.Guard = (_, _) => ValueTask.FromResult(!veto);
        var vetoed = await Wait(region.BackAsync());
        Require(vetoed is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard, By: var by } && by == region.CurrentEntry!.Id
            && Names(region) == "home,editor,review" && editor.Resumed == 0,
            $"A vetoed Back gave {vetoed}.");
        veto = false;
        editor.Resume = (_, _) => throw new InvalidOperationException("resume failed");
        var failed = await Wait(region.BackAsync());
        Require(failed is NavigationResult<Page>.Failed { Phase: NavigationPhase.Preparing } && Names(region) == "home,editor,review"
            && editor.Resumed == 1 && review.Disposed == 0,
            $"A failed resume gave {failed}.");
        editor.Resume = null;
        var retried = await Wait(region.BackAsync());
        Require(retried is NavigationResult<Page>.Committed && Names(region) == "home,editor" && editor.Resumed == 2 && editor.Initialized == 1
            && editor.LastResume == new NavigationResume(region.CurrentEntry!.Id, NavigationOperation.Back),
            "Retrying resumed without re-initializing.");
    }

    // ---- Cancellation ----------------------------------------------------

    private static async Task CancellationWhileWaitingAsync()
    {
        await using var fixture = new Fixture();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var home = new Page("home");
        var calls = 0;
        home.Guard = async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return await gate.Task; // ignores its token
        };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var first = region.PushAsync(NavigationTarget.Own(new Page("first"))).AsTask();
        await Until(() => Volatile.Read(ref calls) == 1, "The first guard did not start.");
        using var cancel = new CancellationTokenSource();
        var waiting = new Page("waiting");
        var second = region.PushAsync(NavigationTarget.Own(waiting), cancellationToken: cancel.Token).AsTask();
        cancel.Cancel();
        var cancelled = await Wait(second);
        Require(cancelled is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Cancelled } && calls == 1 && waiting.Disposed == 1,
            $"Cancelling a waiting request gave {cancelled} after {calls} guard calls.");
        gate.SetResult(true);
        var superseded = await Wait(first);
        Require(superseded is NavigationResult<Page>.Superseded && Names(region) == "home",
            $"The superseded first request gave {superseded}.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(fixture.Navigator.TrackedEntryCount == 1 && !region.IsTransitioning, "Admission did not recover after cancellation.");
    }

    private static async Task CancellationWhileGuardingAsync()
    {
        await using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var home = new Page("home")
        {
            Guard = async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
                return true;
            },
        };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        using var cancel = new CancellationTokenSource();
        var next = new Page("next");
        var pending = region.PushAsync(NavigationTarget.Own(next), cancellationToken: cancel.Token).AsTask();
        await Wait(started.Task);
        cancel.Cancel();
        var result = await Wait(pending);
        Require(result is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Cancelled } && Names(region) == "home" && next.Disposed == 1,
            $"Cancelling a guard gave {result}.");
    }

    private static async Task CancellationWhilePreparingAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new InitPage("slow")
        {
            Initialize = async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            },
        };
        using var cancel = new CancellationTokenSource();
        var pending = region.PushAsync(NavigationTarget.Own<Page>(slow), cancellationToken: cancel.Token).AsTask();
        await Wait(started.Task);
        cancel.Cancel();
        var result = await Wait(pending);
        Require(result is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Cancelled } && Names(region) == "home"
            && slow.Disposed == 1 && slow.Entry!.Retirement.IsCancellationRequested && fixture.Navigator.TrackedEntryCount == 1,
            $"Cancelling initialize gave {result}; the pending entry was not retired.");
    }

    private static async Task CancellationBeforeCommitTurnAsync()
    {
        await using var gated = new GatedContext();
        await using var fixture = new Fixture(gated);
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        using var cancel = new CancellationTokenSource();
        var next = new Page("next");
        gated.Close();
        var pending = region.PushAsync(NavigationTarget.Own(next), cancellationToken: cancel.Token).AsTask();
        await Wait(gated.Waiting.Task);
        cancel.Cancel();
        gated.Open();
        var result = await Wait(pending);
        Require(result is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Cancelled } && Names(region) == "home" && next.Disposed == 1,
            $"Cancelling before the commit turn gave {result}.");
    }

    // ---- Supersession ----------------------------------------------------

    // A request whose token is already cancelled is rejected at admission, so it
    // must not supersede the transition that is already in flight.
    private static async Task PreCancelledRequestDoesNotSupersedeAsync()
    {
        await using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var home = new Page("home");
        home.Guard = async (departure, token) =>
        {
            if (departure.Entry.Value == 1 && !started.Task.IsCompleted)
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
            }
            return true;
        };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var older = new Page("older");
        var first = region.PushAsync(NavigationTarget.Own(older)).AsTask();
        await Wait(started.Task);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var rejected = await Wait(region.PushAsync(NavigationTarget.Own(new Page("cancelled")), null, cancelled.Token));
        Require(rejected is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Cancelled },
            $"A cancelled request gave {rejected}.");

        release.SetResult();
        var committed = await Wait(first);
        Require(committed is NavigationResult<Page>.Committed && Names(region) == "home,older" && older.Disposed == 0,
            $"A cancelled request superseded its predecessor: {committed}, {Names(region)}.");
    }

    private static async Task CloseDuringPreparationIsRejectedAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        // The factory runs on the caller's thread until its first await, so the push starts on the pool.
        var push = Task.Run(() => region.PushAsync(NavigationTarget.Create<Page>(_ =>
        {
            entered.TrySetResult();
            release.Wait(Timeout);
            // Once the navigator is closing, this throws ObjectDisposedException from the factory.
            fixture.Navigator.CreateRegion<Page>(new object());
            return new Page("created");
        })).AsTask());
        await Wait(entered.Task);
        var disposal = fixture.Navigator.DisposeAsync().AsTask();
        await Until(() => Throws<ObjectDisposedException>(() => fixture.Navigator.CreateRegion<Page>(new object())),
            "The navigator did not start closing.");
        release.Set();
        var result = await Wait(push);
        await Wait(disposal);
        Require(result is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed } && fixture.Logs.Count(1061) == 0,
            $"A close during preparation gave {result} and logged {fixture.Logs.Count(1061)} preparation failures.");
    }

    private sealed record BlockedClose(Fixture Fixture, FakeTimeProvider Time, NavigationRegion<Page> Region, Page Pushed,
        Task Push, Task Disposal, ManualResetEventSlim Release, ConcurrentQueue<Page?> Seen);

    // Disposes a navigator whose region's commit turn is blocked in a PropertyChanged handler.
    // Time is fake: the wait for running transitions is expired once, then time stops while
    // the close waits for its clearing turn (the gated context counts those requests).
    private static async Task<BlockedClose> BlockedCloseAsync()
    {
        var context = new GatedContext();
        var time = new FakeTimeProvider();
        var fixture = new Fixture(context, time, TimeSpan.FromSeconds(10));
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var inTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        var seen = new ConcurrentQueue<Page?>();
        region.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(region.Current)) return;
            seen.Enqueue(region.Current);
            inTurn.TrySetResult();
            release.Wait(Timeout);
        };
        var pushed = new Page("pushed");
        var push = region.PushAsync(NavigationTarget.Own(pushed)).AsTask();
        await Wait(inTurn.Task);
        // DisposeAsync registers the timer of its wait for the running transition before it
        // returns, so one advance expires it.
        var disposal = fixture.Navigator.DisposeAsync().AsTask();
        time.Advance(TimeSpan.FromSeconds(11));
        await Until(() => context.ActionRequests > 0, "Closing did not reach the clearing turn.");
        return new(fixture, time, region, pushed, push, disposal, release, seen);
    }

    private static async Task CloseChangesStateInsideTurnsAsync()
    {
        var blocked = await BlockedCloseAsync();
        await using (blocked.Fixture)
        using (blocked.Release)
        {
            // The commit turn is still raising notifications and the close timeout has not
            // passed, so the close waits for the turn before it changes the region.
            await Task.Delay(300);
            var during = (blocked.Region.Current, blocked.Region.History.Count);
            blocked.Release.Set();
            await Wait(blocked.Disposal);
            await Wait(blocked.Push);
            Require(during.Current == blocked.Pushed && during.Count == 1,
                "Closing changed the region while its commit turn was still raising notifications.");
            Require(blocked.Region.Current is null && !blocked.Region.CanGoBack && blocked.Pushed.Disposed == 1,
                "Closing did not clear and retire the region.");
        }
    }

    private static async Task CloseTimeoutBoundsDisposalAsync()
    {
        var blocked = await BlockedCloseAsync();
        await using (blocked.Fixture)
        using (blocked.Release)
        {
            // The turn stays blocked. Once the close timeout passes, the region is cleared
            // outside a turn and disposal completes with a warning.
            await Until(() =>
            {
                if (blocked.Disposal.IsCompleted) return true;
                blocked.Time.Advance(TimeSpan.FromSeconds(11));
                return false;
            }, "A blocked turn kept DisposeAsync from completing after the close timeout.");
            await Wait(blocked.Disposal);
            Require(blocked.Region.Current is null && blocked.Pushed.Disposed == 1
                && blocked.Fixture.Logs.Has(1068, LogLevel.Warning),
                "The timed-out close did not clear and retire the region with a warning.");
            blocked.Release.Set();
            await Wait(blocked.Push);
            // The notification owed for the cleared stack is raised once the turn is free.
            await Until(() => blocked.Seen.Contains(null), "The cleared region never raised its Current change.");
        }
    }

    // Disposal waits for an initialize hook that returns within the close timeout before it
    // disposes the content, and does not dispose under the hook while it runs.
    private static async Task RetirementWaitsForInitializeAsync()
    {
        var time = new FakeTimeProvider();
        var fixture = new Fixture(null, time, TimeSpan.FromSeconds(10));
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initDone = 0;
        var disposedDuringInit = 0;
        var page = new InitPage("slow")
        {
            Initialize = async (_, _) =>
            {
                started.SetResult();
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
        var disposal = fixture.Navigator.DisposeAsync().AsTask();
        await Task.Delay(100);
        Require(page.Disposed == 0 && !disposal.IsCompleted, "Disposal disposed content under a running initialize hook.");
        gate.SetResult();
        await Wait(disposal);
        Require(page.Disposed == 1 && disposedDuringInit == 0, "Content was disposed before its initialize hook returned.");
        Require(await Wait(push) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed }, "The pending push was not rejected.");
        Require(fixture.Logs.Count(1069) == 0, "A hook that returned in time logged a timeout.");
        await fixture.Context.DisposeAsync();
    }

    // K regions whose initialize hooks ignore cancellation: disposal ends after one close timeout
    // for the transitions (no per-region re-wait), without any further advance of the clock.
    private static async Task InitializeWaitsShareOneDeadlineAsync()
    {
        const int regions = 3;
        var time = new FakeTimeProvider();
        var fixture = new Fixture(null, time, TimeSpan.FromSeconds(10));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pages = new List<InitPage>();
        var pushes = new List<Task>();
        for (var index = 0; index < regions; index++)
        {
            var region = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Borrow(new Page($"home{index}")));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var page = new InitPage($"blocked{index}") { Initialize = async (_, _) => { started.SetResult(); await gate.Task; } };
            pages.Add(page);
            pushes.Add(region.PushAsync(NavigationTarget.Own<Page>(page)).AsTask());
            await Wait(started.Task);
        }
        var disposal = fixture.Navigator.DisposeAsync().AsTask();
        // One advance expires the wait for the transitions; the hooks are not awaited again.
        time.Advance(TimeSpan.FromSeconds(11));
        await Wait(disposal);
        Require(pages.All(page => page.Disposed == 1), "Disposal did not retire every blocked entry within one close timeout.");
        Require(fixture.Logs.Count(1069) == regions && fixture.Logs.Has(1069, LogLevel.Warning),
            $"Expected {regions} initialize timeout warnings, got {fixture.Logs.Count(1069)}.");
        gate.SetResult();
        await Wait(Task.WhenAll(pushes));
        await fixture.Context.DisposeAsync();
    }

    // Retirement that wins the claim keeps initialize from ever starting. The stall between the
    // cancellation check and the claim is held by a test seam while disposal retires the entry.
    private static async Task RetiredEntryNeverInitializesAsync()
    {
        var time = new FakeTimeProvider();
        var fixture = new Fixture(null, time, TimeSpan.FromSeconds(10));
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var page = new InitPage("late");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var proceed = new ManualResetEventSlim();
        fixture.Navigator.BeforeInitializeClaim = () =>
        {
            reached.TrySetResult();
            proceed.Wait(Timeout);
        };
        var push = Task.Run(async () => await region.PushAsync(NavigationTarget.Own<Page>(page)));
        await Wait(reached.Task);
        var disposal = fixture.Navigator.DisposeAsync().AsTask();
        // The stalled transition keeps the first wait running; expire it so retirement runs.
        time.Advance(TimeSpan.FromSeconds(11));
        await Until(() => page.Disposed == 1, "Disposal did not retire the stalled entry.");
        proceed.Set();
        await Wait(disposal);
        await Wait(push);
        Require(page.Initialized == 0, $"Initialize ran {page.Initialized} times on content retired before it was claimed.");
        await fixture.Context.DisposeAsync();
    }

    // The clearing turns of two blocked regions share one deadline: one advance, one warning.
    private static async Task SharedClearingDeadlineAsync()
    {
        var context = new GatedContext();
        var time = new FakeTimeProvider();
        var fixture = new Fixture(context, time, TimeSpan.FromSeconds(10));
        var first = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Own(new Page("first")));
        var second = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Own(new Page("second")));
        context.Close();
        var disposal = fixture.Navigator.DisposeAsync().AsTask();
        time.Advance(TimeSpan.FromSeconds(11));
        await Wait(disposal);
        Require(first.Current is null && second.Current is null, "A timed-out close left a region populated.");
        Require(fixture.Logs.Count(1068) == 1, $"Expected one close timeout warning, got {fixture.Logs.Count(1068)}.");
        context.Open();
        await fixture.Context.DisposeAsync();
    }

    private static async Task CancellationCallbackFailureDoesNotAbortDisposalAsync()
    {
        await using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var home = new Page("home")
        {
            Guard = async (_, token) =>
            {
                token.Register(() => throw new InvalidOperationException("cancellation callback"));
                entered.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
                return true;
            },
        };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Own(home));
        var next = new Page("next");
        var push = region.PushAsync(NavigationTarget.Own(next)).AsTask();
        await Wait(entered.Task);
        await Wait(fixture.Navigator.DisposeAsync().AsTask());
        var result = await Wait(push);
        Require(home.Disposed == 1 && next.Disposed == 1 && region.Current is null && fixture.Navigator.TrackedEntryCount == 0,
            $"A throwing cancellation callback aborted disposal: {result}, {home.Disposed}, {next.Disposed}.");
        var logged = fixture.Logs.Single(1064);
        Require(logged.State["Step"] as string == "Cancel" && logged.Exception is not null,
            "The cancellation callback failure was not logged as a Cancel step.");
    }

    private static async Task GuardFailureDuringCloseIsRejectedAsync()
    {
        await using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var home = new Page("home")
        {
            Guard = (_, _) =>
            {
                entered.TrySetResult();
                release.Wait(Timeout, CancellationToken.None);
                throw new InvalidOperationException("guard failed while closing");
            },
        };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var push = Task.Run(() => region.PushAsync(NavigationTarget.Own(new Page("next"))).AsTask());
        await Wait(entered.Task);
        var disposal = fixture.Navigator.DisposeAsync().AsTask();
        await Until(() => Throws<ObjectDisposedException>(() => fixture.Navigator.CreateRegion<Page>(new object())),
            "The navigator did not start closing.");
        release.Set();
        var result = await Wait(push);
        await Wait(disposal);
        Require(result is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed } && fixture.Logs.Count(1060) == 0,
            $"A guard failing during a close gave {result} and logged {fixture.Logs.Count(1060)} guard failures.");
    }

    private static async Task CreateRegionDisposesFactoryInstanceAsync()
    {
        await using var fixture = new Fixture();
        // Closing starts while the factory runs.
        var closing = new Page("closing");
        Require(Throws<ObjectDisposedException>(() => fixture.Navigator.CreateRegion<Page>(new object(),
                NavigationTarget.Create<Page>(services =>
                {
                    _ = fixture.Navigator.DisposeAsync().AsTask();
                    return closing;
                }))),
            "A region was created while the navigator was closing.");
        await Until(() => closing.Disposed == 1, "The factory instance was not disposed when closing started.");

        // Binding the instance fails: it is already bound to another model context.
        await using var navigating = new Fixture();
        await using var holding = new Fixture();
        var bound = new Page("bound");
        using var lease = RunicModelContextRegistry.Shared.Bind(holding.Context, bound);
        Require(Throws<InvalidOperationException>(() => navigating.Navigator.CreateRegion<Page>(new object(),
                NavigationTarget.Create<Page>(_ => bound))),
            "A model context conflict did not fail the region.");
        await Until(() => bound.Disposed == 1, "The factory instance was not disposed when binding failed.");

        // An initializable factory instance that is only IAsyncDisposable is disposed too.
        var init = new InitPage("init");
        Require(Throws<ArgumentException>(() => navigating.Navigator.CreateRegion<Page>(new object(),
                NavigationTarget.Create<Page>(_ => init))),
            "An initializable factory initial target was accepted.");
        await Until(() => init.Disposed == 1, "An asynchronously disposable rejected instance was not disposed.");
    }

    private static async Task SupersessionBeforeCommitAsync()
    {
        await using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var home = new Page("home");
        home.Guard = async (departure, token) =>
        {
            if (departure.Entry.Value == 1 && !started.Task.IsCompleted)
            {
                started.SetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            }
            return true;
        };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var older = new Page("older");
        var first = region.PushAsync(NavigationTarget.Own(older)).AsTask();
        await Wait(started.Task);
        var newer = new Page("newer");
        var second = region.PushAsync(NavigationTarget.Own(newer)).AsTask();
        var superseded = await Wait(first);
        var committed = await Wait(second);
        Require(superseded is NavigationResult<Page>.Superseded && committed is NavigationResult<Page>.Committed
            && Names(region) == "home,newer" && older.Disposed == 1 && newer.Disposed == 0,
            $"Supersession gave {superseded} and {committed}.");
        Require(fixture.Logs.Has(1066, LogLevel.Debug), "Superseded was not logged as 1066.");
    }

    private static async Task NoSupersessionAfterCommitStartsAsync()
    {
        await using var gated = new GatedContext();
        await using var fixture = new Fixture(gated);
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        gated.Close();
        var committing = region.PushAsync(NavigationTarget.Own(new Page("first"))).AsTask();
        await Wait(gated.Waiting.Task);
        var later = region.PushAsync(NavigationTarget.Own(new Page("second"))).AsTask();
        gated.Open();
        var first = await Wait(committing);
        var second = await Wait(later);
        Require(first is NavigationResult<Page>.Committed && second is NavigationResult<Page>.Committed && Names(region) == "home,first,second",
            $"A committing transition was superseded: {first}, {second}.");
    }

    // ---- Failures and logging -------------------------------------------

    private static async Task HookFailuresAsync()
    {
        await using var fixture = new Fixture();
        var home = new Page("home") { Guard = (_, _) => throw new InvalidOperationException("guard-secret") };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var next = new Page("next");
        var guarded = await Wait(region.PushAsync(NavigationTarget.Own(next)));
        Require(guarded is NavigationResult<Page>.Failed { Phase: NavigationPhase.Guarding, Error.Message: "guard-secret" }
            && Names(region) == "home" && next.Disposed == 1,
            $"A throwing guard gave {guarded}.");
        var entry = fixture.Logs.Single(1060);
        Require(entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException
            && entry.State["Region"] as string == "Page" && entry.State["RegionId"] is int regionId && regionId > 0
            && entry.State["Operation"] as string == "Push" && entry.State["EntryType"] as string == "Page"
            && !entry.Message.Contains("guard-secret", StringComparison.Ordinal),
            $"1060 was not structured: {string.Join(", ", entry.State)}");

        home.Guard = null;
        var failing = new InitPage("failing") { Initialize = (_, _) => throw new FormatException("init") };
        var initialized = await Wait(region.PushAsync(NavigationTarget.Own<Page>(failing)));
        Require(initialized is NavigationResult<Page>.Failed { Phase: NavigationPhase.Preparing, Error: FormatException }
            && failing.Disposed == 1 && failing.Initialized == 1 && Names(region) == "home",
            $"A throwing initialize gave {initialized}.");
        var preparation = fixture.Logs.Single(1061);
        Require(preparation.Exception is FormatException && preparation.State["EntryType"] as string == "InitPage",
            "1061 did not carry the initialize failure.");

        var factory = await Wait(region.PushAsync(NavigationTarget.Create<Page>(_ => throw new NotSupportedException("factory"))));
        Require(factory is NavigationResult<Page>.Failed { Phase: NavigationPhase.Preparing, Error: NotSupportedException }
            && fixture.Navigator.TrackedEntryCount == 1,
            $"A throwing factory gave {factory}.");
        var rejected = await Wait(region.BackAsync());
        Require(rejected is NavigationResult<Page>.Rejected && fixture.Logs.Has(1065, LogLevel.Debug), "Rejected was not logged as 1065.");
    }

    private static async Task GuardsRunForRetainAsync()
    {
        await using var fixture = new Fixture();
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        home.Guard = (departure, _) => ValueTask.FromResult(departure.Kind != NavigationDepartureKind.Retain);
        var vetoed = await Wait(region.PushAsync(NavigationTarget.Own(new Page("next"))));
        Require(vetoed is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard, By.Value: 1 }
            && home.Departures is [{ Kind: NavigationDepartureKind.Retain, Operation: NavigationOperation.Push }],
            $"A Retain guard gave {vetoed}.");
        home.Guard = null;
        var next = new Page("next");
        await Wait(region.PushAsync(NavigationTarget.Own(next)));
        await Wait(region.BackAsync());
        Require(next.Departures is [{ Kind: NavigationDepartureKind.Retire, Operation: NavigationOperation.Back }],
            "A Back did not ask the departing guard with Kind = Retire.");
    }

    private static async Task ContextDisposedDuringCommitAsync()
    {
        await using var gated = new GatedContext();
        await using var fixture = new Fixture(gated);
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var next = new Page("next");
        gated.Close();
        var pending = region.PushAsync(NavigationTarget.Own(next)).AsTask();
        await Wait(gated.Waiting.Task);
        await gated.Inner.DisposeAsync();
        gated.Open();
        var result = await Wait(pending);
        Require(result is NavigationResult<Page>.Failed { Phase: NavigationPhase.Committing, Error: ObjectDisposedException }
            && Names(region) == "home" && next.Disposed == 1,
            $"A disposed context during commit gave {result}.");
        Require(fixture.Logs.Single(1062).Exception is ObjectDisposedException, "1062 did not carry the exception.");
    }

    // ---- Reentrancy and turns ---------------------------------------------

    private static async Task ReentrantHooksAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var sibling = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        NavigationResult<Page>? inner = null;
        NavigationResult<object>? innerBack = null;
        NavigationResult<Page>? siblingResult = null;
        var transitionEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<NavigationResult<Page>>? spawned = null;
        var reentrant = new InitPage("reentrant")
        {
            Initialize = async (entry, token) =>
            {
                inner = await region.PushAsync(NavigationTarget.Own(new Page("inner")), cancellationToken: token);
                innerBack = await entry.BackAsync(token);
                siblingResult = await sibling.PushAsync(NavigationTarget.Own(new Page("sibling")), cancellationToken: token);
                // Work the hook spawned outlives its transition and is not reentrant then.
                spawned = Task.Run(async () =>
                {
                    await transitionEnded.Task;
                    return await region.PushAsync(NavigationTarget.Own(new Page("later")));
                });
            },
        };
        var result = await Wait(region.PushAsync(NavigationTarget.Own<Page>(reentrant)));
        transitionEnded.SetResult();
        Require(result is NavigationResult<Page>.Committed, $"The reentrant page's push gave {result}.");
        Require(inner is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Reentrant }
            && innerBack is NavigationResult<object>.Rejected { Reason: NavigationRejection.Reentrant },
            $"A hook's request to its own region gave {inner} and {innerBack}.");
        Require(siblingResult is NavigationResult<Page>.Committed, $"A hook's request to a sibling region gave {siblingResult}.");
        var later = await Wait(spawned!);
        Require(later is NavigationResult<Page>.Committed && Names(region) == "home,reentrant,later",
            $"Work spawned by a hook was falsely reentrant: {later}");
    }

    private static async Task AdmissionInsideTurnAsync()
    {
        await using var fixture = new Fixture();
        var context = fixture.Context;
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var hookInTurn = false;
        home.Guard = (_, _) =>
        {
            hookInTurn |= context.IsExecuting;
            return ValueTask.FromResult(true);
        };
        var next = new InitPage("next") { Initialize = (_, _) => { hookInTurn |= context.IsExecuting; return ValueTask.CompletedTask; } };
        Task<NavigationResult<Page>>? pending = null;
        var completedInTurn = true;
        await context.InvokeAsync(() =>
        {
            pending = region.PushAsync(NavigationTarget.Own<Page>(next)).AsTask();
            completedInTurn = pending.IsCompleted;
        });
        var continuationInTurn = true;
        var observed = pending!.ContinueWith(_ => continuationInTurn = context.IsExecuting, TaskContinuationOptions.ExecuteSynchronously);
        var result = await Wait(pending);
        await Wait(observed);
        Require(result is NavigationResult<Page>.Committed && !completedInTurn && !hookInTurn && !context.IsExecuting && !continuationInTurn,
            "Admission inside a turn ran a hook or a continuation in a turn.");
    }

    private static async Task TransitioningRaisedInTurnsAsync()
    {
        await using var fixture = new Fixture();
        var context = fixture.Context;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var home = new Page("home") { Guard = (_, _) => new ValueTask<bool>(gate.Task) };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var changes = new ConcurrentQueue<(string? Property, bool InTurn, bool Value)>();
        region.PropertyChanged += (_, args) => changes.Enqueue((args.PropertyName, context.IsExecuting, region.IsTransitioning));

        // Off-turn admission posts the change.
        var pending = region.PushAsync(NavigationTarget.Own(new Page("next"))).AsTask();
        await Until(() => changes.Any(change => change is ("IsTransitioning", true, true)), "IsTransitioning = true was not raised in a turn.");
        gate.SetResult(true);
        await Wait(pending);
        await Wait(fixture.Navigator.WhenIdleAsync());
        await Until(() => changes.Any(change => change is ("IsTransitioning", true, false)), "IsTransitioning = false was not raised in a turn.");
        Require(changes.All(change => change.InTurn), "A region change was raised outside a turn.");
        Require(changes.Any(change => change.Property == nameof(region.Current)) && changes.Any(change => change.Property == nameof(region.CanGoBack)),
            "The commit did not raise Current and CanGoBack.");

        // Admission inside a turn raises inline.
        while (changes.TryDequeue(out _)) { }
        var raisedInline = false;
        home.Guard = null;
        Task<NavigationResult<Page>>? inTurn = null;
        await context.InvokeAsync(() =>
        {
            inTurn = region.BackAsync().AsTask();
            raisedInline = changes.Any(change => change is ("IsTransitioning", true, true));
        });
        await Wait(inTurn!);
        Require(raisedInline, "Admission inside a turn did not raise IsTransitioning inline.");
    }

    private static async Task ThrowingHandlerDoesNotSkipOthersAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var later = 0;
        region.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(region.Current)) throw new InvalidOperationException("handler");
        };
        region.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(region.Current)) Interlocked.Increment(ref later);
        };
        var result = await Wait(region.PushAsync(NavigationTarget.Own(new Page("next"))));
        Require(result is NavigationResult<Page>.Committed && later == 1 && Names(region) == "home,next",
            $"A throwing handler skipped the next one or undid the commit: {result}, {later}.");
        var entry = fixture.Logs.Single(1063);
        Require(entry.Exception is InvalidOperationException && entry.State["Property"] as string == "Current", "1063 was not logged.");
    }

    // ---- Child regions ---------------------------------------------------

    private static async Task ChildPoliciesAsync()
    {
        foreach (var policy in new[] { NavigationChildRetention.Keep, NavigationChildRetention.ResetToRoot, NavigationChildRetention.Clear })
        {
            await using var fixture = new Fixture();
            var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
            ParentPage? parent = null;
            await Wait(region.PushAsync(NavigationTarget.Create<Page>(_ => parent = new ParentPage("parent", fixture.Navigator, policy))));
            var child = parent!.Child;
            var c1 = new Page("c1");
            var c2 = new Page("c2");
            await Wait(child.PushAsync(NavigationTarget.Own(c1)));
            await Wait(child.PushAsync(NavigationTarget.Own(c2)));
            var pushed = await Wait(region.PushAsync(NavigationTarget.Own(new Page("other"))));
            var expected = policy switch
            {
                NavigationChildRetention.Keep => "root,c1,c2",
                NavigationChildRetention.ResetToRoot => "root",
                _ => "",
            };
            Require(pushed is NavigationResult<Page>.Committed && Names(child) == expected
                && (c1.Disposed + c2.Disposed) == (policy == NavigationChildRetention.Keep ? 0 : 2) && parent.ChildRoot.Disposed == 0,
                $"{policy}: the child region was {Names(child)} after its parent was retained.");
            if (policy != NavigationChildRetention.Keep)
                Require(c2.Departures is [{ Kind: NavigationDepartureKind.Retire }] && c1.Departures is [.., { Kind: NavigationDepartureKind.Retire }],
                    $"{policy}: reset child entries were not guarded.");
            await Wait(region.BackAsync());
            Require(Names(child) == expected && parent.Disposed == 0, $"{policy}: going back changed the child region.");
        }
    }

    private static async Task ParentSupersedesChildTransitionsAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        ParentPage? parent = null;
        await Wait(region.PushAsync(NavigationTarget.Create<Page>(_ => parent = new ParentPage("parent", fixture.Navigator))));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        parent!.ChildRoot.Guard = async (departure, token) =>
        {
            if (departure.Kind != NavigationDepartureKind.Retain) return true;
            started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
            return true;
        };
        var childPage = new Page("child");
        var childPush = parent.Child.PushAsync(NavigationTarget.Own(childPage)).AsTask();
        await Wait(started.Task);
        var back = await Wait(region.BackAsync());
        var child = await Wait(childPush);
        Require(child is NavigationResult<Page>.Superseded && back is NavigationResult<Page>.Committed && parent.Disposed == 1 && childPage.Disposed == 1,
            $"The parent's admission did not supersede the child transition: {child}, {back}.");
        var closed = await Wait(parent.Child.PushAsync(NavigationTarget.Own(new Page("late"))));
        Require(closed is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed } && parent.Child.Current is null,
            $"A retired parent's child region accepted a request: {closed}.");
    }

    private static async Task ChildCommitSupersedesParentAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        ParentPage? parent = null;
        await Wait(region.PushAsync(NavigationTarget.Create<Page>(_ => parent = new ParentPage("parent", fixture.Navigator))));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        parent!.Guard = async (_, _) =>
        {
            started.TrySetResult();
            return await gate.Task;
        };
        var back = region.BackAsync().AsTask();
        await Wait(started.Task);
        var child = await Wait(parent.Child.PushAsync(NavigationTarget.Own(new Page("child"))));
        Require(child is NavigationResult<Page>.Committed, $"A child request admitted after its parent's gave {child}.");
        gate.SetResult(true);
        var parentResult = await Wait(back);
        Require(parentResult is NavigationResult<Page>.Superseded && Names(region) == "home,parent" && parent.Disposed == 0,
            $"A child commit during the parent's guard did not supersede the parent: {parentResult}.");
    }

    private static async Task RetirementDoesNotAwaitChildTransitionAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        ParentPage? parent = null;
        await Wait(region.PushAsync(NavigationTarget.Create<Page>(_ => parent = new ParentPage("parent", fixture.Navigator))));
        var parentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        parent!.Guard = async (_, _) =>
        {
            parentStarted.TrySetResult();
            return await parentGate.Task;
        };
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        parent.ChildRoot.Guard = async (departure, _) =>
        {
            if (departure.Kind != NavigationDepartureKind.Retain) return true;
            childStarted.TrySetResult();
            return await childGate.Task; // ignores its token
        };
        var back = region.BackAsync().AsTask();
        await Wait(parentStarted.Task);
        var pendingChild = new Page("pending");
        var childPush = parent.Child.PushAsync(NavigationTarget.Own(pendingChild)).AsTask();
        await Wait(childStarted.Task);
        parentGate.SetResult(true);
        var parentResult = await Wait(back);
        Require(parentResult is NavigationResult<Page>.Committed && parent.Disposed == 1 && !childPush.IsCompleted,
            $"The parent's retirement waited for a child transition: {parentResult}.");
        childGate.SetResult(true);
        var child = await Wait(childPush);
        Require(child is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed } && pendingChild.Disposed == 1,
            $"The closed child's transition gave {child} and did not retire its pending entry.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(fixture.Navigator.TrackedEntryCount == 1, $"{fixture.Navigator.TrackedEntryCount} entries remain tracked.");
    }

    private static async Task RetirementOrderAsync()
    {
        await using var fixture = new Fixture();
        var journal = new Journal();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        ParentPage? parent = null;
        await Wait(region.PushAsync(NavigationTarget.Create<Page>(_ => parent = new ParentPage("parent", fixture.Navigator, journal: journal))));
        var c0 = new Page("c0", journal);
        await Wait(parent!.Child.PushAsync(NavigationTarget.Own<Page>(c0)));
        ParentPage? c1 = null;
        await Wait(parent.Child.PushAsync(NavigationTarget.Create<Page>(_ => c1 = new ParentPage("c1", fixture.Navigator, journal: journal))));
        var g = new Page("g", journal);
        await Wait(c1!.Child.PushAsync(NavigationTarget.Own<Page>(g)));
        var back = await Wait(region.BackAsync());
        Require(journal.Text == "g,c1,c0,parent",
            $"Retirement order was {journal.Text}, not deepest first with the current entry before history.");
        Require(back is NavigationResult<Page>.Committed { Retired.Count: 6 } && new Page[] { parent, c0, c1, g }.All(page => page.Disposed == 1 && page.LeaseHeldAtDispose),
            "An entry was not disposed once before its lease was released.");
        Require(new object[] { parent, c0, c1, g }.All(page => !RunicModelContextRegistry.Shared.TryGet(page, out _)),
            "A retired entry kept its model-context lease.");
        Require(fixture.Navigator.TrackedEntryCount == 1, "Retired descendants are still tracked.");
    }

    private static async Task BorrowedContentIsNeverDisposedAsync()
    {
        await using var fixture = new Fixture();
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var borrowed = new ParentPage("borrowed", fixture.Navigator);
        await Wait(region.PushAsync(NavigationTarget.Borrow<Page>(borrowed)));
        await Wait(region.ResetAsync(NavigationTarget.Borrow(new Page("other"))));
        var childPush = await Wait(borrowed.Child.PushAsync(NavigationTarget.Own(new Page("child"))));
        Require(borrowed.Disposed == 0 && home.Disposed == 0 && childPush is NavigationResult<Page>.Committed,
            "Borrowed content was disposed or its child region was closed.");
        Require(!RunicModelContextRegistry.Shared.TryGet(borrowed, out _), "Borrowed content was bound to the model context.");
        await fixture.Navigator.DisposeAsync();
        Require(borrowed.Disposed == 0 && borrowed.ChildRoot.Disposed == 0, "Navigator disposal disposed borrowed content.");
    }

    private static async Task OwnershipChecksAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var owned = new Page("owned");
        await Wait(region.PushAsync(NavigationTarget.Own(owned)));
        Require(Throws<InvalidOperationException>(() => region.PushAsync(NavigationTarget.Own(owned)).AsTask()),
            "Owning a live owned instance again did not throw.");
        await Wait(region.BackAsync());
        Require(Throws<InvalidOperationException>(() => region.PushAsync(NavigationTarget.Own(owned)).AsTask()),
            "Owning a retired instance again did not throw.");
        Require(Throws<InvalidOperationException>(() => fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Own(owned))),
            "An initial target reused an owned instance.");
        var reused = await Wait(region.PushAsync(NavigationTarget.Create<Page>(_ => owned)));
        Require(reused is NavigationResult<Page>.Failed { Phase: NavigationPhase.Preparing, Error: InvalidOperationException } && owned.Disposed == 1,
            $"A factory returning an owned instance gave {reused}.");
    }

    private static async Task TargetValidationAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        Require(region.Current is null && region.CurrentEntry is null, "A region without an initial target was not empty.");
        Require(Throws<ArgumentException>(() => region.PushAsync(new ForeignTarget()).AsTask()), "A foreign target was accepted.");
        Require(Throws<ArgumentException>(() => fixture.Navigator.CreateRegion<Page>(new object(), new ForeignTarget())),
            "A foreign initial target was accepted.");
        Require(Throws<ArgumentException>(() => fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Borrow<Page>(new InitPage("init")))),
            "An initial target implementing INavigationInitialize was accepted.");
        Require(Throws<ArgumentException>(() => fixture.Navigator.CreateRegion<object>(new object(), NavigationTarget.Borrow<object>(new InputPage()))),
            "An initial target implementing INavigationInitialize<TInput> was accepted.");
        Require(Throws<ArgumentException>(() => fixture.Navigator.CreateRegion<InputPage>(new object(),
                NavigationTarget.Create<InputPage, string>(_ => new InputPage(), "input"))),
            "A Create target with input was accepted as an initial target.");
        var created = new InputPage();
        Require(Throws<ArgumentException>(() => fixture.Navigator.CreateRegion<object>(new object(), NavigationTarget.Create<object>(_ => created)))
            && created.Disposed == 1,
            "An initializable factory initial target was accepted or not disposed.");
    }

    private static async Task InitializePrecedenceAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<object>(fixture.Root);
        BothPage? withInput = null;
        await Wait(region.PushAsync(NavigationTarget.Create<BothPage, int>(_ => withInput = new BothPage(), 42)));
        Require(withInput is { Plain: 0, Input: 42 }, "A Create<T, TInput> entry did not call only the typed initialize.");
        var plain = new BothPage();
        await Wait(region.PushAsync(NavigationTarget.Own<object>(plain)));
        Require(plain is { Plain: 1, Input: 0 }, "An owned entry did not call only the untyped initialize.");
    }

    // ---- Disposal --------------------------------------------------------

    private static async Task DisposeMidTransitionAsync()
    {
        var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Own(new Page("home")));
        var committed = new Page("committed");
        await Wait(region.PushAsync(NavigationTarget.Own(committed)));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingPage = new InitPage("pending")
        {
            Initialize = async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            },
        };
        var pending = region.PushAsync(NavigationTarget.Own<Page>(pendingPage)).AsTask();
        await Wait(started.Task);
        await Wait(fixture.Navigator.DisposeAsync().AsTask());
        var result = await Wait(pending);
        Require(result is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed } && pendingPage.Disposed == 1
            && committed.Disposed == 1 && region.Current is null,
            $"Disposal mid-transition gave {result}.");
        Require(fixture.Navigator.TrackedEntryCount == 0 && committed.Disposed == 1 && !committed.DisposedInTurn,
            "Disposal left entries unretired.");
        var closed = await Wait(region.PushAsync(NavigationTarget.Own(new Page("late"))));
        Require(closed is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed }, "A disposed navigator admitted a request.");
        Require(Throws<ObjectDisposedException>(() => fixture.Navigator.CreateRegion<Page>(new object())), "A disposed navigator created a region.");
        await fixture.Context.DisposeAsync();
    }

    private static async Task DisposeWaitsForRunningCleanupAsync()
    {
        var fixture = new Fixture(closeTimeout: TimeSpan.FromMilliseconds(100));
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new Page("slow") { OnDispose = async () => { disposing.TrySetResult(); await gate.Task; } };
        var removed = new Page("removed");
        await Wait(region.PushAsync(NavigationTarget.Own(removed)));
        await Wait(region.PushAsync(NavigationTarget.Own(slow)));
        // Reset retires the current entry first; its disposal blocks while
        // `removed` waits as a removed-but-not-retired entry.
        var reset = region.ResetAsync(NavigationTarget.Borrow(new Page("root"))).AsTask();
        await Wait(disposing.Task);
        var dispose = fixture.Navigator.DisposeAsync().AsTask();
        await Until(() => removed.Disposed == 1, "DisposeAsync did not retire a removed entry.");
        Require(!dispose.IsCompleted, "DisposeAsync did not wait for running cleanup.");
        gate.SetResult();
        await Wait(dispose);
        var result = await Wait(reset);
        Require(result is NavigationResult<Page>.Committed && slow.Disposed == 1 && removed.Disposed == 1
            && fixture.Navigator.TrackedEntryCount == 0,
            $"Concurrent retirement paths disposed an entry other than once: {result}, {slow.Disposed}, {removed.Disposed}.");
        await fixture.Context.DisposeAsync();
    }

    private static async Task CleanupFailureIsLoggedAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var failing = new Page("failing") { OnDispose = () => throw new IOException("dispose") };
        await Wait(region.PushAsync(NavigationTarget.Own(failing)));
        var back = await Wait(region.BackAsync());
        Require(back is NavigationResult<Page>.Committed { Retired.Count: 1 } && failing.Disposed == 1
            && !RunicModelContextRegistry.Shared.TryGet(failing, out _) && fixture.Navigator.TrackedEntryCount == 1,
            $"A failing disposal stopped cleanup: {back}.");
        var entry = fixture.Logs.Single(1064);
        Require(entry.Exception is IOException && entry.State["Step"] as string == "Dispose" && entry.State["EntryType"] as string == "Page",
            "1064 did not carry the disposal failure.");
    }

    private static async Task OverrunIsLoggedAsync()
    {
        var time = new FakeTimeProvider();
        await using var fixture = new Fixture(time: time);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var home = new Page("home");
        home.Guard = async (_, _) =>
        {
            started.TrySetResult();
            return await gate.Task;
        };
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var first = region.PushAsync(NavigationTarget.Own(new Page("first"))).AsTask();
        await Wait(started.Task);
        home.Guard = null;
        var second = region.PushAsync(NavigationTarget.Own(new Page("second"))).AsTask();
        time.Advance(TimeSpan.FromSeconds(4));
        Require(!fixture.Logs.Has(1067, LogLevel.Warning), "1067 was logged before 5 seconds.");
        time.Advance(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(10));
        Require(fixture.Logs.Count(1067) == 1 && fixture.Logs.Single(1067).Level == LogLevel.Warning,
            "An overrunning superseded predecessor was not logged once as 1067.");
        Require(!second.IsCompleted, "The next request did not keep waiting for the overrunning predecessor.");
        gate.SetResult(true);
        Require(await Wait(first) is NavigationResult<Page>.Superseded && await Wait(second) is NavigationResult<Page>.Committed,
            "The overrun did not resolve.");
    }

    private static async Task ServiceRegistrationAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddRunicNavigation();
        await using var provider = services.BuildServiceProvider();
        RunicNavigator navigator;
        Page owned;
        await using (var scope = provider.CreateAsyncScope())
        {
            navigator = scope.ServiceProvider.GetRequiredService<RunicNavigator>();
            var context = scope.ServiceProvider.GetRequiredService<IRunicModelContext>();
            Require(ReferenceEquals(navigator, scope.ServiceProvider.GetRequiredService<RunicNavigator>()), "The navigator is not scoped.");
            IServiceProvider? seen = null;
            var region = navigator.CreateRegion<Page>(new object());
            owned = new Page("owned");
            await Wait(region.PushAsync(NavigationTarget.Create<Page>(window => { seen = window; return owned; })));
            Require(ReferenceEquals(seen, scope.ServiceProvider), "A factory did not receive the window provider.");
            Require(RunicModelContextRegistry.Shared.GetRequired(owned) == context, "Owned content was not bound to the scoped context.");
            owned.Context = context;
        }
        Require(owned.Disposed == 1 && owned.ContextAliveAtDispose, "The scope did not dispose the navigator before its context.");
        Require(Throws<ObjectDisposedException>(() => navigator.CreateRegion<Page>(new object())), "The scope did not dispose the navigator.");
    }

    // ---- PushForResult (W230-003) ------------------------------------------

    private static async Task ResultCompletedAsync()
    {
        await using var fixture = new Fixture();
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var picker = new InitPage("picker");
        var request = region.PushForResult<string>(NavigationTarget.Own<Page>(picker));
        var pushed = await Wait(request.Transition);
        Require(pushed is NavigationResult<Page>.Committed { Current.Content: var current } && current == picker
            && await Wait(request.Transition) is NavigationResult<Page>.Committed,
            $"PushForResult did not commit, or its transition could not be awaited twice: {pushed}");
        Require(!request.Completion.IsCompleted, "The completion ended before the entry completed.");

        // The completion is set after the commit turn of the return, never inside it.
        var completedInTurn = false;
        region.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(region.Current)) completedInTurn |= request.Completion.IsCompleted;
        };
        var back = await Wait(picker.Entry!.CompleteAsync("chosen"));
        Require(back is NavigationResult<object>.Committed { Current.Content: var resumed, Retired: [var retired] } && resumed == home
            && retired == picker.Entry.Id, $"CompleteAsync did not go back: {back}");
        var completion = await Wait(request.Completion);
        Require(completion is NavigationCompletion<string>.Completed { Value: "chosen" } && !completedInTurn,
            $"The request ended {completion}; completed inside the commit turn: {completedInTurn}.");
        Require(picker.Disposed == 1 && home.Resumed == 1 && region.Current == home && fixture.Logs.Count(1070) == 0,
            "Completing did not retire the result entry and resume the previous one.");
        var again = await Wait(picker.Entry.CompleteAsync("again"));
        Require(again is NavigationResult<object>.Rejected { Reason: NavigationRejection.NotCurrent } && fixture.Logs.Count(1071) == 0,
            $"A second CompleteAsync gave {again}.");
    }

    // A result entry pushed onto an empty region (an in-page dialog) returns to the empty state.
    private static async Task ResultFromEmptyRegionAsync()
    {
        await using var fixture = new Fixture();
        var dialog = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        var confirm = new InitPage("confirm");
        var request = dialog.PushForResult<bool>(NavigationTarget.Own<Page>(confirm));
        Require(await Wait(request.Transition) is NavigationResult<Page>.Committed && dialog.Current == confirm && !dialog.CanGoBack,
            "PushForResult into an empty region did not commit.");
        var plainBack = await Wait(dialog.BackAsync());
        Require(plainBack is NavigationResult<Page>.Rejected { Reason: NavigationRejection.NoHistory } && dialog.Current == confirm,
            $"A plain Back from the only entry gave {plainBack}.");
        var back = await Wait(confirm.Entry!.CompleteAsync(true));
        Require(back is NavigationResult<object>.Committed { Current: null } && dialog.Current is null && dialog.CurrentEntry is null,
            $"Completing the only entry did not empty the region: {back}");
        Require(await Wait(request.Completion) is NavigationCompletion<bool>.Completed { Value: true } && confirm.Disposed == 1
            && fixture.Navigator.TrackedEntryCount == 0, "The result was not delivered from the emptied region.");
    }

    private static async Task ResultDismissedWhenNotCommittedAsync()
    {
        await using var fixture = new Fixture();
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));

        var stale = region.PushForResult<int>(NavigationTarget.Own(new Page("stale")), new NavigationRequestOptions(new NavigationEntryId(99)));
        Require(stale.Completion.IsCompleted && await Wait(stale.Transition) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.NotCurrent }
            && await Wait(stale.Completion) is NavigationCompletion<int>.Dismissed, "A request rejected at admission was not dismissed.");

        home.Guard = (_, _) => ValueTask.FromResult(false);
        var vetoedPage = new Page("vetoed");
        var vetoed = region.PushForResult<int>(NavigationTarget.Own(vetoedPage));
        Require(await Wait(vetoed.Transition) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard }
            && await Wait(vetoed.Completion) is NavigationCompletion<int>.Dismissed && vetoedPage.Disposed == 1,
            "A vetoed request was not dismissed.");
        home.Guard = null;

        var failing = new InitPage("failing") { Initialize = (_, _) => throw new InvalidOperationException("initialize") };
        var failed = region.PushForResult<int>(NavigationTarget.Own<Page>(failing));
        Require(await Wait(failed.Transition) is NavigationResult<Page>.Failed { Phase: NavigationPhase.Preparing }
            && await Wait(failed.Completion) is NavigationCompletion<int>.Dismissed && failing.Disposed == 1,
            "A failed request was not dismissed.");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new InitPage("slow")
        {
            Initialize = async (_, token) =>
            {
                started.TrySetResult();
                await never.Task.WaitAsync(token);
            },
        };
        var superseded = region.PushForResult<int>(NavigationTarget.Own<Page>(slow));
        await Wait(started.Task);
        var later = await Wait(region.PushAsync(NavigationTarget.Own(new Page("later"))));
        Require(later is NavigationResult<Page>.Committed && await Wait(superseded.Transition) is NavigationResult<Page>.Superseded
            && await Wait(superseded.Completion) is NavigationCompletion<int>.Dismissed && slow.Disposed == 1,
            $"A superseded request was not dismissed: {later}.");

        var cancelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = new InitPage("blocking")
        {
            Initialize = async (_, token) =>
            {
                cancelStarted.TrySetResult();
                await never.Task.WaitAsync(token);
            },
        };
        using var cancel = new CancellationTokenSource();
        var cancelled = region.PushForResult<int>(NavigationTarget.Own<Page>(blocking), cancellationToken: cancel.Token);
        await Wait(cancelStarted.Task);
        await cancel.CancelAsync();
        Require(await Wait(cancelled.Transition) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Cancelled }
            && await Wait(cancelled.Completion) is NavigationCompletion<int>.Dismissed && blocking.Disposed == 1,
            "A request cancelled before its commit was not dismissed.");

        var dismissals = fixture.Logs.All(1070).ToArray();
        Require(dismissals.Length == 5 && dismissals.All(entry => entry.Level == LogLevel.Debug
            && entry.State["Reason"]?.ToString() == "NotCommitted" && entry.State["Region"] as string == "Page" && entry.State["RegionId"] is 1),
            $"Expected five 1070 NotCommitted entries, got {dismissals.Length}.");
    }

    private static async Task ResultDismissedOnRetirementAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));

        // A plain Back retires the entry without completing it.
        var first = new InitPage("first");
        var request = region.PushForResult<string>(NavigationTarget.Own<Page>(first));
        await Wait(request.Transition);
        Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Committed
            && await Wait(request.Completion) is NavigationCompletion<string>.Dismissed && first.Disposed == 1,
            "An entry that retired without completing did not dismiss its request.");
        Require(fixture.Logs.Single(1070).State["Reason"]?.ToString() == "Retired", "1070 did not report the retirement.");

        // A retained result entry can't complete, and dismisses when it retires.
        var retained = new InitPage("retained");
        var kept = region.PushForResult<string>(NavigationTarget.Own<Page>(retained));
        await Wait(kept.Transition);
        await Wait(region.PushAsync(NavigationTarget.Own(new Page("top"))));
        var notCurrent = await Wait(retained.Entry!.CompleteAsync("x"));
        Require(notCurrent is NavigationResult<object>.Rejected { Reason: NavigationRejection.NotCurrent } && !kept.Completion.IsCompleted,
            $"A retained result entry completed: {notCurrent}.");
        Require(await Wait(region.ClearHistoryAsync()) is NavigationResult<Page>.Committed
            && await Wait(kept.Completion) is NavigationCompletion<string>.Dismissed && retained.Disposed == 1,
            "ClearHistory did not dismiss the retained result entry's request.");
    }

    private static async Task ResultTypeChecksAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var plain = new InitPage("plain");
        await Wait(region.PushAsync(NavigationTarget.Own<Page>(plain)));
        Require(Throws<InvalidOperationException>(() => _ = plain.Entry!.CompleteAsync(1).AsTask()) && region.Current == plain,
            "CompleteAsync on an entry pushed without PushForResult did not throw.");

        var typed = new InitPage("typed");
        var request = region.PushForResult<string>(NavigationTarget.Own<Page>(typed));
        await Wait(request.Transition);
        Require(Throws<InvalidOperationException>(() => _ = typed.Entry!.CompleteAsync(42).AsTask()) && region.Current == typed && !request.Completion.IsCompleted,
            "CompleteAsync with the wrong result type did not throw.");

        // A request for a base type takes a derived result.
        var general = new InitPage("general");
        var any = region.PushForResult<object>(NavigationTarget.Own<Page>(general));
        await Wait(any.Transition);
        Require(await Wait(general.Entry!.CompleteAsync("text")) is NavigationResult<object>.Committed
            && await Wait(any.Completion) is NavigationCompletion<object>.Completed { Value: "text" },
            "A request for object did not take a string result.");
        Require(await Wait(typed.Entry!.CompleteAsync("ok")) is NavigationResult<object>.Committed
            && await Wait(request.Completion) is NavigationCompletion<string>.Completed { Value: "ok" } && region.Current == plain,
            "The typed request did not complete after the type error.");
    }

    // A vetoed return leaves the entry current and the request open, so CompleteAsync can be repeated.
    private static async Task ResultCompleteRejectedKeepsRequestOpenAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var veto = true;
        var picker = new InitPage("picker");
        picker.Guard = (departure, _) => ValueTask.FromResult(!(veto && departure.Kind == NavigationDepartureKind.Retire));
        var request = region.PushForResult<int>(NavigationTarget.Own<Page>(picker));
        await Wait(request.Transition);
        var rejected = await Wait(picker.Entry!.CompleteAsync(1));
        Require(rejected is NavigationResult<object>.Rejected { Reason: NavigationRejection.Guard } && region.Current == picker
            && !request.Completion.IsCompleted && picker.Disposed == 0,
            $"A vetoed CompleteAsync gave {rejected} or ended the request.");
        veto = false;
        Require(await Wait(picker.Entry.CompleteAsync(2)) is NavigationResult<object>.Committed
            && await Wait(request.Completion) is NavigationCompletion<int>.Completed { Value: 2 },
            "A repeated CompleteAsync did not deliver its value.");
    }

    private static async Task ResultCallerCancelledAfterCommitAsync()
    {
        await using var fixture = new Fixture();
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var guarding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new InitPage("picker");
        picker.Guard = async (departure, _) =>
        {
            if (departure.Kind != NavigationDepartureKind.Retire) return true;
            guarding.TrySetResult();
            return await release.Task;
        };
        using var cancel = new CancellationTokenSource();
        var request = region.PushForResult<int>(NavigationTarget.Own<Page>(picker), cancellationToken: cancel.Token);
        Require(await Wait(request.Transition) is NavigationResult<Page>.Committed, "The result push did not commit.");

        await cancel.CancelAsync();
        // Dismissed at once, while the back transition it issued is still guarding.
        Require(request.Completion is { IsCompletedSuccessfully: true, Result: NavigationCompletion<int>.Dismissed },
            "Cancelling the caller's token after the commit did not dismiss the request at once.");
        await Wait(guarding.Task);
        Require(region.Current == picker && region.IsTransitioning, "The issued back transition did not run the entry's guard.");
        release.SetResult(true);
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(region.Current == home && picker.Disposed == 1
            && picker.Departures is [{ Kind: NavigationDepartureKind.Retire, Operation: NavigationOperation.Back }],
            "Cancelling the caller's token did not go back from the result entry.");
        Require(fixture.Logs.Single(1070).State["Reason"]?.ToString() == "Cancelled", "1070 did not report the cancellation.");
    }

    // When the issued back is rejected, the entry stays; a later CompleteAsync still goes back and drops its value.
    private static async Task ResultCallerCancelledBackRejectedAsync()
    {
        await using var fixture = new Fixture();
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var veto = true;
        var picker = new InitPage("picker");
        picker.Guard = (departure, _) => ValueTask.FromResult(!(veto && departure.Kind == NavigationDepartureKind.Retire));
        using var cancel = new CancellationTokenSource();
        var request = region.PushForResult<int>(NavigationTarget.Own<Page>(picker), cancellationToken: cancel.Token);
        await Wait(request.Transition);
        await cancel.CancelAsync();
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(await Wait(request.Completion) is NavigationCompletion<int>.Dismissed && region.Current == picker && picker.Disposed == 0,
            "A rejected back after cancellation did not leave the entry current.");
        Require(fixture.Logs.All(1065).Any(entry => Equals(entry.State["Operation"], NavigationOperation.Back)
            && Equals(entry.State["Reason"], NavigationRejection.Guard)), "The rejected back was not logged as 1065.");

        veto = false;
        var late = await Wait(picker.Entry!.CompleteAsync(7));
        Require(late is NavigationResult<object>.Committed && region.Current == home && picker.Disposed == 1
            && request.Completion.Result is NavigationCompletion<int>.Dismissed && fixture.Logs.Count(1071) == 1,
            $"A late CompleteAsync after dismissal gave {late}; the value was not dropped.");
    }

    // The caller cancels, then at once pushes into the same region, as a superseded guard that
    // runs again does. The return Back is admitted before the dismissal is observable, so the
    // push supersedes it or follows it; either way the dismissed entry is not retained.
    private static async Task ResultCallerCancelledThenPushRetiresAsync()
    {
        for (var round = 0; round < 50; round++)
        {
            await using var fixture = new Fixture();
            var dialog = fixture.Navigator.CreateRegion<Page>(fixture.Root);
            var first = new InitPage("first");
            using var cancel = new CancellationTokenSource();
            var request = dialog.PushForResult<bool>(NavigationTarget.Own<Page>(first), cancellationToken: cancel.Token);
            await Wait(request.Transition);
            var observed = request.Completion.ContinueWith(_ =>
                dialog.PushForResult<bool>(NavigationTarget.Own<Page>(new InitPage("second"))), TaskScheduler.Default);
            await cancel.CancelAsync();
            var second = await Wait(observed);
            Require(await Wait(second.Transition) is NavigationResult<Page>.Committed, "The second push did not commit.");
            await Wait(fixture.Navigator.WhenIdleAsync());
            Require(dialog.Current is InitPage { Name: "second" } && dialog.History.Count == 0 && first.Disposed == 1,
                $"Round {round}: the dismissed entry was retained ({dialog.History.Count} below the current entry).");
        }
    }

    private static async Task ResultDismissedOnCloseAsync()
    {
        var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var active = new InitPage("active");
        var committed = region.PushForResult<int>(NavigationTarget.Own<Page>(active));
        await Wait(committed.Transition);
        var other = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparing = new InitPage("preparing")
        {
            Initialize = async (_, token) =>
            {
                started.TrySetResult();
                await never.Task.WaitAsync(token);
            },
        };
        var pending = other.PushForResult<int>(NavigationTarget.Own<Page>(preparing));
        await Wait(started.Task);

        await Wait(fixture.Navigator.DisposeAsync().AsTask());
        Require(committed.Completion.Result is NavigationCompletion<int>.Dismissed && pending.Completion.Result is NavigationCompletion<int>.Dismissed
            && await Wait(pending.Transition) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed },
            "Closing the navigator did not dismiss its open requests.");
        Require(active.Disposed == 1 && preparing.Disposed == 1 && fixture.Navigator.TrackedEntryCount == 0
            && fixture.Logs.All(1070).Count(entry => entry.State["Reason"]?.ToString() == "Closed") == 2,
            "Closing did not retire the result entries.");
        var late = region.PushForResult<int>(NavigationTarget.Own(new Page("late")));
        Require(await Wait(late.Transition) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed }
            && await Wait(late.Completion) is NavigationCompletion<int>.Dismissed, "A closed navigator did not dismiss a new request.");
        await fixture.Context.DisposeAsync();
    }

    // The Notes pattern: a departure guard awaits a confirm pushed for a result into a sibling
    // region, passing its own token. Supersession and window close unblock it.
    private static async Task SiblingGuardAwaitsResultAsync(bool close)
    {
        var fixture = new Fixture();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var dialog = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        var document = new Page("document");
        await Wait(main.PushAsync(NavigationTarget.Own(document)));
        NavigationResultRequest<Page, bool>? confirm = null;
        InitPage? confirmPage = null;
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answered = new TaskCompletionSource<NavigationCompletion<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        document.Guard = async (departure, token) =>
        {
            if (departure.Kind != NavigationDepartureKind.Retire) return true;
            confirmPage = new InitPage("confirm");
            confirm = dialog.PushForResult<bool>(NavigationTarget.Own<Page>(confirmPage), cancellationToken: token);
            shown.TrySetResult();
            var answer = await confirm.Completion;
            answered.TrySetResult(answer);
            return answer is NavigationCompletion<bool>.Completed { Value: true };
        };
        var back = main.BackAsync().AsTask();
        await Wait(shown.Task);
        Require(await Wait(confirm!.Transition) is NavigationResult<Page>.Committed && dialog.Current == confirmPage,
            "The guard's confirm was not pushed into the sibling region.");

        if (close)
        {
            var disposal = fixture.Navigator.DisposeAsync().AsTask();
            Require(await Wait(answered.Task) is NavigationCompletion<bool>.Dismissed, "Closing did not unblock the guard.");
            await Wait(disposal);
            Require(await Wait(back) is not NavigationResult<Page>.Committed && document.Disposed == 1 && confirmPage!.Disposed == 1
                && fixture.Navigator.TrackedEntryCount == 0, "Closing during the guard did not retire everything.");
            await fixture.Context.DisposeAsync();
            return;
        }

        // A later request supersedes the guarded Back; the guard's token dismisses the confirm.
        var other = new Page("other");
        var push = main.PushAsync(NavigationTarget.Own(other)).AsTask();
        Require(await Wait(answered.Task) is NavigationCompletion<bool>.Dismissed, "Supersession did not unblock the guard.");
        var backResult = await Wait(back);
        Require(backResult is not NavigationResult<Page>.Committed, $"The superseded Back gave {backResult}.");
        Require(await Wait(push) is NavigationResult<Page>.Committed && main.Current == other && document.Disposed == 0,
            "The superseding push did not commit.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(dialog.Current is null && confirmPage!.Disposed == 1, "The dismissed confirm did not leave the dialog region.");
        await fixture.DisposeAsync();
    }

    // Seeded race of result pushes, completions, caller cancellations, plain backs, pushes and
    // clears. Every completion ends; Completed only with the value of a CompleteAsync whose return
    // committed, which wins unless the caller cancelled; at most one return commits per entry;
    // result pages are disposed at most once; closing ends every request and retires every entry.
    private static async Task ResultRaceAsync(int seed, bool disposeDuringRace = false)
    {
        var master = new Random(seed);
        var violations = new ConcurrentQueue<string>();
        var fixture = new Fixture();
        var home = new Page("home");
        NavigationRegion<Page>[] regions =
        [
            fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home)),
            fixture.Navigator.CreateRegion<Page>(fixture.Root),
        ];
        var requests = new ConcurrentQueue<(InitPage Page, NavigationResultRequest<Page, int> Request, CancellationTokenSource Cancel)>();
        var returned = new ConcurrentDictionary<InitPage, ConcurrentQueue<int>>();
        var workerSeeds = Enumerable.Range(0, 4).Select(_ => master.Next()).ToArray();
        var committed = 0;

        async Task WorkerAsync(int worker)
        {
            var random = new Random(workerSeeds[worker]);
            for (var step = 0; step < 120; step++)
            {
                var region = regions[random.Next(regions.Length)];
                var choice = random.Next(10);
                if (choice < 4)
                {
                    var page = new InitPage($"result{worker}.{step}");
                    var cancel = new CancellationTokenSource();
                    if (random.Next(3) == 0) cancel.CancelAfter(random.Next(3));
                    var request = region.PushForResult<int>(NavigationTarget.Own<Page>(page), cancellationToken: cancel.Token);
                    requests.Enqueue((page, request, cancel));
                    if (random.Next(2) == 0 && await Wait(request.Transition) is NavigationResult<Page>.Committed)
                        Interlocked.Increment(ref committed);
                }
                else if (choice < 7)
                {
                    var open = requests.ToArray();
                    if (open.Length == 0) continue;
                    // Mostly recent requests, whose entries are likely still current.
                    var (page, _, _) = open[Math.Max(0, open.Length - 1 - random.Next(Math.Min(open.Length, 4)))];
                    if (page.Entry is not { } entry) continue;
                    var value = random.Next();
                    if (await Wait(entry.CompleteAsync(value)) is NavigationResult<object>.Committed)
                        returned.GetOrAdd(page, _ => new()).Enqueue(value);
                }
                else if (choice < 8) await Wait(region.BackAsync());
                else if (choice < 9) await Wait(region.PushAsync(NavigationTarget.Own(new Page("plain"))));
                else await Wait(region.ClearAsync());
                var pause = random.Next(6);
                if (pause == 0) await Task.Yield();
                else if (pause == 1) await Task.Delay(1);
            }
        }

        var workers = Enumerable.Range(0, 4).Select(worker => Task.Run(() => WorkerAsync(worker))).ToList();
        if (disposeDuringRace)
        {
            workers.Add(Task.Run(async () =>
            {
                for (var waited = 0; Volatile.Read(ref committed) < 4 && waited < 5000; waited++) await Task.Delay(1);
                await fixture.Navigator.DisposeAsync().AsTask();
            }));
        }
        await Wait(Task.WhenAll(workers));
        if (!disposeDuringRace)
        {
            // After the race the navigator still delivers a result.
            await Wait(fixture.Navigator.WhenIdleAsync());
            var last = new InitPage("last");
            var final = regions[1].PushForResult<int>(NavigationTarget.Own<Page>(last));
            await Wait(final.Transition);
            Require(await Wait(last.Entry!.CompleteAsync(-1)) is NavigationResult<object>.Committed
                && await Wait(final.Completion) is NavigationCompletion<int>.Completed { Value: -1 },
                $"seed {seed}: a result after the race was not delivered.");
        }
        await Wait(fixture.Navigator.DisposeAsync().AsTask());
        await fixture.Context.DisposeAsync();

        foreach (var (page, request, cancel) in requests)
        {
            if (!request.Completion.IsCompleted)
            {
                violations.Enqueue($"seed {seed}: {page.Name}'s request was still open after the navigator closed");
                continue;
            }
            var values = returned.TryGetValue(page, out var queue) ? queue.ToArray() : [];
            if (values.Length > 1) violations.Enqueue($"seed {seed}: {values.Length} returns committed from {page.Name}");
            var transition = await Wait(request.Transition);
            switch (request.Completion.Result)
            {
                case NavigationCompletion<int>.Completed { Value: var value } when !values.Contains(value):
                    violations.Enqueue($"seed {seed}: {page.Name} completed with {value}, which no committed return carried");
                    break;
                case NavigationCompletion<int>.Completed when transition is not NavigationResult<Page>.Committed:
                    violations.Enqueue($"seed {seed}: {page.Name} completed although its push gave {transition}");
                    break;
                case NavigationCompletion<int>.Dismissed when values.Length == 1 && !cancel.IsCancellationRequested && !disposeDuringRace:
                    violations.Enqueue($"seed {seed}: {page.Name}'s committed return was dropped without a cancellation");
                    break;
            }
            if (page.Disposed > 1 || (transition is NavigationResult<Page>.Committed && page.Disposed != 1))
                violations.Enqueue($"seed {seed}: {page.Name} was disposed {page.Disposed} times");
            cancel.Dispose();
        }
        if (fixture.Navigator.TrackedEntryCount != 0)
            violations.Enqueue($"seed {seed}: {fixture.Navigator.TrackedEntryCount} entries were left unretired");
        if (home.Disposed != 0) violations.Enqueue($"seed {seed}: the borrowed home was disposed");
        Require(violations.IsEmpty, string.Join(Environment.NewLine, violations.Take(10)));
    }

    // ---- Randomized race -------------------------------------------------

    // disposeDuringRace disposes the navigator while the workers are still running,
    // with a zero CloseTimeout, so retirement overlaps admitted transitions.
    private static async Task RandomizedRaceAsync(int seed, bool disposeDuringRace = false, bool patient = false)
    {
        RacePage.Calm = false;
        var master = new Random(seed);
        // A zero CloseTimeout lets disposal overrun an initialize hook that already started, by design;
        // only the patient and non-disposing variants require that initialize never overlaps disposal.
        var state = new RaceState(seed) { StrictInit = !disposeDuringRace || patient };
        var violations = state.Violations;
        var created = new ConcurrentQueue<RacePage>();
        var pool = Enumerable.Range(0, 4).Select(index => new RacePage($"borrowed{index}", state)).ToArray();
        var fixture = new Fixture(closeTimeout: disposeDuringRace && !patient ? TimeSpan.Zero : null);
        var region = fixture.Navigator.CreateRegion<RacePage>(fixture.Root, NavigationTarget.Borrow(pool[0]));
        var childRegions = new ConcurrentQueue<NavigationRegion<RacePage>>();
        var checking = true;
        var committed = 0;
        // Drawn before any task starts: System.Random is not thread-safe.
        var workerSeeds = Enumerable.Range(0, 4).Select(_ => master.Next()).ToArray();
        var disposeAfterCommits = 1 + master.Next(8);

        // A retired entry must never become current, on the root or on any child region.
        void Watch(NavigationRegion<RacePage> observed)
        {
            observed.PropertyChanged += (_, args) =>
            {
                if (!checking || args.PropertyName != nameof(observed.CurrentEntry)) return;
                if (observed.CurrentEntry is { } current && (current.State != NavigationEntryState.Active || current.Content.Disposed > 0))
                    violations.Enqueue($"seed {seed}: retired entry {current} became current");
            };
        }
        Watch(region);

        RacePage Create(Random random, NavigationRegion<RacePage> origin)
        {
            var page = new InitRacePage($"page{created.Count}", state)
            {
                Behavior = random.Next(20),
                Delay = random.Next(3),
                Origin = origin,
            };
            if (random.Next(3) == 0)
            {
                var policy = (NavigationChildRetention)random.Next(3);
                page.Child = fixture.Navigator.CreateRegion<RacePage>(page, NavigationTarget.Borrow(pool[random.Next(pool.Length)]),
                    new NavigationRegionOptions(policy));
                childRegions.Enqueue(page.Child);
                Watch(page.Child);
            }
            created.Enqueue(page);
            return page;
        }

        async Task WorkerAsync(int worker)
        {
            var random = new Random(workerSeeds[worker]);
            for (var step = 0; step < 150; step++)
            {
                var target = region;
                if (random.Next(4) == 0 && !childRegions.IsEmpty)
                    target = childRegions.ElementAt(random.Next(childRegions.Count));
                using var cancel = new CancellationTokenSource();
                if (random.Next(6) == 0) cancel.CancelAfter(random.Next(3));
                var expected = random.Next(5) == 0 ? target.CurrentEntry?.Id : null;
                var options = new NavigationRequestOptions(expected);
                var choice = random.Next(10);
                var pageRandom = new Random(random.Next());
                INavigationTarget<RacePage> NewTarget() => random.Next(4) == 0
                    ? NavigationTarget.Borrow(pool[random.Next(pool.Length)])
                    : NavigationTarget.Create(_ => Create(pageRandom, target));
                var operation = choice switch
                {
                    < 4 => target.PushAsync(NewTarget(), options, cancel.Token),
                    < 6 => target.BackAsync(options, cancel.Token),
                    6 => target.History is { Count: > 0 } history
                        ? target.BackToAsync(history[random.Next(history.Count)].Id, options, cancel.Token)
                        : target.BackAsync(options, cancel.Token),
                    7 => target.ReplaceAsync(NewTarget(), options, cancel.Token),
                    8 => random.Next(2) == 0 ? target.ResetAsync(NewTarget(), options, cancel.Token) : target.ClearHistoryAsync(options, cancel.Token),
                    _ => target.ClearAsync(options, cancel.Token),
                };
                var result = await operation.AsTask().WaitAsync(Timeout);
                if (result is NavigationResult<RacePage>.Failed { Error: not RaceException } failed)
                    violations.Enqueue($"seed {seed}: unexpected failure {failed.Error}");
                if (result is NavigationResult<RacePage>.Committed) Interlocked.Increment(ref committed);
                var pause = random.Next(8);
                if (pause == 0) await Task.Yield();
                else if (pause == 1) await Task.Delay(1);
            }
        }

        var workers = Enumerable.Range(0, 4).Select(worker => Task.Run(() => WorkerAsync(worker))).ToList();
        if (disposeDuringRace)
        {
            // Disposal races the admitted transitions and their retirements.
            workers.Add(Task.Run(async () =>
            {
                // Dispose once some transitions committed, bounded so a quiet run still ends.
                for (var waited = 0; Volatile.Read(ref committed) < disposeAfterCommits && waited < 5000; waited++)
                    await Task.Delay(1);
                await fixture.Navigator.DisposeAsync().AsTask();
            }));
        }
        await Wait(Task.WhenAll(workers));

        if (!disposeDuringRace)
        {
            await Wait(fixture.Navigator.WhenIdleAsync());

            // History stays coherent: ids increase bottom to top, the top is active.
            foreach (var observed in childRegions.Append(region))
            {
                var stack = observed.History.Append(observed.CurrentEntry).OfType<NavigationEntry<RacePage>>().ToArray();
                for (var index = 0; index < stack.Length; index++)
                {
                    var expectedState = index == stack.Length - 1 ? NavigationEntryState.Active : NavigationEntryState.Retained;
                    if (stack[index].State != expectedState || stack[index].Content.Disposed > 0
                        || (index > 0 && stack[index].Id.Value <= stack[index - 1].Id.Value))
                        violations.Enqueue($"seed {seed}: incoherent history {string.Join(",", stack.Select(entry => entry.ToString()))}");
                }
                if (observed.IsTransitioning) violations.Enqueue($"seed {seed}: a region is still transitioning when idle");
            }

            // Admission always recovers.
            RacePage.Calm = true;
            var final = await Wait(region.ResetAsync(NavigationTarget.Create(_ => Create(new Random(seed), region))));
            if (final is not NavigationResult<RacePage>.Committed) violations.Enqueue($"seed {seed}: admission did not recover: {final}");
        }

        checking = false;
        await Wait(fixture.Navigator.DisposeAsync().AsTask());
        await fixture.Context.DisposeAsync();
        if (fixture.Navigator.TrackedEntryCount != 0)
            violations.Enqueue($"seed {seed}: {fixture.Navigator.TrackedEntryCount} entries were left unretired");
        foreach (var page in created)
        {
            if (page.Disposed != 1) violations.Enqueue($"seed {seed}: {page.Name} was disposed {page.Disposed} times");
            // Every lease bound during the race is released by the time the navigator is gone.
            if (RunicModelContextRegistry.Shared.TryGet(page, out _))
                violations.Enqueue($"seed {seed}: {page.Name} kept its model-context lease after disposal");
        }
        foreach (var page in pool)
            if (page.Disposed != 0) violations.Enqueue($"seed {seed}: borrowed {page.Name} was disposed");
        Require(violations.IsEmpty, string.Join(Environment.NewLine, violations.Take(10)));
        if (!disposeDuringRace)
            Require(committed >= 3 && !created.IsEmpty && state.GuardsRun > 0,
                $"seed {seed}: the race committed only {committed} transitions, created {created.Count} pages and ran {state.GuardsRun} guards.");
    }

    // ---- Helpers ---------------------------------------------------------

    private static string Names<T>(NavigationRegion<T> region) where T : Page =>
        string.Join(",", region.History.Append(region.CurrentEntry).OfType<NavigationEntry<T>>().Select(entry => entry.Content.Name));

    private static async Task<T> Wait<T>(ValueTask<T> task) => await task.AsTask().WaitAsync(Timeout);

    private static async Task<T> Wait<T>(Task<T> task) => await task.WaitAsync(Timeout);

    private static async Task Wait(Task task) => await task.WaitAsync(Timeout);

    private static async Task Wait(ValueTask task) => await task.AsTask().WaitAsync(Timeout);

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException(message);
            await Task.Delay(5);
        }
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture(IRunicModelContext? context = null, TimeProvider? time = null, TimeSpan? closeTimeout = null)
        {
            Context = context ?? new RunicModelContext();
            Navigator = new RunicNavigator(new RunicNavigatorOptions
            {
                ModelContext = Context,
                LoggerFactory = Logs,
                TimeProvider = time,
                CloseTimeout = closeTimeout ?? TimeSpan.FromSeconds(10),
            });
        }

        public IRunicModelContext Context { get; }
        public LogCapture Logs { get; } = new();
        public RunicNavigator Navigator { get; }
        public object Root { get; } = new();

        public async ValueTask DisposeAsync()
        {
            await Navigator.DisposeAsync();
            await Context.DisposeAsync();
        }
    }

    // Holds the navigator's commit turn until opened, to act between Preparing and Committing.
    private sealed class GatedContext : IRunicModelContext
    {
        private TaskCompletionSource _open = CompletedGate();

        public RunicModelContext Inner { get; } = new();
        public TaskCompletionSource Waiting { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsExecuting => Inner.IsExecuting;

        public event Action<Exception>? UnhandledTurnException
        {
            add => Inner.UnhandledTurnException += value;
            remove => Inner.UnhandledTurnException -= value;
        }

        public void Close()
        {
            Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Open() => _open.TrySetResult();

        public bool TryPost(Action turn) => Inner.TryPost(turn);

        // Requests made through the Action overload (the close's clearing turn).
        public int ActionRequests => Volatile.Read(ref _actionRequests);
        private int _actionRequests;

        public ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _actionRequests);
            return _open.Task.IsCompleted || IsExecuting ? Inner.InvokeAsync(turn, cancellationToken) : new(GatedAsync(turn, cancellationToken));
        }

        public ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default) =>
            _open.Task.IsCompleted || IsExecuting ? Inner.InvokeAsync(turn, cancellationToken) : new(GatedAsync(turn, cancellationToken));

        public ValueTask DisposeAsync() => Inner.DisposeAsync();

        private async Task GatedAsync(Action turn, CancellationToken cancellationToken)
        {
            Waiting.TrySetResult();
            await _open.Task;
            await Inner.InvokeAsync(turn, cancellationToken);
        }

        private async Task<T> GatedAsync<T>(Func<T> turn, CancellationToken cancellationToken)
        {
            Waiting.TrySetResult();
            await _open.Task;
            return await Inner.InvokeAsync(turn, cancellationToken);
        }

        private static TaskCompletionSource CompletedGate()
        {
            var gate = new TaskCompletionSource();
            gate.SetResult();
            return gate;
        }
    }

    private sealed class Journal
    {
        private readonly List<string> _items = [];
        public void Add(string item) { lock (_items) _items.Add(item); }
        public string Text { get { lock (_items) return string.Join(",", _items); } }
    }

    private class Page(string name, Journal? journal = null) : INavigationDepartureGuard, INavigationResume, IAsyncDisposable
    {
        private int _disposed;

        public string Name { get; } = name;
        public Func<NavigationDeparture, CancellationToken, ValueTask<bool>>? Guard { get; set; }
        public Func<NavigationResume, CancellationToken, ValueTask>? Resume { get; set; }
        public Func<Task>? OnDispose { get; set; }
        public List<NavigationDeparture> Departures { get; } = [];
        public NavigationResume? LastResume { get; private set; }
        public int Resumed;
        public int Disposed => Volatile.Read(ref _disposed);
        public bool DisposedInTurn { get; private set; }
        public bool LeaseHeldAtDispose { get; private set; }
        public IRunicModelContext? Context { get; set; }
        public bool ContextAliveAtDispose { get; private set; }

        public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken)
        {
            lock (Departures) Departures.Add(departure);
            return Guard?.Invoke(departure, cancellationToken) ?? ValueTask.FromResult(true);
        }

        public ValueTask ResumeAsync(NavigationResume resume, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Resumed);
            LastResume = resume;
            return Resume?.Invoke(resume, cancellationToken) ?? ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposed);
            DisposedInTurn = RunicModelContextRegistry.Shared.TryGet(this, out var context) && context!.IsExecuting;
            LeaseHeldAtDispose = context is not null;
            if (Context is not null)
            {
                try
                {
                    await Context.InvokeAsync(() => { });
                    ContextAliveAtDispose = true;
                }
                catch (ObjectDisposedException) { }
            }
            journal?.Add(Name);
            if (OnDispose is not null) await OnDispose();
        }

        public override string ToString() => Name;
    }

    private sealed class InitPage(string name) : Page(name), INavigationInitialize
    {
        public Func<NavigationEntryContext, CancellationToken, ValueTask>? Initialize { get; set; }
        public int Initialized;
        public NavigationEntryContext? Entry { get; private set; }

        public ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Initialized);
            Entry = entry;
            return Initialize?.Invoke(entry, cancellationToken) ?? ValueTask.CompletedTask;
        }
    }

    private sealed class ParentPage : Page
    {
        public ParentPage(string name, RunicNavigator navigator, NavigationChildRetention policy = NavigationChildRetention.Keep,
            Journal? journal = null) : base(name, journal)
        {
            ChildRoot = new Page("root");
            Child = navigator.CreateRegion<Page>(this, NavigationTarget.Borrow(ChildRoot), new NavigationRegionOptions(policy));
        }

        public Page ChildRoot { get; }
        public NavigationRegion<Page> Child { get; }
    }

    private sealed class InputPage : INavigationInitialize<string>, IDisposable
    {
        public int Disposed;
        public ValueTask InitializeAsync(NavigationEntryContext entry, string input, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public void Dispose() => Disposed++;
    }

    private sealed class BothPage : INavigationInitialize, INavigationInitialize<int>
    {
        public int Plain;
        public int Input;

        public ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken)
        {
            Plain++;
            return ValueTask.CompletedTask;
        }

        public ValueTask InitializeAsync(NavigationEntryContext entry, int input, CancellationToken cancellationToken)
        {
            Input = input;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ForeignTarget : INavigationTarget<Page>;

    private sealed class RaceException() : Exception("race");

    // Shared by the race pages: the violation log, and the guards in flight per region.
    private sealed class RaceState(int seed)
    {
        // The guards running in each region, as "page kind" descriptions.
        private readonly Dictionary<NavigationRegion<RacePage>, List<string>> _guardsInRegion = [];
        private int _guardsRun;

        public bool StrictInit { get; init; } = true;
        public ConcurrentQueue<string> Violations { get; } = new();
        public int GuardsRun => Volatile.Read(ref _guardsRun);

        public void EnterGuard(NavigationRegion<RacePage>? region, string description)
        {
            if (region is null) return;
            lock (_guardsInRegion)
            {
                _guardsRun++;
                if (!_guardsInRegion.TryGetValue(region, out var running)) _guardsInRegion.Add(region, running = []);
                running.Add(description);
                if (running.Count > 1)
                    Violations.Enqueue($"seed {seed}: guards overlapped in one region: {string.Join(", ", running)}");
            }
        }

        public void ExitGuard(NavigationRegion<RacePage>? region, string description)
        {
            if (region is null) return;
            lock (_guardsInRegion) _guardsInRegion[region].Remove(description);
        }
    }

    private sealed class InitRacePage(string name, RaceState state)
        : RacePage(name, state), INavigationInitialize
    {
        public ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken) => Hook(1, cancellationToken);
    }

    private class RacePage(string name, RaceState state)
        : Page(name), INavigationDepartureGuard
    {
        public static volatile bool Calm;
        public int Behavior { get; init; }
        public int Delay { get; init; }
        public NavigationRegion<RacePage>? Child { get; set; }
        // The region this page was admitted to; null for borrowed pool pages.
        public NavigationRegion<RacePage>? Origin { get; init; }

        ValueTask<bool> INavigationDepartureGuard.CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
            GuardAsync(departure.Kind, cancellationToken);

        private async ValueTask<bool> GuardAsync(NavigationDepartureKind kind, CancellationToken cancellationToken)
        {
            var description = $"{Name}#{RuntimeHelpers.GetHashCode(this)} {kind}";
            state.EnterGuard(Origin, description);
            try
            {
                await Hook(2, cancellationToken);
                return Calm || Behavior != 3;
            }
            finally { state.ExitGuard(Origin, description); }
        }

        // Only the transition that created a pending entry, or navigator
        // disposal, retires it, so initialize never starts after disposal.
        // A guard may overlap its entry's retirement by another path (cleanup
        // never awaits guards), so only initialize is checked.
        protected async ValueTask Hook(int failure, CancellationToken cancellationToken)
        {
            if (failure == 1 && Disposed > 0 && state.StrictInit) state.Violations.Enqueue($"{Name} was initialized after disposal");
            if (Delay == 1) await Task.Yield();
            else if (Delay == 2) await Task.Delay(1, cancellationToken);
            if (!Calm && Behavior == failure) throw new RaceException();
        }
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, Exception? Exception, string Message,
        IReadOnlyDictionary<string, object?> State);

    private sealed class LogCapture : ILoggerFactory
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        public bool Has(int id, LogLevel level) => _entries.Any(entry => entry.EventId.Id == id && entry.Level == level);
        public int Count(int id) => _entries.Count(entry => entry.EventId.Id == id);
        public IEnumerable<LogEntry> All(int id) => _entries.Where(entry => entry.EventId.Id == id);
        public LogEntry Single(int id) => _entries.SingleOrDefault(entry => entry.EventId.Id == id)
            ?? throw new InvalidOperationException($"Expected one {id} entry: {string.Join(", ", _entries.Select(entry => entry.EventId.Id))}");

        private sealed class Logger(LogCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
                owner._entries.Enqueue(new(logLevel, eventId, exception, formatter(state, exception),
                    values.ToDictionary(pair => pair.Key, pair => pair.Value)));
            }
        }
    }
}
