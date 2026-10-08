namespace Runic.Navigation.Tests;

internal static partial class NavigationTests
{
    public static async Task RunRetentionAsync() => await OnlyJoinableBacksKeepTheirPlanAsync();

    // Only a plain Back keeps its admitted plan, and only while it runs: a committed push or replace never holds one.
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
