namespace Runic.Navigation.Tests;

internal static partial class NavigationTests
{
    public static async Task RunContextLifetimeAsync()
    {
        await using var fixture = new Fixture(closeTimeout: TimeSpan.FromMilliseconds(100));
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guarded = new Page("guarded")
        {
            Guard = async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task; // Deliberately ignores navigation cancellation.
                return true;
            },
        };
        await Wait(region.PushAsync(NavigationTarget.Own(guarded)));
        var pending = region.BackAsync().AsTask();
        await Wait(entered.Task);
        try
        {
            await Wait(fixture.Context.DisposeAsync());
            Require(fixture.Navigator.IsClosed, "Closing the default context did not close its navigator synchronously.");
            var factoryRan = false;
            var late = region.PushAsync(NavigationTarget.Create<Page>(_ =>
            {
                factoryRan = true;
                return new Page("late");
            })).AsTask();
            Require(late.IsCompleted && await Wait(late) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed }
                && !factoryRan, "A post-close push waited for the uncancellable guard or ran its factory.");
        }
        finally { release.TrySetResult(); }
        Require(await Wait(pending) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed },
            "The in-flight Back did not end Closed.");
    }
}
