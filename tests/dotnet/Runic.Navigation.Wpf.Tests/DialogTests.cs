using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using static Runic.Navigation.Wpf.Tests.Ui;

namespace Runic.Navigation.Wpf.Tests;

// NavigationDialogHost (W240-001 §8.4 and the §15 re-review resolutions).
internal static class DialogTests
{
    public static void Results()
    {
        using var scene = new DialogScene();
        // Completed through the entry.
        var vm = new DialogViewModel("complete");
        var request = scene.Region.PushForResult<bool>(NavigationTarget.Own<object>(vm));
        var window = scene.WaitForWindows(1);
        Require(ReferenceEquals(window.Owner, scene.Owner) && ReferenceEquals(window.DataContext, vm), "The dialog window has the wrong owner or DataContext.");
        Require(window.Content is ContentPresenter { ContentTemplate: not null }, "The dialog presenter has no explicit template.");
        Pump(vm.Entry!.CompleteAsync(true));
        var completion = Pump(request.Completion);
        Require(completion is NavigationCompletion<bool>.Completed { Value: true }, $"Completing gave {completion}.");
        PumpUntil(() => !IsOpen(window) && scene.Windows.Count == 0, "the completed dialog's window to close");

        // Dismissed through a window close, which the host cancels and turns into a dismissal.
        var closed = new DialogViewModel("close");
        var dismissed = scene.Region.PushForResult<bool>(NavigationTarget.Own<object>(closed));
        window = scene.WaitForWindows(1);
        window.Close();
        Require(IsOpen(window), "The host did not cancel a user close.");
        completion = Pump(dismissed.Completion);
        Require(completion is NavigationCompletion<bool>.Dismissed, $"Closing the window gave {completion}.");
        PumpUntil(() => !IsOpen(window) && scene.Windows.Count == 0, "the dismissed dialog's window to close");
        Require(scene.Region.CurrentEntry is null, "The dialog entry stayed.");
    }

    public static void GuardVetoKeepsTheWindow()
    {
        using var scene = new DialogScene();
        var vm = new DialogViewModel("veto") { Allow = () => false };
        var request = scene.Region.PushForResult<bool>(NavigationTarget.Own<object>(vm));
        var window = scene.WaitForWindows(1);
        window.Close();
        PumpUntil(() => vm.Guards == 1, "the dialog guard");
        Drain();
        Require(IsOpen(window) && window.IsVisible && ReferenceEquals(scene.Region.Current, vm), "A vetoed close closed the dialog.");
        vm.Allow = () => true;
        Pump(vm.Entry!.DismissAsync());
        Require(Pump(request.Completion) is NavigationCompletion<bool>.Dismissed, "The dismissal didn't end the request.");
        PumpUntil(() => !IsOpen(window), "the window to close");
    }

    public static void NestedDialogsCloseTopDown()
    {
        using var scene = new DialogScene();
        Pump(scene.Region.PushAsync(NavigationTarget.Own<object>(new DialogViewModel("lower"))));
        var lower = scene.WaitForWindows(1);
        Pump(scene.Region.PushAsync(NavigationTarget.Own<object>(new DialogViewModel("upper"))));
        var upper = scene.WaitForWindows(2);
        Require(ReferenceEquals(upper.Owner, lower) && ReferenceEquals(lower.Owner, scene.Owner), "The dialog windows aren't chained by owner.");
        Require(!IsEnabled(lower) && IsEnabled(upper), "The lower dialog isn't disabled under the upper one.");
        var order = new List<string>();
        lower.Closed += (_, _) => order.Add("lower");
        upper.Closed += (_, _) => order.Add("upper");
        Pump(scene.Region.BackAsync());
        PumpUntil(() => order.Count == 1, "the upper window to close");
        Require(IsEnabled(lower) && IsOpen(lower), "Back did not re-enable the lower dialog.");
        Pump(scene.Region.PushAsync(NavigationTarget.Own<object>(new DialogViewModel("upper again"))));
        var again = scene.WaitForWindows(2);
        again.Closed += (_, _) => order.Add("upper again");
        Pump(scene.Region.ClearAsync());
        PumpUntil(() => order.Count == 3, "both windows to close");
        Require(order.SequenceEqual(["upper", "upper again", "lower"]), $"The windows closed in the order {string.Join(", ", order)}.");
    }

    // An entry dismissed after its commit but before the posted reconcile never gets a window.
    public static void DismissedBeforeReconcile()
    {
        SignallingContext? signalling = null;
        var fixture = new NavFixture(DispatcherPriority.Send, inner => signalling = new SignallingContext(inner));
        using var scene = new DialogScene(fixture: fixture);
        var dialog = new PlainDialog();
        var loaded = LoadedWindows.Count;
        Task<NavigationResult<object>>? clear = null;
        var raised = false;
        scene.Region.PropertyChanged += (_, e) =>
        {
            if (raised || e.PropertyName != nameof(INavigationRegion.CurrentEntry) || !ReferenceEquals(scene.Region.Current, dialog)) return;
            raised = true;
            // Runs right after the commit turn, before the host's reconcile (Normal priority); the clear's commit turn,
            // at Send priority, is queued before this returns.
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Send, () =>
            {
                signalling!.Arm();
                clear = scene.Region.ClearAsync().AsTask();
                // Blocks the UI thread only until the clear, which has no hooks, requests its commit turn from the pool.
                Require(signalling.OffThreadTurn.Wait(TimeSpan.FromSeconds(5)), "The clear never requested its commit turn.");
            });
        };
        var pushed = Pump(scene.Region.PushAsync(NavigationTarget.Own<object>(dialog)));
        Require(pushed is NavigationResult<object>.Committed, $"The push gave {pushed}.");
        PumpUntil(() => clear is not null, "the clear");
        var cleared = Pump(clear!);
        Require(cleared is NavigationResult<object>.Committed, $"The clear gave {cleared}.");
        Drain();
        Require(scene.Windows.Count == 0 && LoadedWindows.Skip(loaded).All(window => !ReferenceEquals(window.DataContext, dialog)),
            "A window opened for an entry dismissed before the reconcile.");
    }

    // Show() throws: 1083 and exactly one ClearAsync; with a vetoing guard, no second 1083 or clear.
    public static void ShowFailureClearsOnce()
    {
        var failing = new Style(typeof(Window));
        failing.Setters.Add(new Setter(Window.AllowsTransparencyProperty, true));
        failing.Setters.Add(new Setter(Window.WindowStyleProperty, WindowStyle.SingleBorderWindow));
        using var scene = new DialogScene(host => host.WindowStyle = failing);
        var request = scene.Region.PushForResult<bool>(NavigationTarget.Own<object>(new DialogViewModel("fails")));
        Require(Pump(request.Completion) is NavigationCompletion<bool>.Dismissed, "A failed Show did not dismiss the dialog.");
        Require(scene.Fixture.Logs.Count(1083) == 1 && scene.Windows.Count == 0, $"Expected one 1083, got {scene.Fixture.Logs.Count(1083)}.");

        var stubborn = new DialogViewModel("stubborn") { Allow = () => false };
        Pump(scene.Region.PushAsync(NavigationTarget.Own<object>(stubborn)));
        PumpUntil(() => stubborn.Guards == 1, "the vetoed clear");
        Drain();
        Drain();
        Require(scene.Fixture.Logs.Count(1083) == 2 && stubborn.Guards == 1 && ReferenceEquals(scene.Region.Current, stubborn),
            $"After a vetoed clear: {scene.Fixture.Logs.Count(1083)} × 1083, {stubborn.Guards} guard runs.");
        stubborn.Allow = () => true;
    }

    public static void ApplicationModality()
    {
        var other = ShowWindow();
        try
        {
            using var scene = new DialogScene();
            var vm = new DialogViewModel("modal");
            Pump(scene.Region.PushAsync(NavigationTarget.Own<object>(vm)));
            var window = scene.WaitForWindows(1);
            Require(!IsEnabled(scene.Owner) && !IsEnabled(other) && IsEnabled(window), "Application modality didn't disable the thread's windows.");
            Require(System.Windows.Interop.ComponentDispatcher.IsThreadModal, "IsThreadModal is false with a dialog open.");
            var ownerEnabledAtClose = false;
            window.Closing += (_, _) => ownerEnabledAtClose = IsEnabled(scene.Owner);
            Pump(vm.Entry!.DismissAsync());
            PumpUntil(() => !IsOpen(window), "the dialog to close");
            Require(ownerEnabledAtClose, "The owner was re-enabled only after the dialog closed.");
            Require(IsEnabled(scene.Owner) && IsEnabled(other), "The windows weren't re-enabled.");
            Require(!ComponentDispatcher.IsThreadModal, "IsThreadModal stayed true.");
        }
        finally { other.Close(); }
    }

    public static void OwnerModality()
    {
        var other = ShowWindow();
        try
        {
            using var scene = new DialogScene(host => host.Modality = NavigationDialogModality.Owner);
            var vm = new DialogViewModel("window-modal");
            Pump(scene.Region.PushAsync(NavigationTarget.Own<object>(vm)));
            var window = scene.WaitForWindows(1);
            Require(!IsEnabled(scene.Owner) && IsEnabled(other) && IsEnabled(window), "Owner modality disabled the wrong windows.");
            Pump(vm.Entry!.DismissAsync());
            PumpUntil(() => !IsOpen(window), "the dialog to close");
            Require(IsEnabled(scene.Owner), "The owner wasn't re-enabled.");
        }
        finally { other.Close(); }
    }

    // A window the app disabled stays disabled; two hosts re-enable a shared owner only after both close.
    public static void SharedRefcount()
    {
        var appDisabled = ShowWindow();
        EnableWindow(Handle(appDisabled), false);
        using var fixture = new NavFixture();
        var first = fixture.Region();
        var second = fixture.Region();
        var firstHost = new NavigationDialogHost { Region = first };
        var secondHost = new NavigationDialogHost { Region = second };
        var owner = ShowWindow(new Grid { Children = { firstHost, secondHost } }, window => AddTemplates(window, typeof(DialogViewModel)));
        try
        {
            var a = new DialogViewModel("a");
            var b = new DialogViewModel("b");
            Pump(first.PushAsync(NavigationTarget.Own<object>(a)));
            PumpUntil(() => firstHost.DialogWindows.Count == 1 && firstHost.DialogWindows[0].IsVisible, "the first dialog");
            Pump(second.PushAsync(NavigationTarget.Own<object>(b)));
            PumpUntil(() => secondHost.DialogWindows.Count == 1 && secondHost.DialogWindows[0].IsVisible, "the second dialog");
            var aWindow = firstHost.DialogWindows[0];
            var bWindow = secondHost.DialogWindows[0];
            Require(!IsEnabled(owner) && !IsEnabled(aWindow) && IsEnabled(bWindow), "The second dialog isn't modal over the first.");

            Pump(a.Entry!.DismissAsync());
            PumpUntil(() => !IsOpen(aWindow), "the first dialog to close");
            Require(!IsEnabled(owner) && IsEnabled(bWindow), "The owner was re-enabled while the second dialog is open.");
            Pump(b.Entry!.DismissAsync());
            PumpUntil(() => !IsOpen(bWindow), "the second dialog to close");
            Require(IsEnabled(owner), "The owner wasn't re-enabled after both dialogs closed.");
            Require(!IsEnabled(appDisabled), "A host re-enabled a window the app disabled.");
        }
        finally
        {
            owner.Close();
            EnableWindow(Handle(appDisabled), true);
            appDisabled.Close();
        }
    }

    // The table counts a window that something re-enabled while held, and an entry dies with its window, so a
    // recycled handle never inherits counts.
    public static void ModalityTableHolds()
    {
        var window = ShowWindow();
        var hwnd = Handle(window);
        try
        {
            var first = DialogModalityTable.TryDisable(hwnd);
            Require(first is not null && !IsWindowEnabled(hwnd) && DialogModalityTable.CountOf(hwnd) == 1, "The first hold did not disable the window.");
            EnableWindow(hwnd, true);
            var second = DialogModalityTable.TryDisable(hwnd);
            Require(ReferenceEquals(first, second) && DialogModalityTable.CountOf(hwnd) == 2 && !IsWindowEnabled(hwnd),
                $"A re-enabled held window has {DialogModalityTable.CountOf(hwnd)} counts.");
            DialogModalityTable.Release(first!);
            Require(!IsWindowEnabled(hwnd), "One release re-enabled a window with two counts.");
            DialogModalityTable.Release(second!);
            Require(IsWindowEnabled(hwnd) && DialogModalityTable.CountOf(hwnd) == 0, "The last release did not re-enable the window.");

            var held = DialogModalityTable.TryDisable(hwnd);
            EnableWindow(hwnd, true);
            window.Close();
            Drain();
            Require(held is { Dead: true } && DialogModalityTable.CountOf(hwnd) == 0, "A destroyed window's entry stayed in the table.");
            DialogModalityTable.Release(held!);
        }
        finally { window.Close(); }
    }

    public static void Escape()
    {
        using var scene = new DialogScene();
        var request = scene.Region.PushForResult<bool>(NavigationTarget.Own<object>(new DialogViewModel("esc")));
        var window = scene.WaitForWindows(1);
        PressEscape(window);
        Require(Pump(request.Completion) is NavigationCompletion<bool>.Dismissed, "Esc did not dismiss the dialog.");
        PumpUntil(() => !IsOpen(window), "the window to close");

        scene.Host.CloseOnEscape = false;
        Pump(scene.Region.PushAsync(NavigationTarget.Own<object>(new DialogViewModel("kept"))));
        window = scene.WaitForWindows(1);
        PressEscape(window);
        Drain();
        Require(IsOpen(window) && scene.Region.CurrentEntry is not null, "Esc dismissed with CloseOnEscape off.");
    }

    // The host never cancels its owner's close: the owner and its dialogs close, and one ClearAsync follows. With a
    // vetoing guard the entries stay without windows, and get windows again under a new owner.
    public static void OwnerClose()
    {
        using var fixture = new NavFixture();
        using (var scene = new DialogScene(fixture: new NavFixture()))
        {
            var request = scene.Region.PushForResult<bool>(NavigationTarget.Own<object>(new DialogViewModel("closes")));
            var window = scene.WaitForWindows(1);
            scene.Owner.Close();
            Require(!IsOpen(scene.Owner) && !IsOpen(window), "The owner's close was cancelled.");
            Require(Pump(request.Completion) is NavigationCompletion<bool>.Dismissed, "The owner's close did not dismiss the dialog.");
            Require(scene.Region.CurrentEntry is null, "The dialog entry stayed.");
        }

        var region = fixture.Region();
        var stubborn = new DialogViewModel("stubborn") { Allow = () => false };
        var host = new NavigationDialogHost { Region = region };
        var owner = ShowWindow(new Grid { Children = { host } }, window => AddTemplates(window, typeof(DialogViewModel)));
        Pump(region.PushAsync(NavigationTarget.Own<object>(stubborn)));
        PumpUntil(() => host.DialogWindows.Count == 1 && host.DialogWindows[0].IsVisible, "the dialog");
        var first = host.DialogWindows[0];
        owner.Close();
        PumpUntil(() => stubborn.Guards == 1, "the owner-close clear");
        Drain();
        Require(!IsOpen(owner) && !IsOpen(first) && ReferenceEquals(region.Current, stubborn) && host.DialogWindows.Count == 0,
            "A vetoed owner-close clear did not leave the entry without a window.");

        var nextHost = new NavigationDialogHost { Region = region };
        var next = ShowWindow(new Grid { Children = { nextHost } }, window => AddTemplates(window, typeof(DialogViewModel)));
        try
        {
            PumpUntil(() => nextHost.DialogWindows.Count == 1 && nextHost.DialogWindows[0].IsVisible, "the dialog under the new owner");
            Require(ReferenceEquals(nextHost.DialogWindows[0].Owner, next), "The dialog came back under the wrong owner.");
            stubborn.Allow = () => true;
            Pump(stubborn.Entry!.DismissAsync());
            PumpUntil(() => nextHost.DialogWindows.Count == 0, "the dialog to close");
        }
        finally { next.Close(); }
    }

    // An async guard confirms through the dialog host with a real window.
    public static void LeaveConfirmationInDialog()
    {
        using var scene = new DialogScene();
        var home = new Page("home");
        var main = scene.Fixture.Region(NavigationTarget.Own<object>(home));
        var editor = new EditorViewModel();
        editor.Leave = LeaveConfirmation.InDialog(scene.Region, () => NavigationTarget.Own<object>(new DialogViewModel("discard?")),
            () => editor.Dirty, () => editor.Discarded = true);
        Pump(main.PushAsync(NavigationTarget.Own<object>(editor)));
        var back = main.BackAsync().AsTask();
        var window = scene.WaitForWindows(1);
        var question = (DialogViewModel)window.DataContext;
        PumpUntil(() => question.Entry is not null, "the question's entry");
        Pump(question.Entry!.CompleteAsync(true));
        var result = Pump(back);
        Require(result is NavigationResult<object>.Committed && ReferenceEquals(main.Current, home) && editor.Discarded,
            $"The confirmed Back gave {result}, discarded: {editor.Discarded}.");
        PumpUntil(() => !IsOpen(window), "the question's window to close");
    }

    // Last: Application.Shutdown with open dialogs closes everything, and the navigator retires every entry.
    public static void ApplicationShutdown()
    {
        var scene = new DialogScene();
        Pump(scene.Region.PushAsync(NavigationTarget.Own<object>(new DialogViewModel("lower"))));
        scene.WaitForWindows(1);
        Pump(scene.Region.PushAsync(NavigationTarget.Own<object>(new DialogViewModel("upper"))));
        var upper = scene.WaitForWindows(2);
        var dispatcher = Dispatcher.CurrentDispatcher;
        Application.Current.Shutdown();
        try { PumpUntil(() => !IsOpen(scene.Owner) && !IsOpen(upper), "the windows to close at shutdown"); }
        catch (InvalidOperationException) when (dispatcher.HasShutdownStarted) { }
        Require(!IsOpen(scene.Owner) && !IsOpen(upper), "Application.Shutdown left windows open.");
        if (!dispatcher.HasShutdownStarted)
            PumpUntil(() => scene.Region.CurrentEntry is null || scene.Fixture.Context.Dispatcher.HasShutdownStarted, "the owner-close clear");
        if (dispatcher.HasShutdownStarted) Require(scene.Fixture.Navigator.DisposeAsync().AsTask().Wait(WaitLimit), "Navigator disposal timed out.");
        else Pump(scene.Fixture.Navigator.DisposeAsync().AsTask(), "navigator disposal");
        Require(scene.Fixture.Navigator.UnretiredEntryCount == 0, $"{scene.Fixture.Navigator.UnretiredEntryCount} entries were not retired.");
        scene.Fixture.Context.Dispose();
        if (!dispatcher.HasShutdownStarted) dispatcher.InvokeShutdown();
    }
}
