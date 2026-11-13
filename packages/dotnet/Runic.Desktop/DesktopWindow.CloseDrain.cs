namespace Runic.Desktop;

// sdk#900 W4b: DesktopWindowOptions.CloseDrainAsync of an open window. The drain runs once; a user close, a request, a
// quit's guard and the host's shutdown start it or join it.
public sealed partial class DesktopWindow
{
    private readonly Lock _drainGate = new();
    // Not disposed: a late forced close or shutdown limit may still cancel it.
    private readonly CancellationTokenSource _drainEnd = new();
    private Func<DesktopCloseDrainContext, CancellationToken, ValueTask>? _closeDrain;
    private Task? _drain;

    // DesktopWindowOptions.CloseDrainAsync, set before the window is published. visible: the window can refuse a close, so
    // the drain runs while it stays open; otherwise it runs once the window is gone.
    internal void UseCloseDrain(Func<DesktopCloseDrainContext, CancellationToken, ValueTask>? drain, bool visible)
    {
        _closeDrain = drain;
        DrainsVisibly = drain is not null && visible;
    }

    internal bool HasCloseDrain => _closeDrain is not null;

    // The window stays open while it drains: its close goes through the close controller, which runs the drain first.
    internal bool DrainsVisibly { get; private set; }

    // A drain started: the close was approved, and a later close only joins it (D064.16).
    internal bool IsDraining => Volatile.Read(ref _drain) is not null;

    // Whether the visible phase was ended early: a forced close, the shutdown limit or the host's disposal.
    internal bool IsDrainEnded => _drainEnd.IsCancellationRequested;

    // Runs the window's drain once, with the reason of the first call; the task completes when the callback has returned,
    // never faulting. Without a drain it is completed.
    internal Task DrainAsync(DesktopCloseReason reason)
    {
        if (_closeDrain is not { } drain) return Task.CompletedTask;
        lock (_drainGate)
        {
            if (_drain is { } running) return running;
            var started = Task.Run(() => RunDrainAsync(drain, reason));
            Volatile.Write(ref _drain, started);
            return started;
        }
    }

    private async Task RunDrainAsync(Func<DesktopCloseDrainContext, CancellationToken, ValueTask> drain, DesktopCloseReason reason)
    {
        try
        {
            await drain(new DesktopCloseDrainContext(reason), _drainEnd.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_drainEnd.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (_engine.Logger is { } logger) Internal.DesktopLog.CloseDrainFailed(logger, error, Internal.DesktopLog.ErrorType(error));
            else System.Diagnostics.Trace.TraceError($"A window's close drain failed with {error.GetType().FullName}; the window closes.");
        }
    }

    // Ends the visible phase: the drain's token is cancelled and the window closes whether or not it returned. Runic
    // cancels nothing else; what the callback does with the token is the application's.
    internal void EndDrain()
    {
        if (_drainEnd.IsCancellationRequested) return;
        try { _drainEnd.Cancel(); }
        catch (AggregateException error)
        {
            if (_engine.Logger is { } logger) Internal.DesktopLog.CloseDrainFailed(logger, error, Internal.DesktopLog.ErrorType(error));
            else System.Diagnostics.Trace.TraceError($"A cancellation callback of a window's close drain failed with {error.GetType().FullName}; the window closes.");
        }
    }

    // The host's shutdown (DesktopHost.ShutdownAsync): drains this window, or joins a drain that a close started, until it
    // returned or the shutdown's limit passed, then closes the window. A window that cannot stay open closes first and
    // drains after.
    internal async Task DrainForShutdownAsync(CancellationToken limit)
    {
        using var end = limit.Register(static window => ((DesktopWindow)window!).EndDrain(), this);
        if (!DrainsVisibly && IsOpen) await CloseQuietlyAsync().ConfigureAwait(false);
        try { await DrainAsync(DesktopCloseReason.Shutdown).WaitAsync(limit).ConfigureAwait(false); }
        catch (OperationCanceledException) when (limit.IsCancellationRequested) { }
        await CloseQuietlyAsync().ConfigureAwait(false);
    }

    private async Task CloseQuietlyAsync()
    {
        try { await CloseAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException or DesktopException) { }
    }
}
