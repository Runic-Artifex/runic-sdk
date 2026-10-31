using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Runic.Desktop.Internal;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// The connection deadline's rule (I-176, I-180) on the test's clock: a timeout from the wait's start, a whole timeout
// from the first request, and before any request an extension while an embedded WebView makes progress, up to four
// timeouts.
public sealed class ConnectionDeadlineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    [Test]
    public void WithoutProgressTheWaitEndsAfterTheTimeout()
    {
        var time = new ManualTime();
        var deadline = new ConnectionDeadline(Timeout, time);

        Assert.Equal(Timeout, deadline.Remaining(deadline.Now, out var extended));
        Assert.Null(extended);
        time.Advance(Timeout);
        Assert.Null(deadline.Remaining(TimeSpan.Zero, out _));
        Assert.Equal(new ConnectionDeadline.Expiry(Timeout, Progress: null, AtCap: false), deadline.Expired);
    }

    [Test]
    public void ANavigationInProgressHoldsTheDeadlineUntilTheLimit()
    {
        var time = new ManualTime();
        var deadline = new ConnectionDeadline(Timeout, time);
        deadline.Report(WebViewStartupSignal.NavigationStarted);

        // The wait still wakes at its timeout, to report the extension once.
        Assert.Equal(Timeout, deadline.Remaining(TimeSpan.Zero, out var extended));
        Assert.Null(extended);
        time.Advance(Timeout);
        Assert.Equal(3 * Timeout, deadline.Remaining(TimeSpan.Zero, out extended));
        Assert.Equal(new ConnectionDeadline.Extension(Timeout, "its first navigation started 1 seconds earlier and had not ended"), extended);
        time.Advance(2.5 * Timeout);
        Assert.Equal(0.5 * Timeout, deadline.Remaining(TimeSpan.Zero, out extended));
        Assert.Null(extended);

        time.Advance(0.5 * Timeout);
        Assert.Null(deadline.Remaining(TimeSpan.Zero, out _));
        Assert.Equal(
            new ConnectionDeadline.Expiry(4 * Timeout, "its first navigation started 4 seconds earlier and had not ended", AtCap: true),
            deadline.Expired);
    }

    [Test]
    public void EachStartupStepAllowsAWholeTimeoutAfterIt()
    {
        var time = new ManualTime();
        var deadline = new ConnectionDeadline(Timeout, time);
        time.Advance(0.8 * Timeout);
        deadline.Report(WebViewStartupSignal.Step);
        time.Advance(Timeout);
        deadline.Report(WebViewStartupSignal.Step);

        Assert.Equal(Timeout, deadline.Remaining(TimeSpan.Zero, out var extended));
        Assert.NotNull(extended);
        time.Advance(Timeout);
        Assert.Null(deadline.Remaining(TimeSpan.Zero, out _));
        Assert.Equal(
            new ConnectionDeadline.Expiry(2.8 * Timeout, "its last startup step was 1 seconds earlier", AtCap: false),
            deadline.Expired);
    }

    [Test]
    public void StepsDoNotExtendTheWaitPastTheLimit()
    {
        var time = new ManualTime();
        var deadline = new ConnectionDeadline(Timeout, time);
        time.Advance(3.5 * Timeout);
        deadline.Report(WebViewStartupSignal.Step);

        Assert.Equal(0.5 * Timeout, deadline.Remaining(TimeSpan.Zero, out _));
        time.Advance(0.5 * Timeout);
        Assert.Null(deadline.Remaining(TimeSpan.Zero, out _));
        Assert.True(deadline.Expired!.AtCap);
    }

    [Test]
    public void AFailedNavigationEndsTheHold()
    {
        var time = new ManualTime();
        var deadline = new ConnectionDeadline(Timeout, time);
        deadline.Report(WebViewStartupSignal.NavigationStarted);
        var changed = deadline.Changed;
        time.Advance(2 * Timeout);
        Assert.NotNull(deadline.Remaining(TimeSpan.Zero, out _));

        // The failure wakes a waiting wait, which then ends: the navigation started more than a timeout ago.
        deadline.Report(WebViewStartupSignal.Failed);
        Assert.True(changed.IsCompleted);
        Assert.Null(deadline.Remaining(TimeSpan.Zero, out _));
        Assert.Equal(new ConnectionDeadline.Expiry(2 * Timeout, Progress: null, AtCap: false), deadline.Expired);
    }

    [Test]
    public void TheFirstRequestGetsAWholeTimeoutEvenPastTheLimit()
    {
        var time = new ManualTime();
        var deadline = new ConnectionDeadline(Timeout, time);
        deadline.Report(WebViewStartupSignal.NavigationStarted);
        time.Advance(3.5 * Timeout);
        deadline.ReportRequest();
        Assert.True(deadline.HasRequest);

        // From the request on, the plain rule: progress no longer extends the wait.
        Assert.Equal(Timeout, deadline.Remaining(TimeSpan.Zero, out var extended));
        Assert.Null(extended);
        time.Advance(Timeout);
        Assert.Null(deadline.Remaining(TimeSpan.Zero, out _));
        Assert.Equal(new ConnectionDeadline.Expiry(4.5 * Timeout, Progress: null, AtCap: false), deadline.Expired);
    }
}

// A clock that moves only when the test advances it. Each timer created on it is announced, so a test can advance the
// clock once a wait sleeps.
internal sealed class ManualTime : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<Timer> _timers = [];
    private readonly Channel<ITimer> _created = Channel.CreateUnbounded<ITimer>();
    private TaskCompletionSource _armed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TimeSpan _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate) return _now.Ticks;
    }

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        _created.Writer.TryWrite(timer);
        return timer;
    }

    // Waits until a timer is created; each creation is taken once.
    public async Task WaitForTimerAsync(CancellationToken cancellationToken) => await _created.Reader.ReadAsync(cancellationToken);

    // Waits until a timer is armed to fire dueIn from now, such as a Task.Delay(dueIn, this) that started since the clock
    // last moved. It takes nothing, so other waits still see the timer.
    public async Task WaitForTimerAsync(TimeSpan dueIn, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task armed;
            lock (_gate)
            {
                if (_timers.Any(timer => timer.Due - _now == dueIn)) return;
                armed = _armed.Task;
            }
            await armed.WaitAsync(cancellationToken);
        }
    }

    // Moves the clock and runs the timers then due, outside the clock's lock.
    public void Advance(TimeSpan by)
    {
        List<Timer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(timer => timer.Due <= _now).ToList();
            foreach (var timer in due)
            {
                _timers.Remove(timer);
            }
        }
        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    // One-shot timers suffice for Task.WaitAsync and Task.Delay.
    private sealed class Timer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        internal TimeSpan Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            TaskCompletionSource armed;
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                if (dueTime == global::System.Threading.Timeout.InfiniteTimeSpan) return true;
                Due = owner._now + dueTime;
                owner._timers.Add(this);
                armed = owner._armed;
                owner._armed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            armed.TrySetResult();
            return true;
        }

        internal void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate) owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class CapturingLogger : ILogger
{
    private readonly List<(LogLevel Level, EventId EventId, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, EventId EventId, string Message)> Entries
    {
        get
        {
            lock (_entries) return _entries.ToArray();
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries) _entries.Add((logLevel, eventId, formatter(state, exception)));
    }
}
