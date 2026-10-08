using System.Runtime.CompilerServices;

namespace Runic.Navigation.Tests;

// Retired content must become collectable once it is disposed: nothing the navigator keeps for the current
// entry, its region or its transitions may reach a retired entry.
internal static partial class NavigationTests
{
    public static async Task RunRetentionAsync()
    {
        foreach (var operation in new[] { "Replace", "Push+Back", "Back" })
            await RetiredContentIsCollectableAsync(operation);
        await OnlyJoinableBacksKeepTheirPlanAsync();
    }

    private static async Task RetiredContentIsCollectableAsync(string operation)
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var retired = await RetireOneAsync(fixture, region, operation);
        await Wait(fixture.Navigator.WhenIdleAsync());
        // The test page logs every departure it sees; a departure the app keeps holds its transition (and the
        // content that transition pushed), so drop the log to test only what the navigator keeps.
        foreach (var page in region.Core.Stack.Select(entry => (Page)entry.Content!))
            lock (page.Departures) page.Departures.Clear();
        for (var attempt = 0; attempt < 5 && retired.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(10);
        }
        Require(!retired.IsAlive, $"{operation}: the retired content is still reachable after it was disposed.");
        GC.KeepAlive(region);
        GC.KeepAlive(fixture);
    }

    // Retires one owned page with a guard and a commit action, the way LeaveConfirmation registers its discard,
    // and returns a weak reference to it. Kept out of line so no local of the caller roots the page.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RetireOneAsync(Fixture fixture, NavigationRegion<Page> region, string operation)
    {
        var page = new Page("x");
        page.Guard = (departure, _) =>
        {
            departure.OnCommitted(() => page.Name.ToString());
            return ValueTask.FromResult(true);
        };
        var weak = new WeakReference(page);
        await Wait(region.PushAsync(NavigationTarget.Own(page)));
        switch (operation)
        {
            case "Replace":
                Require(await Wait(region.ReplaceAsync(NavigationTarget.Own(new Page("y")))) is NavigationResult<Page>.Committed, "Replace failed.");
                break;
            case "Push+Back":
                await Wait(region.PushAsync(NavigationTarget.Own(new Page("y"))));
                await Wait(region.BackAsync()); // retires y; x is current
                Require(await Wait(region.ReplaceAsync(NavigationTarget.Own(new Page("z")))) is NavigationResult<Page>.Committed, "Replace failed.");
                break;
            default:
                Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Committed, "Back failed.");
                break;
        }
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(page.Disposed == 1, $"{operation}: the retired page was not disposed.");
        return weak;
    }

    // Only a plain Back keeps its admitted plan, and only while it runs: a committed push or replace never holds
    // one, and the entry it pushed does not keep the transition.
    private static async Task OnlyJoinableBacksKeepTheirPlanAsync()
    {
        await using var fixture = new Fixture();
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var x = new Page("x");
        var pushing = await HeldInGuardAsync(home, () => region.PushAsync(NavigationTarget.Own(x)).AsTask(), region);
        var replacing = await HeldInGuardAsync(x, () => region.ReplaceAsync(NavigationTarget.Own(new Page("y"))).AsTask(), region);
        await Wait(region.PushAsync(NavigationTarget.Own(new Page("z"))));
        Require(pushing.Running is null && replacing.Running is null, "A running Push or Replace kept its admitted plan.");
        Require(pushing.Plan is null && replacing.Plan is null, "A committed Push or Replace kept its admitted plan.");
        foreach (var entry in region.Core.Stack)
            Require(entry.Transition is null, $"The {entry.Phase} entry {entry.Id} still holds the transition that pushed it.");

        var back = await HeldInGuardAsync(region.Current!, () => region.BackAsync().AsTask(), region);
        Require(back.Running is not null, "A plain Back did not keep its admitted plan.");
        Require(back.Plan is null, "A released Back still holds its admitted plan.");
    }

    // Starts a navigation held in the departing page's guard, records the admitted plan while it runs, then lets it
    // commit and returns that plan and the one left after release.
    private static async Task<(NavigationPlan? Running, NavigationPlan? Plan)> HeldInGuardAsync(
        Page departing, Func<Task<NavigationResult<Page>>> start, NavigationRegion<Page> region)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        departing.Guard = async (_, _) =>
        {
            entered.SetResult();
            await gate.Task;
            return true;
        };
        var navigation = start();
        await Wait(entered.Task);
        var transition = region.Core.InFlight.Single();
        var running = transition.AdmittedPlan;
        departing.Guard = null;
        gate.SetResult();
        Require(await Wait(navigation) is NavigationResult<Page>.Committed, $"{transition.Operation} failed.");
        return (running, transition.AdmittedPlan);
    }
}
