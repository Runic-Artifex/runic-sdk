using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Runic.Navigation.Wpf.Tests;

// W240-005b: the WPF hosts on a real dispatcher (design record W240-001 §8 and §11). One Application with
// OnExplicitShutdown; async steps pump a DispatcherFrame with a timeout and never block the UI thread. Windows
// are shown off-screen. Shutdown tests run on secondary STA threads; Application.Shutdown runs last.
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            // Before any Application or dispatcher exists on this thread.
            Run("registration without an Application", RegistrationTests.WithoutApplication);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var unhandled = new List<Exception>();
            app.DispatcherUnhandledException += (_, e) =>
            {
                unhandled.Add(e.Exception);
                e.Handled = true;
            };
            Ui.Initialize();

            Run("registration", RegistrationTests.WithApplication);
            Run("context basics", ContextTests.Basics);
            Run("nested turn logs 1081 once", ContextTests.NestedTurnLogsOnce);
            Run("hook scheduler outcomes", ContextTests.HookSchedulerOutcomes);
            Run("hooks change UI-affine models", ContextTests.HooksRunOnTheUiThread);
            Run("guard pumps while another region commits", ContextTests.GuardPumpsWhileAnotherRegionCommits);
            Run("nested-pump input is not the hook", ContextTests.NestedPumpInputIsNotTheHook);
            Run("PropertyChanged on the UI thread", ContextTests.PropertyChangedOnTheUiThread);
            Run("shutdown on a secondary thread", ContextTests.ShutdownOnSecondaryThread);
            Run("presenter per entry", HostTests.PresenterPerEntry);
            Run("remount", HostTests.Remount);
            Run("view locator order", HostTests.LocatorOrder);
            Run("view constructors get the content", HostTests.LocatorPassesTheContent);
            Run("failed view and rebinding", HostTests.FailedViewAndRebinding);
            Run("naming convention", HostTests.NamingConvention);
            Run("BrowseBack", HostTests.BrowseBack);
            Run("double BrowseBack, then Back from code", HostTests.DoubleBrowseBackThenBackFromCode);
            Run("unloaded host is collectable", HostTests.UnloadedHostIsCollectable);
            Run("selector guard veto and external navigation", SelectorTests.GuardVetoAndExternalSelection);
            Run("rapid selection and late guard", SelectorTests.RapidSelectionAndLateGuard);
            Run("selector unload and remount", SelectorTests.UnloadCancelsAndRemounts);
            Run("selecting current cancels without replacing", SelectorTests.SelectingCurrentCancelsWithoutReplacing);
            Run("invalid selection restores and logs", SelectorTests.InvalidSelectionRestoresAndLogs);
            Run("plain view location and borrowed lifetime", PlainViewHostTests.RegisteredLocationAndBorrowedLifetime);
            Run("plain view location and failure", PlainViewHostTests.ExplicitLocationAndFailure);
            Run("dialog results", DialogTests.Results);
            Run("dialog guard veto", DialogTests.GuardVetoKeepsTheWindow);
            Run("nested dialogs", DialogTests.NestedDialogsCloseTopDown);
            Run("dismissed before reconcile", DialogTests.DismissedBeforeReconcile);
            Run("Show failure", DialogTests.ShowFailureClearsOnce);
            Run("application modality", DialogTests.ApplicationModality);
            Run("owner modality", DialogTests.OwnerModality);
            Run("shared refcount", DialogTests.SharedRefcount);
            Run("modality table holds", DialogTests.ModalityTableHolds);
            Run("Esc", DialogTests.Escape);
            Run("owner close", DialogTests.OwnerClose);
            Run("leave confirmation in a dialog", DialogTests.LeaveConfirmationInDialog);
            // Last: the main dispatcher can shut down only once.
            Run("Application.Shutdown", DialogTests.ApplicationShutdown);

            if (unhandled.Count > 0)
                Failures.Add(("dispatcher", new InvalidOperationException($"Exceptions reached the dispatcher: {string.Join(Environment.NewLine, unhandled)}")));
            if (Failures.Count == 0)
            {
                Console.WriteLine("Runic.Navigation.Wpf tests passed.");
                return 0;
            }
            Console.Error.WriteLine($"{Failures.Count} Runic.Navigation.Wpf test(s) failed:");
            foreach (var (name, failure) in Failures) Console.Error.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static readonly List<(string Name, Exception Error)> Failures = [];

    // Runs every test; a failure is reported at once and again in the summary, and the run goes on.
    private static void Run(string name, Action test)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            test();
            Console.WriteLine($"PASS {name} ({watch.ElapsedMilliseconds} ms)");
        }
        catch (Exception error)
        {
            Failures.Add((name, error));
            Console.WriteLine($"FAIL {name} ({watch.ElapsedMilliseconds} ms): {error}");
        }
        if (Dispatcher.FromThread(Thread.CurrentThread) is { HasShutdownStarted: false })
        {
            try { Ui.CloseStrayWindows(name); }
            catch (Exception error) { Failures.Add(($"{name} (cleanup)", error)); }
        }
    }
}
