using Microsoft.Extensions.Logging;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

// appearance/1: one watch of the desktop's settings (IDesktopSettings) for a host's open windows, while it has any. Each
// window's WindowContentSession.Appearance follows it, and the dispatcher pushes it to the window's pages. A read that
// fails or a desktop without the settings makes the appearance unknown (null).
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001",
    Justification = "The watch is cancelled and disposed when the host's last window closes; the feed lives as long as its host.")]
internal sealed class DesktopAppearanceFeed(Func<IDesktopSettings> settings, ILogger logger)
{
    private readonly object _gate = new();
    private readonly HashSet<WindowContentSession> _sessions = [];
    private IDesktopSettings? _settings;
    private CancellationTokenSource? _watch;
    private DesktopAppearance? _current;

    // Adds a window's session until the returned registration is disposed. The session gets the last appearance read at
    // once, and every later one.
    internal IDisposable Add(WindowContentSession session)
    {
        lock (_gate)
        {
            _sessions.Add(session);
            if (_current is not null) session.Appearance = _current;
            if (_watch is null)
            {
                _watch = new CancellationTokenSource();
                _settings ??= settings();
                _ = WatchAsync(_settings, _watch);
            }
        }
        return new Registration(this, session);
    }

    private void Remove(WindowContentSession session)
    {
        lock (_gate)
        {
            if (!_sessions.Remove(session) || _sessions.Count > 0 || _watch is null) return;
            // The last window closed: stop watching until another opens, which then starts from a fresh read.
            _watch.Cancel();
            _watch.Dispose();
            _watch = null;
            _current = null;
        }
    }

    private async Task WatchAsync(IDesktopSettings source, CancellationTokenSource watch)
    {
        CancellationToken token;
        try { token = watch.Token; }
        catch (ObjectDisposedException) { return; }
        try
        {
            await foreach (var result in source.WatchAsync(token).ConfigureAwait(false))
            {
                var appearance = result is PlatformResult<DesktopAppearance>.Success success ? success.Value : null;
                WindowContentSession[] targets;
                lock (_gate)
                {
                    // A watch the last closing window stopped applies nothing more.
                    if (!ReferenceEquals(_watch, watch)) return;
                    _current = appearance;
                    targets = [.. _sessions];
                }
                foreach (var session in targets)
                {
                    try { session.Appearance = appearance; }
                    catch (ObjectDisposedException) { }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            // The pages keep the last appearance; their engines' media queries still follow the desktop.
            DesktopLog.AppearanceWatchFailed(logger, error, error.GetType().Name);
        }
    }

    private sealed class Registration(DesktopAppearanceFeed feed, WindowContentSession session) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) feed.Remove(session);
        }
    }
}
