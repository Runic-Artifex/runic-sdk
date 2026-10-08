using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Threading;
using static Runic.Navigation.Wpf.Tests.Ui;

namespace Runic.Navigation.Wpf.Tests;

// DispatcherModelContext (W240-001 §8.1) and where hooks run with it.
internal static class ContextTests
{
    private const int ProbeMessage = 0x8000 + 0x2A; // WM_APP + 42

    public static void Basics()
    {
        using var fixture = new NavFixture();
        var context = fixture.Context;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var unhandled = 0;
        DispatcherUnhandledExceptionEventHandler onUnhandled = (_, e) =>
        {
            unhandled++;
            e.Handled = true;
        };
        dispatcher.UnhandledException += onUnhandled;
        try
        {
            Require(context.IsExecuting, "UI-thread code is not executing.");
            Require(!Pump(Task.Run(() => context.IsExecuting)), "Pool code is executing.");

            // Inline and synchronous on the UI thread, including a throw.
            var inline = false;
            var invoked = context.InvokeAsync(() => { inline = true; });
            Require(inline && invoked.IsCompletedSuccessfully, "InvokeAsync on the UI thread did not run inline.");
            Require(Throws<FormatException>(() => _ = context.InvokeAsync<int>(() => throw new FormatException()).AsTask()),
                "An inline turn's exception did not propagate to the caller.");
            var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Require(Throws<OperationCanceledException>(() => _ = context.InvokeAsync(() => 1, cancelled.Token).AsTask()),
                "An inline turn ran with a cancelled token.");

            // Dispatched from the pool.
            var uiThread = Environment.CurrentManagedThreadId;
            var thread = Pump(Task.Run(async () => await context.InvokeAsync(() => Environment.CurrentManagedThreadId)));
            Require(thread == uiThread, "A pool InvokeAsync did not run on the UI thread.");
            var failed = Task.Run(async () => await context.InvokeAsync<int>(() => throw new FormatException("turn")));
            PumpUntil(() => failed.IsCompleted, "the failing turn");
            Require(failed.Exception?.InnerException is FormatException, $"The failing turn gave {failed.Status}.");
            var precancelled = Task.Run(async () => await context.InvokeAsync(() => 1, cancelled.Token));
            PumpUntil(() => precancelled.IsCompleted, "the cancelled turn");
            Require(precancelled.IsCanceled, $"A cancelled pool turn gave {precancelled.Status}.");

            // Posted turns: a throw goes to UnhandledTurnException, or to log 1080 without a handler.
            Exception? reported = null;
            Action<Exception> handler = error => reported = error;
            context.UnhandledTurnException += handler;
            Require(context.TryPost(() => throw new FormatException("posted")), "TryPost refused an open context.");
            PumpUntil(() => reported is not null, "the reported posted turn");
            Require(reported is FormatException, $"The posted turn reported {reported}.");
            context.UnhandledTurnException -= handler;
            Require(context.TryPost(() => throw new FormatException("posted")), "TryPost refused an open context.");
            PumpUntil(() => fixture.Logs.Count(1080) == 1, "log 1080");
            Drain();
            Require(unhandled == 0, "A turn's exception reached Dispatcher.UnhandledException.");
        }
        finally { dispatcher.UnhandledException -= onUnhandled; }
    }

    // A turn dispatched inside a nested pump inside another turn logs 1081, once per context.
    public static void NestedTurnLogsOnce()
    {
        using var fixture = new NavFixture();
        var context = fixture.Context;
        var ran = 0;
        var outer = context.InvokeAsync(() =>
        {
            Require(context.TurnDepth == 1, "The inline turn was not counted.");
            _ = Task.Run(() => context.InvokeAsync(() => { ran++; }).AsTask());
            PumpUntil(() => ran == 1, "the first nested turn");
            _ = Task.Run(() => context.InvokeAsync(() => { ran++; }).AsTask());
            PumpUntil(() => ran == 2, "the second nested turn");
        });
        Require(outer.IsCompletedSuccessfully, "The outer turn did not complete inline.");
        Require(fixture.Logs.Count(1081) == 1, $"Expected one 1081, got {fixture.Logs.Count(1081)}.");
        Require(context.TurnDepth == 0, "The turn depth did not return to 0.");
    }

    public static void HookSchedulerOutcomes()
    {
        var fixture = new NavFixture();
        var context = fixture.Context;

        // Never inline, on the UI thread, with the dispatcher's context, awaits resume there.
        var ran = false;
        SynchronizationContext? captured = null;
        var resumedOnUi = false;
        var task = context.RunHookAsync(async () =>
        {
            ran = true;
            captured = SynchronizationContext.Current;
            Require(context.TurnDepth == 0, "A hook ran as a turn.");
            await Task.Yield();
            resumedOnUi = context.Dispatcher.CheckAccess();
            return 5;
        }, CancellationToken.None);
        Require(!ran, "A hook ran inline in its caller on the UI thread.");
        Require(Pump(task) == 5 && resumedOnUi && captured is DispatcherSynchronizationContext,
            $"The hook ran under {captured?.GetType().Name} and resumed on the UI thread: {resumedOnUi}.");

        // A synchronous throw is the hook's outcome.
        var thrown = context.RunHookAsync<int>(() => throw new FormatException(), CancellationToken.None);
        PumpUntil(() => thrown.IsCompleted, "the throwing hook");
        Require(thrown.Exception?.InnerException is FormatException, $"A throwing hook gave {thrown.Status}.");

        // Cancelled before the operation starts: the hook never runs.
        var cancellation = new CancellationTokenSource();
        var cancelledRan = false;
        var cancelled = context.RunHookAsync(() =>
        {
            cancelledRan = true;
            return Task.FromResult(1);
        }, cancellation.Token);
        cancellation.Cancel();
        PumpUntil(() => cancelled.IsCompleted, "the cancelled hook");
        Drain();
        Require(cancelled.IsCanceled && !cancelledRan, $"A hook cancelled before start gave {cancelled.Status}, ran: {cancelledRan}.");

        // Closed before it starts: ObjectDisposedException, never runs; posted turns are reported dropped.
        var closedRan = false;
        var closed = context.RunHookAsync(() =>
        {
            closedRan = true;
            return Task.FromResult(1);
        }, CancellationToken.None);
        Exception? dropped = null;
        context.UnhandledTurnException += error => dropped = error;
        var posted = false;
        Require(context.TryPost(() => posted = true), "TryPost refused an open context.");
        context.Dispose();
        Drain();
        Require(closed.IsFaulted && closed.Exception!.InnerException is ObjectDisposedException && !closedRan,
            $"A hook pending at close gave {closed.Status}, ran: {closedRan}.");
        Require(!posted && dropped is ObjectDisposedException, $"A posted turn pending at close ran: {posted}, reported {dropped}.");
        Require(!context.IsExecuting && !context.TryPost(() => { }), "A closed context still accepts turns.");
        var late = context.InvokeAsync(() => 1).AsTask();
        Require(late.IsFaulted && late.Exception!.InnerException is ObjectDisposedException, "A closed context ran a turn.");
        var lateHook = context.RunHookAsync(() => Task.FromResult(1), CancellationToken.None);
        Require(lateHook.IsFaulted && lateHook.Exception!.InnerException is ObjectDisposedException, "A closed context ran a hook.");
        fixture.Dispose();
    }

    // Hooks change a DispatcherObject-affine model directly; owned disposal runs on the UI thread outside turns.
    public static void HooksRunOnTheUiThread()
    {
        using var fixture = new NavFixture();
        var region = fixture.Region();
        var page = new AffinePage(fixture.Context);
        var pushed = Pump(region.PushAsync(NavigationTarget.Own<object>(page)));
        Require(pushed is NavigationResult<object>.Committed, $"The push gave {pushed}.");
        Require(page.InitializedOnUi && page.Text == "initialized and resumed", $"InitializeAsync left '{page.Text}'.");
        var cleared = Pump(region.ClearAsync());
        Require(cleared is NavigationResult<object>.Committed, $"The clear gave {cleared}.");
        PumpUntil(() => page.Disposed, "the owned disposal");
        Require(page.GuardedOnUi, "The guard did not run on the UI thread outside a turn.");
        Require(page.DisposedOnUi && page.DisposeTurnDepth == 0 && page.Text == "disposed",
            $"Owned disposal ran on the UI thread: {page.DisposedOnUi}, at turn depth {page.DisposeTurnDepth}.");
    }

    // A guard may pump: another region commits meanwhile, and no 1081 is logged.
    public static void GuardPumpsWhileAnotherRegionCommits()
    {
        using var fixture = new NavFixture();
        var side = fixture.Region();
        NavigationResult<object>? sideResult = null;
        var guarded = new GuardedPage("guarded", () =>
        {
            var other = side.PushAsync(NavigationTarget.Own<object>(new Page("side"))).AsTask();
            PumpUntil(() => other.IsCompleted, "the side region's commit while the guard pumps");
            sideResult = other.Result;
            return ValueTask.FromResult(true);
        });
        var main = fixture.Region(NavigationTarget.Own<object>(guarded));
        var pushed = Pump(main.PushAsync(NavigationTarget.Own<object>(new Page("next"))));
        Require(pushed is NavigationResult<object>.Committed, $"The guarded push gave {pushed}.");
        Require(sideResult is NavigationResult<object>.Committed, $"The side push inside the guard gave {sideResult}.");
        Require(fixture.Logs.Count(1081) == 0, "A pumping hook logged 1081.");
    }

    // The carried-over check: input that a guard's nested pump dispatches runs on the guard's ExecutionContext,
    // but is not the guard's code, so its requests are admitted; the guard's own requests are Reentrant.
    public static void NestedPumpInputIsNotTheHook()
    {
        using var fixture = new NavFixture();
        var marker = new AsyncLocal<string?>();
        using var source = new HwndSource(new HwndSourceParameters("Runic.Navigation.Wpf.Tests probe")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0, // hidden: no WS_VISIBLE
        });
        NavigationRegion<object>? region = null;
        string? seenByInput = null;
        Task<NavigationResult<object>>? fromInput = null;
        Task<NavigationResult<object>>? fromGuard = null;
        source.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message != ProbeMessage) return 0;
            seenByInput = marker.Value;
            fromInput = region!.PushAsync(NavigationTarget.Own<object>(new Page("from-input"))).AsTask();
            handled = true;
            return 0;
        });
        var guards = 0;
        var guarded = new GuardedPage("guarded", () =>
        {
            // The input's push departs this page again; only the first departure pumps.
            if (++guards > 1) return ValueTask.FromResult(true);
            marker.Value = "guard";
            Require(PostMessage(source.Handle, ProbeMessage, 0, 0), "PostMessage failed.");
            PumpUntil(() => fromInput is not null, "the posted input");
            fromGuard = region!.PushAsync(NavigationTarget.Own<object>(new Page("from-guard"))).AsTask();
            return ValueTask.FromResult(true);
        });
        region = fixture.Region(NavigationTarget.Own<object>(guarded));
        Pump(region.PushAsync(NavigationTarget.Own<object>(new Page("next"))));
        Require(seenByInput == "guard", "The input handler did not run on the guard's ExecutionContext; the test proves nothing.");
        var input = Pump(fromInput!);
        Require(input is not NavigationResult<object>.Rejected { Reason: NavigationRejection.Reentrant },
            $"A request from input dispatched by a guard's nested pump gave {input}.");
        var own = Pump(fromGuard!);
        Require(own is NavigationResult<object>.Rejected { Reason: NavigationRejection.Reentrant },
            $"A request from the guard's own code gave {own}.");
        Pump(fixture.Navigator.WhenIdleAsync().AsTask());
        Require(region.Current is Page { Name: "from-input" }, $"The region shows {region.Current}.");
    }

    public static void PropertyChangedOnTheUiThread()
    {
        using var fixture = new NavFixture();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var region = fixture.Region();
        var calls = new ConcurrentQueue<bool>();
        region.PropertyChanged += (_, _) => calls.Enqueue(dispatcher.CheckAccess());
        Pump(region.PushAsync(NavigationTarget.Own<object>(new Page("from-ui"))));
        var fromUi = calls.Count;
        Pump(Task.Run(async () => await region.PushAsync(NavigationTarget.Own<object>(new Page("from-pool")))));
        Require(fromUi > 0 && calls.Count > fromUi, "Commits raised no PropertyChanged.");
        Require(calls.All(onUi => onUi), "PropertyChanged arrived off the UI thread.");
    }

    // The dispatcher shuts down (on a secondary STA thread, since the main one can shut down only once): navigator
    // disposal finishes within 1 s, owned content is disposed on the pool, and later requests are Rejected(Closed).
    public static void ShutdownOnSecondaryThread()
    {
        RunicNavigator? navigator = null;
        NavigationRegion<object>? region = null;
        OwnedProbe? owned = null;
        // A hook that still awaits when the dispatcher shuts down: its continuation must run on the pool (A9).
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = new GatedInitializePage(gate.Task);
        Task<NavigationResult<object>>? gatedPush = null;
        Thread? uiThread = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                uiThread = Thread.CurrentThread;
                var context = new DispatcherModelContext(dispatcher);
                navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
                region = navigator.CreateRegion<object>(new object());
                owned = new OwnedProbe();
                Pump(region.PushAsync(NavigationTarget.Own<object>(owned)));
                Pump(region.PushAsync(NavigationTarget.Own<object>(new Page("top"))));
                gatedPush = region.PushAsync(NavigationTarget.Own<object>(gated)).AsTask();
                PumpUntil(() => gated.Started, "the gated InitializeAsync");
                dispatcher.BeginInvoke(() => dispatcher.InvokeShutdown());
                Dispatcher.Run();
            }
            catch (Exception failure) { error = failure; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(WaitLimit), "The secondary UI thread did not stop.");
        if (error is not null) throw new InvalidOperationException("The secondary UI thread failed.", error);

        var late = Pump(region!.PushAsync(NavigationTarget.Own<object>(new Page("late"))));
        Require(late is NavigationResult<object>.Rejected { Reason: NavigationRejection.Closed }, $"A push after shutdown gave {late}.");
        gate.SetResult();
        var watch = Stopwatch.StartNew();
        Pump(navigator!.DisposeAsync().AsTask(), "navigator disposal after shutdown");
        Require(watch.Elapsed < TimeSpan.FromSeconds(1), $"Navigator disposal after shutdown took {watch.Elapsed}.");
        Require(gated.ResumedOn is { IsThreadPoolThread: true }, $"The hook resumed on {gated.ResumedOn?.Name ?? "no thread"} after shutdown.");
        Require(gatedPush!.IsCompleted, "The gated push did not settle.");
        Require(owned!.DisposedOn is { } disposer && disposer != uiThread && disposer.IsThreadPoolThread,
            $"Owned content was disposed on {owned.DisposedOn?.Name ?? "no thread"}.");
        Require(navigator.UnretiredEntryCount == 0, $"{navigator.UnretiredEntryCount} entries were not retired.");
    }
}
