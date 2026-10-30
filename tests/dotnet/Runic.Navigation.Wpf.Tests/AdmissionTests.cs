using System.Windows.Input;
using TUnit.Core;
using TUnit.Core.Executors;
using static Runic.Navigation.Wpf.Tests.Ui;

namespace Runic.Navigation.Wpf.Tests;

// W315-020 point 4 (#350): the hosts in a region that rejects requests while busy. BrowseBack follows IsBusy; a window
// close and a dialog dismissal pass Admission = Supersede, so they are never refused as Busy.
[TestExecutor<WpfDispatcherExecutor>]
internal sealed class AdmissionTests
{
    private static readonly NavigationRegionOptions RejectWhileBusy = new() { Admission = NavigationAdmission.RejectWhileBusy };

    // After the commit IsTransitioning is false, but the departed entry still retires: BrowseBack stays disabled.
    [Test, DisplayName("BrowseBack while busy"), NotInParallel("WpfSuite", Order = 48)]
    public void BrowseBackWhileBusy()
    {
        using var fixture = new NavFixture();
        var region = fixture.Navigator.CreateRegion(new object(), NavigationTarget.Own<object>(new Page("home")), RejectWhileBusy);
        var host = new NavigationHost { Region = region };
        var window = ShowWindow(host, window => AddTemplates(window, typeof(Page), typeof(SlowDisposePage)));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Pump(region.PushAsync(NavigationTarget.Own<object>(new Page("middle"))));
            var slow = new SlowDisposePage(release.Task);
            Pump(region.PushAsync(NavigationTarget.Own<object>(slow)));
            var back = region.BackAsync().AsTask();
            PumpUntil(() => slow.Disposing, "the retirement of the departed page");
            PumpUntil(() => host.IsBusy && !host.IsTransitioning, "the host to report the busy region");
            Require(region.CanGoBack && !NavigationCommands.BrowseBack.CanExecute(null, host),
                "BrowseBack can execute while a RejectWhileBusy region is busy.");
            release.SetResult();
            Require(Pump(back) is NavigationResult<object>.Committed, "The Back did not commit.");
            PumpUntil(() => !host.IsBusy, "the host to report the idle region");
            Require(NavigationCommands.BrowseBack.CanExecute(null, host), "BrowseBack stayed disabled once the region was idle.");
        }
        finally
        {
            release.TrySetResult();
            window.Close();
        }
    }

    // The close's clear supersedes the push that keeps the region busy, and the window closes after asking again.
    [Test, DisplayName("GuardWindowClose in a busy region"), NotInParallel("WpfSuite", Order = 49)]
    public void WindowCloseInABusyRegion()
    {
        using var fixture = new NavFixture();
        var region = fixture.Navigator.CreateRegion(new object(), NavigationTarget.Own<object>(new Page("home")), RejectWhileBusy);
        var pushGuard = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = 0;
        var editor = new DeparturePage("editor", async (departure, token) =>
        {
            if (departure.Kind == NavigationDepartureKind.Retire)
            {
                retired++;
                return true;
            }
            pushGuard.TrySetResult(token);
            await Task.Delay(Timeout.Infinite, token);
            return true;
        });
        Pump(region.PushAsync(NavigationTarget.Own<object>(editor)));
        var host = new NavigationHost { Region = region, GuardWindowClose = true };
        var window = ShowWindow(host, window => AddTemplates(window, typeof(Page), typeof(DeparturePage)));
        try
        {
            var push = region.PushAsync(NavigationTarget.Own<object>(new Page("next"))).AsTask();
            var token = Pump(pushGuard.Task, "the push's guard");
            Require(region.IsBusy, "The push did not keep the region busy.");
            window.Close();
            PumpUntil(() => !IsOpen(window), "the window to close");
            Require(Pump(push) is NavigationResult<object>.Superseded && token.IsCancellationRequested && retired == 1
                && region.CurrentEntry is null, "The close did not supersede the busy region's push and clear the region.");
        }
        finally
        {
            host.GuardWindowClose = false;
            if (IsOpen(window)) window.Close();
        }
    }

    // Esc dismisses a dialog whose region is busy with a push over it: the dismissal supersedes the push.
    [Test, DisplayName("dialog dismissal in a busy region"), NotInParallel("WpfSuite", Order = 50)]
    public void DialogDismissalInABusyRegion()
    {
        using var scene = new DialogScene(options: RejectWhileBusy);
        var pushGuard = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new DeparturePage("busy", async (departure, token) =>
        {
            if (departure.Kind == NavigationDepartureKind.Retire) return true;
            pushGuard.TrySetResult(token);
            await Task.Delay(Timeout.Infinite, token);
            return true;
        });
        Pump(scene.Region.PushAsync(NavigationTarget.Own<object>(dialog)));
        var window = scene.WaitForWindows(1);
        var push = scene.Region.PushAsync(NavigationTarget.Own<object>(new PlainDialog())).AsTask();
        var token = Pump(pushGuard.Task, "the push's guard");
        Require(scene.Region.IsBusy && scene.Region.Admission == NavigationAdmission.RejectWhileBusy, "The push did not keep the region busy.");
        PressEscape(window);
        Require(Pump(push) is NavigationResult<object>.Superseded && token.IsCancellationRequested, "The dismissal did not supersede the push.");
        PumpUntil(() => scene.Region.CurrentEntry is null && !IsOpen(window), "the dismissed dialog to close");
    }

    // #741: in a region that queues, Esc waits behind a push over the dialog instead of superseding it. The push stacks a
    // second dialog; the dismissal of the covered dialog is then NotCurrent, and both dialogs stay.
    [Test, DisplayName("dialog dismissal in a queued region"), NotInParallel("WpfSuite", Order = 51)]
    public void DialogDismissalInAQueuedRegion()
    {
        using var scene = new DialogScene(options: new NavigationRegionOptions { Admission = NavigationAdmission.Queue });
        var pushGuard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new DeparturePage("queued", async (departure, _) =>
        {
            if (departure.Kind == NavigationDepartureKind.Retire) return true;
            pushGuard.TrySetResult();
            return await allow.Task;
        });
        Pump(scene.Region.PushAsync(NavigationTarget.Own<object>(dialog)));
        var window = scene.WaitForWindows(1);
        var push = scene.Region.PushAsync(NavigationTarget.Own<object>(new PlainDialog())).AsTask();
        Pump(pushGuard.Task, "the push's guard");
        PressEscape(window);
        // Runs the posted dismissal, which queues behind the push.
        Drain();
        allow.SetResult(true);
        Require(Pump(push) is NavigationResult<object>.Committed, "The dismissal superseded the push in a queued region.");
        Pump(scene.Region.WhenIdleAsync().AsTask());
        scene.WaitForWindows(2);
        Require(scene.Region.History.Count == 1 && ReferenceEquals(scene.Region.History[0].Content, dialog),
            "The covered dialog left the region.");
    }

    // Owned content whose disposal waits for the test.
    internal sealed class SlowDisposePage(Task release) : Page("slow"), IAsyncDisposable
    {
        public bool Disposing { get; private set; }

        public async ValueTask DisposeAsync()
        {
            Disposing = true;
            await release;
        }
    }
}
