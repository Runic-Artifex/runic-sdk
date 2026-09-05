using System.Diagnostics;

namespace Runic.Desktop.Internal;

/// <summary>Serializes asynchronous close decisions without blocking a platform event loop.</summary>
internal sealed class WindowCloseController : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<CancellationToken, ValueTask<bool>> _confirm;
    private readonly Func<ValueTask> _close;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private Task<bool>? _pending;
    private bool _stopped;

    internal WindowCloseController(Func<CancellationToken, ValueTask<bool>> confirm, Func<ValueTask> close)
    {
        _confirm = confirm;
        _close = close;
        _token = _lifetime.Token;
    }

    internal Task<bool> RequestAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_stopped) return Task.FromResult(false);
            if (_pending is null || _pending.IsCompleted)
            {
                // Even a callback that performs synchronous work must not block WM_CLOSE/delete-event/AppKit.
                _pending = Task.Run(DecideAsync);
            }
            return _pending.WaitAsync(cancellationToken);
        }
    }

    internal void RequestFromPlatform() => _ = RequestAsync();

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
        catch (Exception)
        {
            Trace.TraceError("A window close cancellation callback failed.");
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private async Task<bool> DecideAsync()
    {
        try
        {
            _token.ThrowIfCancellationRequested();
            if (!await _confirm(_token).AsTask().WaitAsync(_token).ConfigureAwait(false)) return false;
            lock (_gate)
            {
                if (_stopped) return false;
            }
            await _close().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            // A failed prompt must never become implicit permission to discard application state.
            Trace.TraceError("Window close confirmation failed; the window was kept open.");
            return false;
        }
    }
}
