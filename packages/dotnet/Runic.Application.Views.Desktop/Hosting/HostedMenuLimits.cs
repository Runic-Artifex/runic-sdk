using System.Diagnostics;

namespace Runic.Application.Views.Desktop.Hosting;

// A rate of a guest's requests (host-protocol.md, Limits), such as a window's menu requests or the guest's dialogs: a
// token bucket that holds count requests and refills them over the period, so a guest may make a burst of count
// requests and then count per period.
internal sealed class RequestRate(int count, TimeSpan period)
{
    private readonly object _gate = new();
    private readonly double _perSecond = count / period.TotalSeconds;
    private double _tokens = count;
    private long _last = Stopwatch.GetTimestamp();

    // Whether one more request is within the rate; it is counted when it is.
    internal bool TryTake()
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            _tokens = Math.Min(count, _tokens + Stopwatch.GetElapsedTime(_last, now).TotalSeconds * _perSecond);
            _last = now;
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }
}

// A window's native menu bar rebuilds, coalesced (host-protocol.md, Limits): at most one rebuild starts per interval,
// and each request waits for the first rebuild that starts after it asked, which shows its change. Requests that arrive
// while one rebuild waits or runs share the next one, so a stream of updates costs one rebuild per interval.
internal sealed class CoalescedRebuild<T>(TimeSpan interval, Func<Task<T>> rebuild)
{
    private readonly object _gate = new();
    // The rebuild the next requests wait for; null when none is due.
    private TaskCompletionSource<T>? _next;
    private bool _running;
    private long _lastStart;

    internal Task<T> RequestAsync()
    {
        lock (_gate)
        {
            if (_next is { } due) return due.Task;
            _next = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = _next.Task;
            if (!_running)
            {
                _running = true;
                _ = Task.Run(RunAsync);
            }
            return task;
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            long lastStart;
            lock (_gate) lastStart = _lastStart;
            var wait = lastStart == 0 ? TimeSpan.Zero : interval - Stopwatch.GetElapsedTime(lastStart);
            if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
            TaskCompletionSource<T> current;
            lock (_gate)
            {
                current = _next!;
                _next = null;
                _lastStart = Stopwatch.GetTimestamp();
            }
            try { current.TrySetResult(await rebuild().ConfigureAwait(false)); }
            catch (Exception error) { current.TrySetException(error); }
            lock (_gate)
            {
                if (_next is null)
                {
                    _running = false;
                    return;
                }
            }
        }
    }
}
