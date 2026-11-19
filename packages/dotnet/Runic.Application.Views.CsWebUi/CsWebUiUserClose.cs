using CsWebUi;

namespace Runic.Application.Views.CsWebUi;

// sdk#1148: the user's close of a CS-WebUI WebView window, where WebUI asks before the window closes
// (WebUiWindow.SetCloseHandler): WebView2 on Windows and WebKitGTK on Linux. The handler keeps the window open, the
// Window's close guard (RunicWindowOpening.CloseGuard) decides in a model turn, and an approved close goes through
// WebUiWindow.Close, which CS-WebUI never sends through the handler again. RunicWindow.RequestCloseAsync joins the same
// decision (RunicWindowOpening.UseCloseRequest), so the user and the application never ask twice at once.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The source only cancels and has no timer; a guard may still hold its token after the window closed.")]
internal sealed class CsWebUiUserClose
{
    private readonly object _gate = new();
    private readonly Func<CancellationToken, ValueTask<bool>>? _guard;
    private readonly Action _closeWindow;
    private readonly Func<CancellationToken, ValueTask> _closeRequested;
    private readonly Action _closing;
    // Cancelled when the window closes without asking: the application's CloseAsync or disposal, or WebUI's wait ending.
    private readonly CancellationTokenSource _abandoned = new();
    private Task<bool>? _decision;
    private bool _closed;

    // guard: the Window's close guard, or null for none. closeWindow: the approved user close (WebUiWindow.Close).
    // closeRequested: the approved close of RunicWindow.RequestCloseAsync, which releases the Window as before.
    // closing: the close was decided, so the Window leaves IRunicWindowService.Windows (RunicWindow.MarkClosing).
    internal CsWebUiUserClose(Func<CancellationToken, ValueTask<bool>>? guard, Action closeWindow,
        Func<CancellationToken, ValueTask> closeRequested, Action closing)
    {
        _guard = guard;
        _closeWindow = closeWindow;
        _closeRequested = closeRequested;
        _closing = closing;
    }

    // Why a Window's close guard cannot keep its CS-WebUI window open, for event 1053, or null when it can: only a
    // WebView window that the host shows itself, on a system where WebUI asks before it closes.
    internal static string? GuardUnavailableReason(bool serveOnly, bool webView, bool customShow, bool closeHandlerSupported) =>
        serveOnly ? "CS-WebUI's serve-only mode"
        : customShow ? "CS-WebUI with RunicCsWebUiHostOptions.ShowWindow"
        : !webView ? "CS-WebUI's browser window"
        : !closeHandlerSupported ? "CS-WebUI's WebView on macOS"
        : null;

    // Whether a decision runs now, for tests.
    internal bool IsDeciding
    {
        get
        {
            lock (_gate) return _decision is { IsCompleted: false };
        }
    }

    // WebUI's close handler, on the window's user-interface thread: true lets the window close at once. With a guard
    // it returns false and decides in the background; a close while a decision runs is ignored, as on Runic Desktop.
    internal bool OnUserClose()
    {
        lock (_gate)
        {
            if (_closed || _abandoned.IsCancellationRequested) return true;
            if (_guard is null)
            {
                _closed = true;
            }
            else
            {
                if (_decision is not { IsCompleted: false }) _decision = DecideThenCloseAsync(userClose: true);
                return false;
            }
        }
        _closing();
        return true;
    }

    // RunicWindow.RequestCloseAsync: joins a decision that runs, or starts one; true once the window closes.
    internal async ValueTask<bool> RequestAsync(CancellationToken cancellationToken)
    {
        Task<bool> decision;
        lock (_gate)
        {
            if (_closed || _abandoned.IsCancellationRequested) return true;
            decision = _decision is { IsCompleted: false } running ? running : _decision = DecideThenCloseAsync(userClose: false);
        }
        return await decision.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // The window closes without asking: a running decision is cancelled, and its answer no longer closes anything.
    internal void Abandon()
    {
        try { _abandoned.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task<bool> DecideThenCloseAsync(bool userClose)
    {
        // Leaves the handler's thread (WebUI's user-interface thread) before the guard runs.
        await Task.Yield();
        var token = _abandoned.Token;
        bool allowed;
        // The guard never throws (RunicWindowOpening.CloseGuard); cancellation reads as a refusal.
        try { allowed = _guard is null || await _guard(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { allowed = false; }
        lock (_gate)
        {
            if (_closed || token.IsCancellationRequested) return true;
            if (!allowed) return false;
            _closed = true;
        }
        _closing();
        if (userClose) _closeWindow();
        else await _closeRequested(CancellationToken.None).ConfigureAwait(false);
        return true;
    }
}
