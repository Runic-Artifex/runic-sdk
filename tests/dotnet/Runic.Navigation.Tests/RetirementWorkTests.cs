using Microsoft.Extensions.Logging;
using TUnit.Core;

namespace Runic.Navigation.Tests;

// #284 (0.11 B6): INavigationPresentation.ForgetAsync holds back the release of retired content until work a presentation
// keeps for it has ended, without holding up the retirement; NavigationRequestOptions.Requester rejects a late request of
// an entry that is no longer current, or whose owning entry is not.
internal sealed partial class NavigationTests
{
    [Test]
    public async Task ForgetAsyncDefersTheReleaseWithoutHoldingUpBackAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var presentation = new KeepingPresentation();
        using var attachment = fixture.Navigator.AttachPresentation(presentation);

        var draining = new Page("draining");
        await Wait(region.PushAsync(NavigationTarget.Own(draining)));
        var work = presentation.Keep(draining);
        var back = await Wait(region.BackAsync());
        Require(back is NavigationResult<Page>.Committed, $"Back gave {back}.");
        // Back, IsBusy and WhenIdleAsync do not wait for the kept work.
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(!fixture.Navigator.IsBusy && fixture.Navigator.UnretiredEntryCount == 1,
            "The navigator stayed busy, or kept the entry unretired, while a presentation kept work for its content.");
        Require(presentation.Forgotten.Contains(draining), "The presentation was not asked to forget the content.");
        Require(draining.Disposed == 0 && RunicModelContextRegistry.Shared.TryGet(draining, out _) && fixture.Navigator.DeferredReleaseCount == 1,
            "The content or its lease was released while a presentation kept work for it.");

        work.SetResult();
        await Until(() => draining.Disposed == 1, "The content was not disposed after the kept work ended.");
        await Until(() => !RunicModelContextRegistry.Shared.TryGet(draining, out _) && fixture.Navigator.DeferredReleaseCount == 0,
            "The lease or the deferred release record outlived the release.");

        // Work that faults is logged as a Forget failure (1064), and the content is still released.
        var failing = new Page("failing");
        await Wait(region.PushAsync(NavigationTarget.Own(failing)));
        var failingWork = presentation.Keep(failing);
        await Wait(region.BackAsync());
        failingWork.SetException(new IOException("kept work failed"));
        await Until(() => failing.Disposed == 1, "Content whose kept work faulted was not disposed.");
        var logged = fixture.Logs.Single(1064);
        Require(logged.Exception is IOException && logged.State["Step"] as string == "Forget",
            "Faulted kept work was not logged as a Forget cleanup failure.");

        // A presentation that keeps nothing for the content releases it as before.
        var plain = new Page("plain");
        await Wait(region.PushAsync(NavigationTarget.Own(plain)));
        await Wait(region.BackAsync());
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(plain.Disposed == 1 && fixture.Navigator.DeferredReleaseCount == 0, "Content with no kept work was not disposed by its retirement.");
    }

    [Test]
    public async Task DisposeAsyncStopsWaitingForKeptWorkAfterTheCloseTimeoutAsync()
    {
        await using var fixture = new Fixture(closeTimeout: TimeSpan.FromMilliseconds(200));
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var presentation = new KeepingPresentation();
        using var attachment = fixture.Navigator.AttachPresentation(presentation);
        var overrunning = new Page("overrunning");
        await Wait(region.PushAsync(NavigationTarget.Own(overrunning)));
        var work = presentation.Keep(overrunning);
        await Wait(region.BackAsync());

        await Wait(fixture.Navigator.DisposeAsync());
        var overdue = fixture.Logs.Single(1077);
        Require(overdue.Level == LogLevel.Warning && overdue.State["EntryType"] as string == nameof(Page)
                && overdue.State["Region"] as string == nameof(Page) && overdue.State["RegionId"] is int,
            $"Disposal did not log the overdue release as 1077: {overdue.Message}");
        Require(overrunning.Disposed == 0, "Disposal disposed content while a presentation kept work for it.");
        work.SetResult();
        await Until(() => overrunning.Disposed == 1, "The content was not disposed when its kept work ended after disposal.");

        // Work that ends within the close timeout is awaited without a warning.
        await using var prompt = new Fixture(closeTimeout: TestTimeouts.Scale(TimeSpan.FromSeconds(10)));
        var promptRegion = prompt.Navigator.CreateRegion<Page>(prompt.Root, NavigationTarget.Borrow(new Page("home")));
        var promptPresentation = new KeepingPresentation();
        using var promptAttachment = prompt.Navigator.AttachPresentation(promptPresentation);
        var ending = new Page("ending");
        await Wait(promptRegion.PushAsync(NavigationTarget.Own(ending)));
        var endingWork = promptPresentation.Keep(ending);
        await Wait(promptRegion.BackAsync());
        var disposal = prompt.Navigator.DisposeAsync().AsTask();
        await Task.Delay(50);
        Require(!disposal.IsCompleted, "Disposal did not wait for kept work.");
        endingWork.SetResult();
        await Wait(disposal);
        Require(ending.Disposed == 1 && prompt.Logs.Count(1077) == 0, "Disposal did not release content whose kept work ended in time, or warned.");
    }

    [Test]
    public async Task RequesterMustBeCurrentWithItsOwnersAsync()
    {
        await using var fixture = new Fixture();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var parent = new Page("parent");
        await Wait(main.PushAsync(NavigationTarget.Own(parent)));
        var side = fixture.Navigator.CreateRegion<Page>(parent, NavigationTarget.Own(new Page("child")));
        var child = side.CurrentEntry!.Id;
        var parentId = main.CurrentEntry!.Id;

        // The requester and the entry owning its region are current: a request to a parent region commits.
        var opened = await Wait(main.PushAsync(NavigationTarget.Own(new Page("opened")), new NavigationRequestOptions { Requester = child }));
        Require(opened is NavigationResult<Page>.Committed, $"A request of a current entry gave {opened}.");
        // The push retained the parent: the child is still its region's current entry, but its owner is not current.
        var stale = await Wait(main.PushAsync(NavigationTarget.Own(new Page("stale")), new NavigationRequestOptions { Requester = child }));
        Require(stale is NavigationResult<Page>.Rejected { Reason: NavigationRejection.NotCurrent } rejected && rejected.By == child,
            $"A request of an entry whose owner was retained gave {stale}.");
        var staleOwn = await Wait(side.PushAsync(NavigationTarget.Own(new Page("staleOwn")), new NavigationRequestOptions { Requester = parentId }));
        Require(staleOwn is NavigationResult<Page>.Rejected { Reason: NavigationRejection.NotCurrent }, $"A request of a retained entry gave {staleOwn}.");
        Require(main.CurrentEntry!.Content.Name == "opened" && side.CurrentEntry!.Content.Name == "child", "A rejected request changed a region.");

        await Wait(main.BackAsync());
        var again = await Wait(side.PushAsync(NavigationTarget.Own(new Page("again")), new NavigationRequestOptions { Requester = child }));
        Require(again is NavigationResult<Page>.Committed, $"A request of an entry that is current again gave {again}.");
        // The push retained the requester itself.
        var retained = await Wait(main.PushAsync(NavigationTarget.Own(new Page("late")), new NavigationRequestOptions { Requester = child }));
        Require(retained is NavigationResult<Page>.Rejected { Reason: NavigationRejection.NotCurrent }, $"A request of a retained entry gave {retained}.");

        // Retired, and unknown.
        await Wait(main.BackAsync());
        await Wait(fixture.Navigator.WhenIdleAsync());
        var retired = await Wait(main.PushAsync(NavigationTarget.Own(new Page("afterRetire")), new NavigationRequestOptions { Requester = parentId }));
        Require(retired is NavigationResult<Page>.Rejected { Reason: NavigationRejection.NotCurrent } && main.CurrentEntry!.Content.Name == "home",
            $"A request of a retired entry gave {retired}.");
        var unknown = await Wait(main.PushAsync(NavigationTarget.Own(new Page("unknown")), new NavigationRequestOptions { Requester = new NavigationEntryId(10_000) }));
        Require(unknown is NavigationResult<Page>.Rejected { Reason: NavigationRejection.NotCurrent }, $"A request of an unknown entry gave {unknown}.");
    }

    // The requester stops being current while the request waits for a guard: the request is rejected at its commit.
    [Test]
    public async Task RequesterIsCheckedAgainAtTheCommitAsync()
    {
        await using var fixture = new Fixture();
        var guarded = new Page("guarded");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        guarded.Guard = async (_, _) =>
        {
            entered.TrySetResult();
            return await release.Task;
        };
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(guarded));
        var other = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Borrow(new Page("otherHome")));
        await Wait(other.PushAsync(NavigationTarget.Own(new Page("worker"))));
        var worker = other.CurrentEntry!.Id;

        var request = main.ReplaceAsync(NavigationTarget.Own(new Page("result")), new NavigationRequestOptions { Requester = worker }).AsTask();
        await Wait(entered.Task);
        await Wait(other.BackAsync());
        release.SetResult(true);
        var outcome = await Wait(request);
        Require(outcome is NavigationResult<Page>.Rejected { Reason: NavigationRejection.NotCurrent } rejected && rejected.By == worker
                && main.CurrentEntry!.Content.Name == "guarded",
            $"A request whose requester retired before the commit gave {outcome}.");
    }

    // Keeps work for chosen content until the test completes it.
    private sealed class KeepingPresentation : INavigationPresentation
    {
        private readonly Dictionary<object, TaskCompletionSource> _work = new(ReferenceEqualityComparer.Instance);

        public List<object> Forgotten { get; } = [];

        public TaskCompletionSource Keep(object content)
        {
            var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_work) _work[content] = work;
            return work;
        }

        public void Forget(object content)
        {
            lock (Forgotten) Forgotten.Add(content);
        }

        public ValueTask ForgetAsync(object content)
        {
            Forget(content);
            lock (_work) return _work.TryGetValue(content, out var work) ? new(work.Task) : ValueTask.CompletedTask;
        }
    }
}
