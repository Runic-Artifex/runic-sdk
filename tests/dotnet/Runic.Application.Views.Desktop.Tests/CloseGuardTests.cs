using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Navigation;
using TUnit.Core;
using static DesktopChecks;


// RunicWindow.GuardClose on Runic Desktop: a user close of the embedded window asks the departure guards of the
// navigation regions the Window presents (W250-036). The window host is headless; UserClose calls the
// CloseRequested callback the way GTK's delete-event, WebView2's WM_CLOSE and AppKit's windowShouldClose do.
internal sealed class CloseGuardTests
{
    // A rejecting guard keeps the window open with its entry; the platform callback returns at once.
    [Test, NotInParallel(Order = 19)]
    public async Task ARejectingLeaveGuardKeepsTheWindowOpen()
    {
        await using var fixture = await GuardFixture.OpenAsync<GuardedWindow>();
        fixture.Script.Decide = static _ => ValueTask.FromResult(false);

        fixture.Native.UserClose();
        await fixture.Script.Asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // A second request joins the decision the native close started.
        Require(!await fixture.Presentation.RequestCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
            "A close request completed as allowed although the leave guard rejected it.");
        Require(fixture.Presentation.IsOpen && fixture.Native.IsOpen, "A rejected leave guard did not keep the window open.");
        Require(fixture.Script.AskCount == 1, $"The leave guard was asked {fixture.Script.AskCount} times for one decision.");
        Require(fixture.ViewModel.Main.Current is GuardedPage, "A rejected close cleared the navigation region.");
    }

    // An allowing guard clears the region and the window closes.
    [Test, NotInParallel(Order = 20)]
    public async Task AnAllowingLeaveGuardClosesTheWindow()
    {
        await using var fixture = await GuardFixture.OpenAsync<GuardedWindow>();
        var closed = fixture.Window.WaitForCloseAsync().AsTask();

        fixture.Native.UserClose();
        await closed.WaitAsync(TimeSpan.FromSeconds(5));
        Require(fixture.Script.AskCount == 1, "The window closed without asking the leave guard once.");
        Require(!fixture.Native.IsOpen, "The native window stayed open after the guard allowed the close.");
    }

    // A navigator guard asked before the entries (#412), such as "no navigation while an operation runs", vetoes a user close
    // before the page asks its question: the window stays open with its entry. Once it allows, the page is asked after it.
    [Test, NotInParallel(Order = 36)]
    public async Task ABeforeEntriesNavigatorGuardVetoesTheCloseBeforeThePageIsAsked()
    {
        await using var fixture = await GuardFixture.OpenAsync<GuardedWindow>();
        var asked = new ConcurrentQueue<string>();
        var operation = new OperationGuard(asked);
        fixture.Script.Decide = _ =>
        {
            asked.Enqueue("page");
            return ValueTask.FromResult(true);
        };
        using var registration = fixture.ViewModel.Main.Navigator.AddDepartureGuard(operation,
            new NavigationRegionGuardOptions { Order = NavigationRegionGuardOrder.BeforeEntries, AskOnRetain = true });

        fixture.Native.UserClose();
        await operation.Asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!await fixture.Presentation.RequestCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
            "A close request completed as allowed although the operation guard vetoed it.");
        Require(fixture.Presentation.IsOpen && fixture.Native.IsOpen && fixture.ViewModel.Main.Current is GuardedPage,
            "A vetoing operation guard did not keep the window open with its entry.");
        // The request joins the user close's decision, or asks again when that decision has already ended.
        Require(fixture.Script.AskCount == 0 && !asked.IsEmpty && asked.All(static name => name == "operation"),
            $"The page was asked although the operation guard vetoed first: {string.Join(",", asked)}.");

        operation.Running = false;
        asked.Clear();
        var closed = fixture.Window.WaitForCloseAsync().AsTask();
        fixture.Native.UserClose();
        await closed.WaitAsync(TimeSpan.FromSeconds(5));
        Require(string.Join(",", asked) == "operation,page", $"The close asked {string.Join(",", asked)}.");
    }

    // A failing guard is logged and keeps the window open; so does an overriding CanCloseAsync that throws.
    [Test, NotInParallel(Order = 21)]
    public async Task AFailingGuardIsLoggedAndKeepsTheWindowOpen()
    {
        await using (var fixture = await GuardFixture.OpenAsync<GuardedWindow>())
        {
            fixture.Script.Decide = static _ => throw new InvalidOperationException("The draft store is unavailable.");
            Require(!await fixture.Presentation.RequestCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
                "A failing leave guard allowed the close.");
            Require(fixture.Presentation.IsOpen && fixture.ViewModel.Main.Current is GuardedPage,
                "A failing leave guard did not keep the window open with its entry.");
            Require(fixture.Logs.Has(1060, LogLevel.Error), "The failing leave guard was not logged as NavigationGuardFailed.");
        }

        await using (var fixture = await GuardFixture.OpenAsync<ThrowingGuardWindow>())
        {
            fixture.Native.UserClose();
            await fixture.Logs.WaitForAsync(1052);
            Require(!await fixture.Presentation.RequestCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
                "A throwing CanCloseAsync allowed the close.");
            Require(fixture.Presentation.IsOpen, "A throwing CanCloseAsync did not keep the window open.");
            var entry = fixture.Logs.Entries.First(static entry => entry.EventId == 1052);
            Require(entry is { Level: LogLevel.Error, Category: "Runic.Application.Views" } &&
                entry.Message.Contains(nameof(ThrowingGuardWindow), StringComparison.Ordinal) &&
                entry.Exception is TimeoutException,
                $"WindowCloseGuardFailed did not name the Window and keep the exception: {entry}");
            Require(fixture.Logs.Entries.All(static entry => entry.EventId != 3001),
                "Runic Desktop logged the guard failure a second time.");
        }
    }

    // A guard that never answers holds no thread and no event loop: further requests join it, and the application's own
    // close neither waits for it nor lets it decide; it cancels the guard's token.
    [Test, NotInParallel(Order = 22)]
    public async Task AGuardThatNeverAnswersDoesNotHangTheClose()
    {
        var fixture = await GuardFixture.OpenAsync<GuardedWindow>();
        await using (fixture)
        {
            var guardToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Script.Decide = async cancellationToken =>
            {
                guardToken.TrySetResult(cancellationToken);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return true;
            };

            var callback = Stopwatch.StartNew();
            fixture.Native.UserClose();
            fixture.Native.UserClose();
            callback.Stop();
            Require(callback.Elapsed < TimeSpan.FromSeconds(1), $"The platform close callback blocked for {callback.Elapsed}.");
            var token = await guardToken.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = fixture.Presentation.RequestCloseAsync().AsTask();
            using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            {
                try
                {
                    await fixture.Presentation.RequestCloseAsync(stop.Token);
                    throw new InvalidOperationException("A close request completed while its guard was still deciding.");
                }
                catch (OperationCanceledException) { }
            }
            Require(fixture.Presentation.IsOpen && fixture.Script.AskCount == 1 && !pending.IsCompleted,
                "Close requests did not share the pending guard decision.");

            var closing = Stopwatch.StartNew();
            var result = await fixture.Window.CloseAsync(TimeSpan.FromSeconds(5)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            closing.Stop();
            Require(result.Drained && closing.Elapsed < TimeSpan.FromSeconds(5),
                $"Closing the window waited for a guard that never answered ({closing.Elapsed}).");
            Require(token.IsCancellationRequested, "Closing the window did not cancel the pending guard.");
            Require(!await pending.WaitAsync(TimeSpan.FromSeconds(5)), "An abandoned guard decision reported the close as allowed.");
            Require(!fixture.Native.IsOpen, "The native window stayed open.");
        }
    }

    // W315-020 (#350): a close clears with Admission = Supersede, so a region that rejects requests while busy still
    // closes during a request: the clear supersedes it and asks the guard again.
    [Test, NotInParallel(Order = 32)]
    public async Task ACloseSupersedesARequestOfABusyRegion()
    {
        await using var fixture = await GuardFixture.OpenAsync<GuardedWindow>(admission: NavigationAdmission.RejectWhileBusy);
        var pushGuard = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        fixture.Script.Decide = async cancellationToken =>
        {
            if (Interlocked.Increment(ref asked) > 1) return true;
            pushGuard.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return true;
        };
        var push = fixture.ViewModel.Main.PushAsync(NavigationTarget.Own(new GuardedPage(fixture.Script))).AsTask();
        var token = await pushGuard.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(fixture.ViewModel.Main.IsBusy && fixture.ViewModel.Main.Admission == NavigationAdmission.RejectWhileBusy,
            "The Main region was not busy with the push.");

        var closed = fixture.Window.WaitForCloseAsync().AsTask();
        Require(await fixture.Presentation.RequestCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
            "A close of a busy RejectWhileBusy region did not close the window.");
        await closed.WaitAsync(TimeSpan.FromSeconds(5));
        Require(await push.WaitAsync(TimeSpan.FromSeconds(5)) is NavigationResult<GuardedPage>.Superseded && token.IsCancellationRequested
            && fixture.Script.AskCount == 2, "The close did not supersede the busy region's request and ask the guard again.");
    }

    // Without GuardClose a user close closes at once and asks nothing.
    [Test, NotInParallel(Order = 23)]
    public async Task AnUnguardedWindowClosesWithoutAsking()
    {
        await using var fixture = await GuardFixture.OpenAsync<UnguardedWindow>();
        Require(!fixture.Native.ConfirmsClose, "An unguarded Window configured close confirmation.");
        var closed = fixture.Window.WaitForCloseAsync().AsTask();
        fixture.Native.UserClose();
        await closed.WaitAsync(TimeSpan.FromSeconds(5));
        Require(fixture.Script.AskCount == 0, "An unguarded Window asked its leave guard.");
    }

    // The Window's guard runs before the host's own ConfirmCloseAsync, which decides only when the guard allowed it; a
    // window host that cannot keep its window open opens the Window unguarded with a warning instead of failing.
    [Test, NotInParallel(Order = 24)]
    public async Task GuardsComposeWithConfirmCloseAndDegradeWithAWarning()
    {
        var confirmed = 0;
        var order = new List<string>();
        await using (var fixture = await GuardFixture.OpenAsync<GuardedWindow>(confirmClose: _ =>
        {
            lock (order) order.Add("host");
            Interlocked.Increment(ref confirmed);
            return ValueTask.FromResult(false);
        }))
        {
            fixture.Script.Decide = _ => { lock (order) order.Add("guard"); return ValueTask.FromResult(false); };
            Require(!await fixture.Presentation.RequestCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
                "A rejecting guard allowed the close.");
            Require(confirmed == 0, "The host's ConfirmCloseAsync ran after the Window's guard rejected the close.");

            fixture.Script.Decide = _ => { lock (order) order.Add("guard"); return ValueTask.FromResult(true); };
            Require(!await fixture.Presentation.RequestCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
                "The close ignored the host's ConfirmCloseAsync.");
            lock (order)
                Require(order.SequenceEqual(["guard", "guard", "host"]), $"The close decisions ran as {string.Join(", ", order)}.");
            Require(fixture.Presentation.IsOpen, "A rejected host confirmation closed the window.");
        }

        await using (var fixture = await GuardFixture.OpenAsync<GuardedWindow>(confirmsClose: false))
        {
            Require(fixture.Presentation.IsOpen && !fixture.Presentation.Capabilities.HasFlag(DesktopWindowCapabilities.CloseConfirmation),
                "A window host without close confirmation did not open the guarded Window.");
            var warning = fixture.Logs.Entries.SingleOrDefault(static entry => entry.EventId == 1053);
            Require(warning is { Level: LogLevel.Warning } &&
                warning.Message.Contains(nameof(GuardedWindow), StringComparison.Ordinal) &&
                warning.Message.Contains("embedded-window host", StringComparison.Ordinal),
                $"Opening a guarded Window unguarded did not log WindowCloseGuardUnavailable once: {warning}");
        }
    }

    // RunicWindow.RequestCloseAsync on the embedded window is the native close request: it shares the user close's
    // decision, and returns whether the window closed.
    [Test, NotInParallel(Order = 25)]
    public async Task RequestCloseAsksTheSameGuardAsAUserClose()
    {
        await using var fixture = await GuardFixture.OpenAsync<GuardedWindow>();
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Script.Decide = _ => new(answer.Task);

        fixture.Native.UserClose();
        await fixture.Script.Asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var request = fixture.Opened.RequestCloseAsync().AsTask();
        answer.SetResult(false);
        Require(!await request.WaitAsync(TimeSpan.FromSeconds(5)), "RequestCloseAsync closed a window whose guard rejected.");
        Require(fixture.Script.AskCount == 1 && fixture.Presentation.IsOpen,
            "RequestCloseAsync did not join the user close's decision, or the window closed.");

        fixture.Script.Decide = static _ => ValueTask.FromResult(true);
        var closed = fixture.Opened.WaitForCloseAsync().AsTask();
        Require(await fixture.Opened.RequestCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
            "RequestCloseAsync did not report the allowed close.");
        await closed.WaitAsync(TimeSpan.FromSeconds(5));
        Require(fixture.Script.AskCount == 2 && !fixture.Native.IsOpen, "The allowed request did not close the native window.");
        Require(await fixture.Opened.RequestCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)) && fixture.Script.AskCount == 2,
            "A request on a closed window asked again or reported it open.");
    }

    // Without GuardClose RequestCloseAsync closes; with it, a host whose user close cannot be guarded still guards the
    // request, which shares one decision, and CloseAsync still closes without asking.
    [Test, NotInParallel(Order = 26)]
    public async Task RequestCloseGuardsEveryHostAndCloseAsyncStillDoesNotAsk()
    {
        await using (var fixture = await GuardFixture.OpenAsync<UnguardedWindow>())
        {
            Require(await fixture.Opened.RequestCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
                "RequestCloseAsync kept an unguarded Window open.");
            await fixture.Opened.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Require(fixture.Script.AskCount == 0, "An unguarded Window asked its leave guard.");
        }

        await using (var fixture = await GuardFixture.OpenAsync<GuardedWindow>(confirmsClose: false))
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Script.Decide = _ => new(answer.Task);
            var first = fixture.Opened.RequestCloseAsync().AsTask();
            await fixture.Script.Asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = fixture.Opened.RequestCloseAsync().AsTask();
            answer.SetResult(false);
            Require(!await first.WaitAsync(TimeSpan.FromSeconds(5)) && !await second.WaitAsync(TimeSpan.FromSeconds(5)),
                "A rejected request closed a window whose host cannot confirm closes.");
            Require(fixture.Script.AskCount == 1 && fixture.Presentation.IsOpen,
                "Concurrent requests did not share one decision, or the window closed.");

            fixture.Script.Decide = static _ => ValueTask.FromResult(true);
            var closed = fixture.Opened.WaitForCloseAsync().AsTask();
            Require(await fixture.Opened.RequestCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
                "An allowed request did not report the close.");
            await closed.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!fixture.Native.IsOpen, "An allowed request left the native window open.");
        }

        await using (var fixture = await GuardFixture.OpenAsync<GuardedWindow>(confirmsClose: false))
        {
            var result = await fixture.Opened.CloseAsync(TimeSpan.FromSeconds(5)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Require(result.Drained && fixture.Script.AskCount == 0 && !fixture.Native.IsOpen, "CloseAsync asked the guard.");
        }
    }

    // A request whose guard never answers ends when the application closes the window without asking: the guard's
    // token is cancelled and the request reports the window closed.
    [Test, NotInParallel(Order = 27)]
    public async Task CloseAsyncEndsAPendingRequest()
    {
        await using var fixture = await GuardFixture.OpenAsync<GuardedWindow>(confirmsClose: false);
        var guardToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Script.Decide = async cancellationToken =>
        {
            guardToken.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return true;
        };
        var request = fixture.Opened.RequestCloseAsync().AsTask();
        var token = await guardToken.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            try
            {
                await fixture.Opened.RequestCloseAsync(stop.Token);
                throw new InvalidOperationException("A request completed while its guard was still deciding.");
            }
            catch (OperationCanceledException) { }
        }
        Require(!request.IsCompleted && !token.IsCancellationRequested, "Cancelling one caller's wait ended the shared decision.");

        _ = await fixture.Opened.CloseAsync(TimeSpan.FromSeconds(5)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Require(await request.WaitAsync(TimeSpan.FromSeconds(5)), "A request ended by CloseAsync did not report the window closed.");
        Require(token.IsCancellationRequested && fixture.Script.AskCount == 1, "CloseAsync did not cancel the pending guard.");
    }

    // A ViewModel closes its own guarded Window through IRunicWindowContext, from a command's model turn, with no
    // holder of the Window: the guard keeps it open, then lets it close. Without close confirmation the request takes
    // the path CS-WebUI and browser windows use.
    [Test, NotInParallel(Order = 29)]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AViewModelClosesItsOwnGuardedWindow(bool confirmsClose)
    {
        await using var fixture = await GuardFixture.OpenAsync<GuardedWindow>(confirmsClose: confirmsClose);
        fixture.Script.Decide = static _ => ValueTask.FromResult(false);
        Require(!await fixture.ViewModel.QuitAsync().WaitAsync(TimeSpan.FromSeconds(5)),
            "The ViewModel's close request reported the window closed although the guard kept it.");
        Require(fixture.Native.IsOpen && fixture.ViewModel.Main.Current is GuardedPage && fixture.Script.AskCount == 1,
            "A kept close request did not leave the window open with its entry after asking the guard once.");

        fixture.Script.Decide = static _ => ValueTask.FromResult(true);
        var closed = fixture.Window.WaitForCloseAsync().AsTask();
        Require(await fixture.ViewModel.QuitAsync().WaitAsync(TimeSpan.FromSeconds(5)),
            "The ViewModel's close request reported the window open although the guard allowed it.");
        await closed.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!fixture.Native.IsOpen && fixture.Script.AskCount == 2, "An allowed close request did not close the native window.");
    }

    // Outside a Window's scope the context has no Window, and says so instead of closing something else.
    [Test]
    public async Task AWindowContextOutsideAWindowScopeThrows()
    {
        await using var provider = new ServiceCollection().AddRunicWindowService()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IRunicWindowContext>();
        Require(ReferenceEquals(context, scope.ServiceProvider.GetRequiredService<IRunicWindowContext>()),
            "One scope resolved two window contexts.");
        try
        {
            await context.RequestCloseAsync();
            throw new InvalidOperationException("Closing through an unbound window context did not fail.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("outside a Window's scope", StringComparison.Ordinal)) { }
    }
}

// One guarded Window on a headless Desktop host whose embedded window confirms closes.
sealed class GuardFixture : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly DesktopHost _host;
    private readonly IRunicWindow _window;

    private GuardFixture(ServiceProvider provider, DesktopHost host, IRunicWindow window, IBridgeWindow bridgeWindow,
        GuardedShellModel viewModel, CloseGuardScript script, RecordingLoggerFactory logs, ConfirmingWindowHost native,
        ConcurrentQueue<DesktopDiagnostic> diagnostics)
    {
        _provider = provider;
        _host = host;
        _window = window;
        Window = bridgeWindow;
        ViewModel = viewModel;
        Script = script;
        Logs = logs;
        Native = native;
        Diagnostics = diagnostics;
    }

    public IBridgeWindow Window { get; }
    // The Window itself, as application code holds it.
    public IRunicWindow Opened => _window;
    public DesktopWindow Presentation => ((DesktopBridgeWindow<GuardedShellModel>)Window).Window;
    public DesktopSurface Surface => ((DesktopBridgeWindow<GuardedShellModel>)Window).Surface;
    // What the host reported to its DiagnosticSink.
    public ConcurrentQueue<DesktopDiagnostic> Diagnostics { get; }
    public GuardedShellModel ViewModel { get; }
    public CloseGuardScript Script { get; }
    public RecordingLoggerFactory Logs { get; }
    public ConfirmingWindowHost Native { get; }

    public static async Task<GuardFixture> OpenAsync<TWindow>(Func<CancellationToken, ValueTask<bool>>? confirmClose = null,
        bool confirmsClose = true, NavigationAdmission admission = NavigationAdmission.Supersede) where TWindow : RunicWindow<GuardedShellModel>, new()
    {
        var script = new CloseGuardScript { Admission = admission };
        var logs = new RecordingLoggerFactory();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(logs);
        services.AddSingleton(script);
        services.AddRunicNavigation();
        services.AddRunicWindowService();
        services.AddScoped<GuardedShellModel>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, GuardedShellModel, IDisposable>>(
            _ => (transport, content, model) => new GuardedShellBridge(transport, model, content));
        var provider = services.BuildServiceProvider();
        var factory = new ConfirmingWindowFactory { ConfirmsClose = confirmsClose };
        var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
        var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            WaitForConnection = false,
            DiagnosticSink = diagnostics.Enqueue,
        });
        try
        {
            var windows = new RunicDesktopHost(provider, host, new RunicDesktopHostOptions
            {
                SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
                WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded, ConfirmCloseAsync = confirmClose },
            });
            var window = await windows.OpenWindowAsync<TWindow>();
            return new GuardFixture(provider, host, window, window.Host, window.ViewModel, script, logs, factory.Hosts.Single(), diagnostics);
        }
        catch
        {
            await host.DisposeAsync();
            await provider.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _window.DisposeAsync();
        await _host.DisposeAsync();
        await _provider.DisposeAsync();
    }
}

sealed class GuardedWindow : RunicWindow<GuardedShellModel>
{
    public GuardedWindow() => GuardClose = true;
}

sealed class UnguardedWindow : RunicWindow<GuardedShellModel>;

sealed class ThrowingGuardWindow : RunicWindow<GuardedShellModel>
{
    public ThrowingGuardWindow() => GuardClose = true;

    protected override async ValueTask<bool> CanCloseAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        throw new TimeoutException("The autosave check timed out.");
    }
}

// A navigator guard that vetoes every departure while an operation runs.
sealed class OperationGuard(ConcurrentQueue<string> asked) : INavigationRegionGuard
{
    private volatile bool _running = true;
    public bool Running { get => _running; set => _running = value; }
    public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<bool> CanDepartAsync(NavigationRegionDeparture departure, CancellationToken cancellationToken)
    {
        asked.Enqueue("operation");
        Asked.TrySetResult();
        return ValueTask.FromResult(!Running);
    }
}

// What the page's departure guard answers, and how often it was asked.
sealed class CloseGuardScript
{
    private int _askCount;
    // The admission policy of the shell's Main region.
    public NavigationAdmission Admission { get; init; }
    public Func<CancellationToken, ValueTask<bool>> Decide { get; set; } = static _ => ValueTask.FromResult(true);
    public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int AskCount => Volatile.Read(ref _askCount);

    public ValueTask<bool> AskAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _askCount);
        Asked.TrySetResult();
        return Decide(cancellationToken);
    }
}

sealed class GuardedPage(CloseGuardScript script) : INavigationDepartureGuard
{
    public string Title { get; } = "Draft";
    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        script.AskAsync(cancellationToken);
}

sealed class GuardedShellModel : INotifyPropertyChanged
{
    private readonly IRunicWindowContext _window;
    private readonly IRunicModelContext _context;

    public GuardedShellModel(RunicNavigator navigator, CloseGuardScript script, IRunicWindowContext window, IRunicModelContext context)
    {
        _window = window;
        _context = context;
        Main = navigator.CreateRegion(this, NavigationTarget.Own(new GuardedPage(script)),
            new NavigationRegionOptions { Admission = script.Admission });
    }

    public NavigationRegion<GuardedPage> Main { get; }

    // What a Quit command does: the Bridge starts it in a turn of the Window's model context.
    public async Task<bool> QuitAsync() => await await _context.InvokeAsync(() => _window.RequestCloseAsync().AsTask());

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

// What the generator writes for a ViewModel with one NavigationRegion property: the snapshot presents the region's
// current page as content and names its reference, so the page is served by routes of its own.
sealed class GuardedShellBridge(IBridgeTransport transport, GuardedShellModel vm, WindowContentSession content)
    : ViewModelBridge<GuardedShellModel>(transport, vm, "guardedShell",
        (writer, model, revision, _) => Write(content, writer, model, revision),
        [new PropertyDescriptor<GuardedShellModel>("Main", static vm => vm.Main.Current, null, static vm => vm.Main)], [],
        [], content: content)
{
    private static void Write(WindowContentSession content, Utf8JsonWriter writer, GuardedShellModel vm, long revision)
    {
        writer.WriteStartObject();
        writer.WriteNumber("revision", revision);
        if (vm.Main.Current is { } page)
        {
            writer.WriteString("title", page.Title);
            writer.WriteString("page", content.Present(vm, "Main", "page", page, static (_, _, _) => new PageBridge()).Id);
        }
        else
        {
            content.Clear(vm, "Main");
            writer.WriteNull("title");
            writer.WriteNull("page");
        }
        writer.WriteEndObject();
    }

    // The page's own bridge; the content session serves its Mount and Unmount routes around it.
    private sealed class PageBridge : IDisposable
    {
        public void Dispose() { }
    }
}

sealed class ConfirmingWindowFactory : IDesktopWindowHostFactory
{
    public List<ConfirmingWindowHost> Hosts { get; } = [];
    public bool ConfirmsClose { get; init; } = true;
    public bool IsSupported => true;
    public DesktopWindowCapabilities Capabilities => ConfirmingWindowHost.CapabilitiesFor(ConfirmsClose);

    public IDesktopWindowHost Create()
    {
        var host = new ConfirmingWindowHost { SupportsCloseConfirmation = ConfirmsClose };
        lock (Hosts) Hosts.Add(host);
        return host;
    }
}

// A headless embedded window host. UserClose is the title-bar close button.
sealed class ConfirmingWindowHost : IDesktopWindowHost
{
    private volatile bool _open;
    private Action? _closeRequested;

    public static DesktopWindowCapabilities CapabilitiesFor(bool confirmsClose) =>
        DesktopWindowCapabilities.Focus | DesktopWindowCapabilities.Minimize | DesktopWindowCapabilities.Maximize |
        DesktopWindowCapabilities.Resize | DesktopWindowCapabilities.Move |
        (confirmsClose ? DesktopWindowCapabilities.CloseConfirmation : DesktopWindowCapabilities.None);

    public bool SupportsCloseConfirmation { get; init; }
    public DesktopWindowCapabilities Capabilities => CapabilitiesFor(SupportsCloseConfirmation);
    // Whether the window opened with a CloseRequested callback, which the host calls instead of closing.
    public bool ConfirmsClose => Volatile.Read(ref _closeRequested) is not null;
    public event EventHandler? Closed;
    public bool IsOpen => _open;
    public nint NativeHandle => 0;

    // Returns as soon as the request is handed on, as a platform event handler must.
    public void UserClose()
    {
        if (Volatile.Read(ref _closeRequested) is { } closeRequested) closeRequested();
        else CloseAsync().AsTask().GetAwaiter().GetResult();
    }

    public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _closeRequested, SupportsCloseConfirmation ? options.CloseRequested : null);
        _open = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_open) { _open = false; Closed?.Invoke(this, EventArgs.Empty); }
        return ValueTask.CompletedTask;
    }

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask FocusAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() { _open = false; return ValueTask.CompletedTask; }
}

sealed record LogEntry(string Category, int EventId, LogLevel Level, string Message, Exception? Exception);

sealed class RecordingLoggerFactory : ILoggerFactory
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_entries) return [.. _entries]; }
    }

    public bool Has(int eventId, LogLevel level) => Entries.Any(entry => entry.EventId == eventId && entry.Level == level);

    public async Task WaitForAsync(int eventId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Entries.All(entry => entry.EventId != eventId))
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException($"Event {eventId} was not logged.");
            await Task.Delay(10);
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    private sealed class Logger(RecordingLoggerFactory factory, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (factory._entries) factory._entries.Add(new(category, eventId.Id, logLevel, formatter(state, exception), exception));
        }
    }
}
