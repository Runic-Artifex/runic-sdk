extern alias cswebui;
using TUnit.Core;
using CsWebUiUserClose = cswebui::Runic.Application.Views.CsWebUi.CsWebUiUserClose;

// sdk#1148: the user's close of a CS-WebUI WebView window. WebUI's close handler keeps the window open while the Window's
// close guard decides; an approved close goes through WebUiWindow.Close, and RunicWindow.RequestCloseAsync joins the
// same decision. The real WebView2 and WebKitGTK windows are covered by the probes' --probe-webview-close-guard.
internal sealed class CsWebUiUserCloseChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Test]
    public async Task ARefusingGuardKeepsTheWindowOpenAndAnAllowingOneClosesItOnce()
    {
        using var fixture = new Fixture();
        Require(!fixture.Close.OnUserClose(), "The handler let a guarded window close before the guard answered.");
        var first = await fixture.NextQuestion();
        // A second close while the guard asks is ignored, as on Runic Desktop.
        Require(!fixture.Close.OnUserClose(), "A second user close during the decision let the window close.");
        first.SetResult(false);
        await WaitUntil(() => !fixture.Close.IsDeciding);
        Require(fixture.Asked == 1 && fixture.WindowCloses == 0 && fixture.Closings == 0,
            $"Refused: asked {fixture.Asked}, closed {fixture.WindowCloses}, closing {fixture.Closings}.");

        Require(!fixture.Close.OnUserClose(), "The handler let the window close without asking again.");
        (await fixture.NextQuestion()).SetResult(true);
        await WaitUntil(() => fixture.WindowCloses == 1);
        Require(fixture.Asked == 2 && fixture.Closings == 1 && fixture.Requests == 0,
            $"Allowed: asked {fixture.Asked}, closing {fixture.Closings}, released {fixture.Requests}.");
        // CS-WebUI does not call the handler for its own Close; a stray call afterwards lets the window go.
        Require(fixture.Close.OnUserClose() && fixture.Asked == 2, "A closed window was asked again.");
    }

    [Test]
    public async Task ACloseRequestJoinsTheUsersDecision()
    {
        using var fixture = new Fixture();
        Require(!fixture.Close.OnUserClose(), "The handler let a guarded window close.");
        var question = await fixture.NextQuestion();
        var request = fixture.Close.RequestAsync(CancellationToken.None).AsTask();
        question.SetResult(true);
        Require(await request.WaitAsync(Limit), "The joined request did not report the close.");
        Require(fixture.Asked == 1 && fixture.WindowCloses == 1 && fixture.Requests == 0,
            $"Asked {fixture.Asked}, closed {fixture.WindowCloses}, released {fixture.Requests}: the request did not join.");
    }

    [Test]
    public async Task ARequestReleasesTheWindowAsBeforeAndAUserCloseJoinsIt()
    {
        using var fixture = new Fixture();
        var refused = fixture.Close.RequestAsync(CancellationToken.None).AsTask();
        (await fixture.NextQuestion()).SetResult(false);
        Require(!await refused.WaitAsync(Limit), "A refused request reported the window closed.");

        var allowed = fixture.Close.RequestAsync(CancellationToken.None).AsTask();
        var question = await fixture.NextQuestion();
        Require(!fixture.Close.OnUserClose(), "A user close during the request's decision let the window close.");
        question.SetResult(true);
        Require(await allowed.WaitAsync(Limit), "An allowed request did not report the window closed.");
        Require(fixture.Asked == 2 && fixture.Requests == 1 && fixture.WindowCloses == 0 && fixture.Closings == 1,
            $"Asked {fixture.Asked}, released {fixture.Requests}, closed {fixture.WindowCloses}, closing {fixture.Closings}.");
    }

    [Test]
    public async Task TheWindowsOwnCloseAbandonsTheDecision()
    {
        using var fixture = new Fixture();
        Require(!fixture.Close.OnUserClose(), "The handler let a guarded window close.");
        var question = await fixture.NextQuestion();
        fixture.Close.Abandon();
        Require(fixture.LastToken.IsCancellationRequested, "The guard's token was not cancelled by the window's own close.");
        question.SetResult(true);
        await WaitUntil(() => !fixture.Close.IsDeciding);
        Require(fixture.WindowCloses == 0 && fixture.Requests == 0, "An abandoned decision still closed the window.");
        Require(await fixture.Close.RequestAsync(CancellationToken.None) && fixture.Asked == 1, "A closed window was asked again.");
        Require(fixture.Close.OnUserClose(), "The handler kept a window open that closes anyway.");
    }

    // sdk#1190: with a close drain the decided close keeps the window open until the drain ends, and a user close
    // meanwhile is ignored; RequestCloseAsync returns once the close is decided.
    [Test]
    public async Task ADecidedCloseDrainsBeforeTheWindowCloses()
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drains = 0;
        var closings = 0;
        var close = new CsWebUiUserClose(null, () => throw new InvalidOperationException("The window closed without the drain."),
            _ => throw new InvalidOperationException("The request released the Window without the drain."), () => Interlocked.Increment(ref closings))
        {
            Drain = () =>
            {
                Interlocked.Increment(ref drains);
                return drained.Task;
            },
        };
        Require(!close.OnUserClose(), "An unguarded window with a drain closed at the user's close.");
        await WaitUntil(() => Volatile.Read(ref drains) == 1 && close.IsDraining);
        Require(!close.OnUserClose() && !close.OnUserClose(), "A user close during the drain let the window close.");
        Require(await close.RequestAsync(CancellationToken.None).AsTask().WaitAsync(Limit), "A request during the drain did not report the close.");
        Require(Volatile.Read(ref drains) == 1 && Volatile.Read(ref closings) == 1, $"Drained {drains} times, closing {closings} times.");
        drained.SetResult();
        await WaitUntil(() => !close.IsDraining);
        Require(close.OnUserClose(), "The handler kept a window open after its drain.");

        // A guarded request: the guard first, then the drain, and the request returns before the drain ends.
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guarded = new CsWebUiUserClose(_ => ValueTask.FromResult(true), () => { }, _ => ValueTask.CompletedTask, () => { })
        {
            Drain = () => pending.Task,
        };
        Require(await guarded.RequestAsync(CancellationToken.None).AsTask().WaitAsync(Limit) && guarded.IsDraining,
            "A request did not return once the close was decided, with the drain running.");
        pending.SetResult();
        await WaitUntil(() => !guarded.IsDraining);
    }

    [Test]
    public void TheDrainIsAvailableForTheWebViewAndServeOnly()
    {
        Require(CsWebUiUserClose.DrainUnavailableReason(serveOnly: true, webView: false, customShow: false, closeHandlerSupported: false) is null,
            "Serve-only mode did not drain.");
        Require(CsWebUiUserClose.DrainUnavailableReason(serveOnly: false, webView: true, customShow: false, closeHandlerSupported: true) is null,
            "A WebView window on Windows or Linux did not drain.");
        Require(CsWebUiUserClose.DrainUnavailableReason(serveOnly: false, webView: false, customShow: false, closeHandlerSupported: true) is { } browser
            && browser.Contains("browser window", StringComparison.Ordinal), "A browser window drained.");
        Require(CsWebUiUserClose.DrainUnavailableReason(serveOnly: false, webView: true, customShow: false, closeHandlerSupported: false) is { } mac
            && mac.Contains("macOS", StringComparison.Ordinal), "A macOS WebView drained.");
    }

    [Test]
    public void TheGuardIsAvailableOnlyForTheHostsOwnWebViewWhereWebUiAsks()
    {
        Require(CsWebUiUserClose.GuardUnavailableReason(serveOnly: false, webView: true, customShow: false, closeHandlerSupported: true) is null,
            "A WebView window on Windows or Linux was not guarded.");
        foreach (var (serveOnly, webView, customShow, supported, expected) in new[]
                 {
                     (true, true, false, true, "serve-only"),
                     (false, false, false, true, "browser window"),
                     (false, true, true, true, "ShowWindow"),
                     (false, true, false, false, "macOS"),
                 })
        {
            var reason = CsWebUiUserClose.GuardUnavailableReason(serveOnly, webView, customShow, supported);
            Require(reason is not null && reason.Contains("CS-WebUI", StringComparison.Ordinal) && reason.Contains(expected, StringComparison.Ordinal),
                $"Unavailable reason '{reason}' does not name CS-WebUI and {expected}.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Queue<TaskCompletionSource<bool>> _questions = new();
        private readonly SemaphoreSlim _asked = new(0);
        private int _asks, _windowCloses, _requests, _closings;

        internal Fixture()
        {
            Close = new CsWebUiUserClose(
                cancellationToken =>
                {
                    var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_questions)
                    {
                        _questions.Enqueue(answer);
                        LastToken = cancellationToken;
                    }
                    Interlocked.Increment(ref _asks);
                    _asked.Release();
                    return new ValueTask<bool>(answer.Task);
                },
                () => Interlocked.Increment(ref _windowCloses),
                _ =>
                {
                    Interlocked.Increment(ref _requests);
                    return ValueTask.CompletedTask;
                },
                () => Interlocked.Increment(ref _closings));
        }

        internal CsWebUiUserClose Close { get; }

        internal CancellationToken LastToken { get; private set; }

        internal int Asked => Volatile.Read(ref _asks);

        internal int WindowCloses => Volatile.Read(ref _windowCloses);

        internal int Requests => Volatile.Read(ref _requests);

        internal int Closings => Volatile.Read(ref _closings);

        public void Dispose() => _asked.Dispose();

        internal async Task<TaskCompletionSource<bool>> NextQuestion()
        {
            Require(await _asked.WaitAsync(Limit), "The close guard was not asked.");
            lock (_questions) return _questions.Dequeue();
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            Require(DateTime.UtcNow < deadline, "The close did not settle in time.");
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
