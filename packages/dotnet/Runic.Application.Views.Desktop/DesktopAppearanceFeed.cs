using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

// appearance/1: one watch of the desktop's settings (IDesktopSettings) for the open windows that share the feed, while
// there are any. Each window's WindowContentSession.Appearance follows it, and the dispatcher pushes it to the window's
// pages.
// - The source is the application's IDesktopSettings, which the feed uses and never disposes, or, without one, the
//   platform's own, which the feed creates for its first window and disposes once its last window closed.
// - A read that does not succeed (Unavailable after a portal restart or a COM failure, Failed) keeps the last appearance
//   read; the appearance is unknown (null) only until a first read succeeds.
// - A watch that throws is logged (2009) through the logger of an open window; the pages keep the last appearance, and
//   the next window that opens watches again.
internal sealed class DesktopAppearanceFeed(IDesktopSettings? settings, Func<IDesktopSettings>? create = null)
{
    private readonly Func<IDesktopSettings> _create = create ?? DesktopPlatformServices.CreateSettings;
    private readonly object _gate = new();
    // Each open window's session and the logger of its services.
    private readonly Dictionary<WindowContentSession, ILogger> _sessions = [];
    private Watch? _watch;
    private DesktopAppearance? _current;

    // Adds a window's session until the returned registration is disposed. The session gets the last appearance read at
    // once, and every later one.
    internal IDisposable Add(WindowContentSession session, ILogger? logger = null)
    {
        lock (_gate)
        {
            _sessions[session] = logger ?? NullLogger.Instance;
            if (_current is not null) session.Appearance = _current;
            _watch ??= Start();
        }
        return new Registration(this, session);
    }

    // Caller holds the gate. Null when the platform's source could not be created; the next window tries again.
    private Watch? Start()
    {
        IDesktopSettings source;
        try { source = settings ?? _create(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            DesktopLog.AppearanceWatchFailed(Logger(), error, error.GetType().Name);
            return null;
        }
        var watch = new Watch(source, owned: settings is null);
        watch.Running = Task.Run(() => WatchAsync(watch));
        return watch;
    }

    // Caller holds the gate.
    private ILogger Logger() => _sessions.Count > 0 ? _sessions.Values.Last() : NullLogger.Instance;

    private void Remove(WindowContentSession session)
    {
        Watch? stopped;
        lock (_gate)
        {
            if (!_sessions.Remove(session) || _sessions.Count > 0) return;
            // The last window closed: stop watching until another opens, which then starts from a fresh read.
            (stopped, _watch, _current) = (_watch, null, null);
        }
        if (stopped is not null) _ = stopped.StopAsync();
    }

    private async Task WatchAsync(Watch watch)
    {
        var token = watch.Cancellation.Token;
        try
        {
            await foreach (var result in watch.Source.WatchAsync(token).ConfigureAwait(false))
            {
                WindowContentSession[] targets;
                DesktopAppearance appearance;
                lock (_gate)
                {
                    // A watch the last closing window stopped applies nothing more.
                    if (!ReferenceEquals(_watch, watch)) return;
                    // A read that failed keeps the last appearance.
                    if (result is not PlatformResult<DesktopAppearance>.Success success) continue;
                    _current = appearance = success.Value;
                    targets = [.. _sessions.Keys];
                }
                foreach (var session in targets)
                {
                    try { session.Appearance = appearance; }
                    catch (ObjectDisposedException) { }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            // The pages keep the last appearance; their engines' media queries still follow the desktop.
            ILogger logger;
            lock (_gate) logger = Logger();
            DesktopLog.AppearanceWatchFailed(logger, error, error.GetType().Name);
        }
        // The watch failed or ended: the next window that opens starts another.
        bool current;
        lock (_gate)
        {
            current = ReferenceEquals(_watch, watch);
            if (current) _watch = null;
        }
        // From the watch itself, which must not wait for its own end.
        if (current) await watch.StopAsync(waitForWatch: false).ConfigureAwait(false);
    }

    private sealed class Watch(IDesktopSettings source, bool owned)
    {
        internal IDesktopSettings Source => source;
        private int _stopping;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Task Running { get; set; } = Task.CompletedTask;

        // Cancels the watch, waits for it, and disposes a source the feed created.
        internal async Task StopAsync(bool waitForWatch = true)
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
            try
            {
                await Cancellation.CancelAsync().ConfigureAwait(false);
                if (waitForWatch) await Running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (owned) await source.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception) { /* The source is gone either way; the next window creates another. */ }
            finally { Cancellation.Dispose(); }
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
