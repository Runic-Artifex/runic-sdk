using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Runic.Desktop.Internal;

// The close drain of a window (DesktopWindowOptions.CloseDrainAsync, sdk#900): its surface's current window.
internal interface IWindowCloseDrain
{
    // A drain started: the close was approved, and a later close request only joins it.
    bool IsDraining { get; }

    // Starts the drain, or joins the one running; completes when the drain's callback returned, never faulting.
    Task DrainAsync(DesktopCloseReason reason);
}

/// <summary>Serializes asynchronous close decisions without blocking a platform event loop.</summary>
/// <remarks>
/// With a close drain, an approved close keeps the window open while the drain runs and closes it afterwards; the
/// decision completes when the close was approved, and a request during the drain is approved without asking again.
/// </remarks>
internal sealed class WindowCloseController : IDisposable
{
    private static readonly Func<CancellationToken, ValueTask<bool>> Approve = static _ => ValueTask.FromResult(true);

    private readonly object _gate = new();
    private readonly Func<CancellationToken, ValueTask<bool>> _confirm;
    private readonly Func<ValueTask> _close;
    private readonly IWindowCloseDrain? _drain;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly ILogger? _logger;
    private Task<bool>? _pending;
    private bool _stopped;

    internal WindowCloseController(Func<CancellationToken, ValueTask<bool>>? confirm, Func<ValueTask> close, ILogger? logger = null,
        IWindowCloseDrain? drain = null)
    {
        _logger = logger;
        _confirm = confirm ?? Approve;
        _close = close;
        _drain = drain;
        _token = _lifetime.Token;
    }

    internal Task<bool> RequestAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(DesktopCloseReason.Request, cancellationToken);

    internal Task<bool> RequestAsync(DesktopCloseReason reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_stopped) return Task.FromResult(false);
            // The close was approved and the window drains: a second close, such as the user's, is consumed (D064.16).
            if (_drain is { IsDraining: true }) return Task.FromResult(true);
            if (_pending is null || _pending.IsCompleted)
            {
                // Even a callback that performs synchronous work must not block WM_CLOSE/delete-event/AppKit.
                _pending = Task.Run(() => DecideAsync(reason));
            }
            return _pending.WaitAsync(cancellationToken);
        }
    }

    internal void RequestFromPlatform() => _ = RequestAsync(DesktopCloseReason.User);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
        }
        _ = CancelAsync();
        GC.SuppressFinalize(this);
    }

    private async Task CancelAsync()
    {
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (_logger is null) Trace.TraceError($"A window close cancellation callback failed: {error}");
            else DesktopLog.CloseCancellationCallbackFailed(_logger, error, DesktopLog.ErrorType(error));
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private async Task<bool> DecideAsync(DesktopCloseReason reason)
    {
        try
        {
            _token.ThrowIfCancellationRequested();
            if (!await _confirm(_token).AsTask().WaitAsync(_token).ConfigureAwait(false)) return false;
            lock (_gate)
            {
                if (_stopped) return false;
            }
            if (_drain is { } drain)
            {
                // Approved: the window stays open while it drains and closes after it; the decision returns now, so a
                // request made from the work being drained does not wait for itself.
                _ = DrainThenCloseAsync(drain, reason);
                return true;
            }
            await _close().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception error)
        {
            // A failed prompt must never become implicit permission to discard application state.
            if (_logger is null) Trace.TraceError($"Window close confirmation failed; the window was kept open: {error}");
            else DesktopLog.CloseConfirmationFailed(_logger, error, DesktopLog.ErrorType(error));
            return false;
        }
    }

    private async Task DrainThenCloseAsync(IWindowCloseDrain drain, DesktopCloseReason reason)
    {
        await drain.DrainAsync(reason).ConfigureAwait(false);
        lock (_gate)
        {
            // The window closed meanwhile, such as by force or because the drain closed it.
            if (_stopped) return;
        }
        try
        {
            await _close().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (_logger is null) Trace.TraceError($"Window close after its drain failed: {error}");
            else DesktopLog.CloseConfirmationFailed(_logger, error, DesktopLog.ErrorType(error));
        }
    }
}
