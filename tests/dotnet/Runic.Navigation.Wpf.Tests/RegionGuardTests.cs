using TUnit.Core;
using TUnit.Core.Executors;
using static Runic.Navigation.Wpf.Tests.Ui;

namespace Runic.Navigation.Wpf.Tests;

// W315-020 point 3 (#350): GuardWindowClose clears the region, which asks its region guards and the navigator's guards;
// #412: those asked BeforeEntries come before the page's guard.
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

    // #412: a navigator guard asked BeforeEntries vetoes the close before the page's guard asks; once it allows, the page
    // is asked after it.
    [Test, DisplayName("GuardWindowClose asks BeforeEntries guards first"), NotInParallel("WpfSuite", Order = 55)]
    public void WindowCloseAsksBeforeEntriesGuardsFirst()
    {
        using var fixture = new NavFixture();
        var asked = new List<string>();
        var running = true;
        var region = fixture.Region(NavigationTarget.Own<object>(new GuardedPage("editor", () =>
        {
            asked.Add("page");
            return ValueTask.FromResult(true);
        })));
        using var operationGuard = fixture.Navigator.AddDepartureGuard(new ScriptedGuard(departure =>
        {
            asked.Add($"operation {departure.Kind} {departure.Operation}");
            return ValueTask.FromResult(!running);
        }), new NavigationRegionGuardOptions { Order = NavigationRegionGuardOrder.BeforeEntries, AskOnRetain = true });
        var host = new NavigationHost { Region = region, GuardWindowClose = true };
        var window = ShowWindow(host, window => AddTemplates(window, typeof(GuardedPage)));
        try
        {
            window.Close();
            PumpUntil(() => asked.Count == 1, "the operation guard");
            PumpUntil(() => !region.IsTransitioning, "the vetoed clear");
            Drain();
            Require(IsOpen(window) && region.CurrentEntry is not null && string.Join(",", asked) == "operation Retire Clear",
                $"A vetoing BeforeEntries guard did not keep the window open before the page was asked: {string.Join(",", asked)}.");

            running = false;
            window.Close();
            PumpUntil(() => !IsOpen(window), "the window to close");
            Require(region.CurrentEntry is null && string.Join(",", asked) == "operation Retire Clear,operation Retire Clear,page",
                $"The close asked {string.Join(",", asked)}.");
        }
        finally
        {
            host.GuardWindowClose = false;
            if (IsOpen(window)) window.Close();
        }
    }

    // I-032: WindowCloseConfirmation asks once per close, not once per guarded region; Create asks only for closes, and
    // OncePerClose leaves every other departure to the guard it wraps.
    [Test, DisplayName("WindowCloseConfirmation asks once per close"), NotInParallel("WpfSuite", Order = 65)]
    public void CloseConfirmationAsksOncePerClose()
    {
        using var fixture = new NavFixture();
        var first = fixture.Region(NavigationTarget.Own<object>(new Page("first")));
        var second = fixture.Region(NavigationTarget.Own<object>(new Page("second")));
        var answers = new Queue<TaskCompletionSource<bool>>();
        var asked = 0;
        using var registration = fixture.Navigator.AddDepartureGuard(WindowCloseConfirmation.Create((departure, _) =>
        {
            asked++;
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            answers.Enqueue(answer);
            return new ValueTask<bool>(answer.Task);
        }));
        var panel = new System.Windows.Controls.StackPanel();
        var firstHost = new NavigationHost { Region = first, GuardWindowClose = true };
        var secondHost = new NavigationHost { Region = second, GuardWindowClose = true };
        panel.Children.Add(firstHost);
        panel.Children.Add(secondHost);
        var window = ShowWindow(panel, window => AddTemplates(window, typeof(Page)));
        try
        {
            // Outside a close, the guard lets every departure pass without asking.
            Pump(first.PushAsync(NavigationTarget.Own<object>(new Page("pushed"))));
            Require(Pump(first.BackAsync()) is NavigationResult<object>.Committed && asked == 0, "Back asked the close confirmation.");

            // No: asked once, the window stays open with both regions.
            window.Close();
            PumpUntil(() => answers.Count == 1, "the close confirmation");
            answers.Dequeue().SetResult(false);
            PumpUntil(() => !first.IsTransitioning, "the vetoed clear");
            Drain();
            Require(IsOpen(window) && first.CurrentEntry is not null && second.CurrentEntry is not null && asked == 1,
                $"A no closed the window or cleared a region; asked {asked} times.");

            // Yes: asked once more for both regions, and the window closes.
            window.Close();
            PumpUntil(() => answers.Count == 1, "the close confirmation again");
            answers.Dequeue().SetResult(true);
            PumpUntil(() => !IsOpen(window), "the window to close");
            Require(first.CurrentEntry is null && second.CurrentEntry is null && asked == 2 && answers.Count == 0,
                $"The close asked {asked} times in all for two regions.");
        }
        finally
        {
            firstHost.GuardWindowClose = false;
            secondHost.GuardWindowClose = false;
            while (answers.TryDequeue(out var answer)) answer.TrySetResult(true);
            if (IsOpen(window)) window.Close();
        }
    }

    [Test, DisplayName("WindowCloseConfirmation.OncePerClose wraps a guard"), NotInParallel("WpfSuite", Order = 66)]
    public void CloseConfirmationWrapsAGuard()
    {
        using var fixture = new NavFixture();
        var first = fixture.Region(NavigationTarget.Own<object>(new Page("first")));
        var second = fixture.Region(NavigationTarget.Own<object>(new Page("second")));
        var asked = new List<string>();
        using var registration = fixture.Navigator.AddDepartureGuard(WindowCloseConfirmation.OncePerClose(new ScriptedGuard(departure =>
        {
            asked.Add($"{departure.Operation}");
            return ValueTask.FromResult(true);
        })));
        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(new NavigationHost { Region = first, GuardWindowClose = true });
        panel.Children.Add(new NavigationHost { Region = second, GuardWindowClose = true });
        var window = ShowWindow(panel, window => AddTemplates(window, typeof(Page)));
        try
        {
            // Outside a close the wrapped guard is asked as before, each time.
            Pump(first.PushAsync(NavigationTarget.Own<object>(new Page("pushed"))));
            Pump(first.BackAsync());
            Pump(first.PushAsync(NavigationTarget.Own<object>(new Page("pushed again"))));
            Pump(first.BackAsync());
            Require(asked is ["Back", "Back"], $"Outside a close the guard was asked as {string.Join(", ", asked)}.");

            window.Close();
            PumpUntil(() => !IsOpen(window), "the window to close");
            Require(asked is ["Back", "Back", "Clear"] && first.CurrentEntry is null && second.CurrentEntry is null,
                $"The close asked the guard as {string.Join(", ", asked)}.");
        }
        finally
        {
            WindowCloseGuard.Remove(first);
            WindowCloseGuard.Remove(second);
            if (IsOpen(window)) window.Close();
        }
    }

    private sealed class ScriptedGuard(Func<NavigationRegionDeparture, ValueTask<bool>> answer) : INavigationRegionGuard
    {
        public ValueTask<bool> CanDepartAsync(NavigationRegionDeparture departure, CancellationToken cancellationToken) => answer(departure);
    }
}
