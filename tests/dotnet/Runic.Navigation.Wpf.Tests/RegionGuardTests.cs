using TUnit.Core;
using TUnit.Core.Executors;
using static Runic.Navigation.Wpf.Tests.Ui;

namespace Runic.Navigation.Wpf.Tests;

// W315-020 point 3 (#350): GuardWindowClose clears the region, which asks its region guards and the navigator's guards.
[TestExecutor<WpfDispatcherExecutor>]
internal sealed class RegionGuardTests
{
    [Test, DisplayName("GuardWindowClose asks region guards"), NotInParallel("WpfSuite", Order = 51)]
    public void WindowCloseAsksRegionGuards()
    {
        using var fixture = new NavFixture();
        var region = fixture.Region(NavigationTarget.Own<object>(new Page("home")));
        var answers = new Queue<TaskCompletionSource<bool>>();
        var asked = new List<string>();
        using var regionGuard = region.AddDepartureGuard(new ScriptedGuard(departure =>
        {
            asked.Add($"region {departure.Kind} {departure.Operation}");
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            answers.Enqueue(answer);
            return new ValueTask<bool>(answer.Task);
        }));
        using var navigatorGuard = fixture.Navigator.AddDepartureGuard(new ScriptedGuard(departure =>
        {
            asked.Add($"navigator {departure.Kind} {departure.Operation}");
            return ValueTask.FromResult(true);
        }));
        var host = new NavigationHost { Region = region, GuardWindowClose = true };
        var window = ShowWindow(host, window => AddTemplates(window, typeof(Page)));
        try
        {
            window.Close();
            PumpUntil(() => answers.Count == 1, "the region guard");
            answers.Dequeue().SetResult(false);
            PumpUntil(() => !region.IsTransitioning, "the vetoed clear");
            Drain();
            Require(IsOpen(window) && region.CurrentEntry is not null && string.Join(",", asked) == "region Retire Clear",
                $"A vetoing region guard did not keep the window open: asked {string.Join(",", asked)}.");

            window.Close();
            PumpUntil(() => answers.Count == 1, "the region guard again");
            answers.Dequeue().SetResult(true);
            PumpUntil(() => !IsOpen(window), "the window to close");
            Require(region.CurrentEntry is null && string.Join(",", asked) == "region Retire Clear,region Retire Clear,navigator Retire Clear",
                $"The close did not ask the region and navigator guards: {string.Join(",", asked)}.");
        }
        finally
        {
            host.GuardWindowClose = false;
            while (answers.TryDequeue(out var answer)) answer.TrySetResult(true);
            if (IsOpen(window)) window.Close();
        }
    }

    private sealed class ScriptedGuard(Func<NavigationRegionDeparture, ValueTask<bool>> answer) : INavigationRegionGuard
    {
        public ValueTask<bool> CanLeaveAsync(NavigationRegionDeparture departure, CancellationToken cancellationToken) => answer(departure);
    }
}
