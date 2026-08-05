using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RunicToolkit.Hosting.ContractTests.Fakes;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow;
    private long _timestamp;
    private bool _advancing;

    public ManualTimeProvider()
        : this(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero))
    {
    }

    public ManualTimeProvider(DateTimeOffset start)
    {
        if (start.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The start time must use the UTC offset.", nameof(start));
        }

        _utcNow = start;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_sync)
        {
            return _timestamp;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ValidateTimerInterval(dueTime, nameof(dueTime));
        ValidateTimerInterval(period, nameof(period));

        var timer = new ManualTimer(this, callback, state);
        lock (_sync)
        {
            _timers.Add(timer);
            timer.ChangeUnderLock(dueTime, period, _timestamp);
        }

        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Time cannot move backwards.");
        }

        long targetTimestamp;
        lock (_sync)
        {
            if (_advancing)
            {
                throw new InvalidOperationException("Manual time cannot be advanced concurrently or from a timer callback.");
            }

            _advancing = true;
            targetTimestamp = checked(_timestamp + amount.Ticks);
        }

        try
        {
            while (true)
            {
                ManualTimer? next;
                long nextTimestamp;

                lock (_sync)
                {
                    next = FindNextTimer(targetTimestamp);
                    if (next is null)
                    {
                        MoveClockUnderLock(targetTimestamp);
                        return;
                    }

                    nextTimestamp = next.DueTimestamp;
                    MoveClockUnderLock(nextTimestamp);
                    next.MarkFiredUnderLock(nextTimestamp);
                }

                next.Invoke();
            }
        }
        finally
        {
            lock (_sync)
            {
                _advancing = false;
            }
        }
    }

    private static void ValidateTimerInterval(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private ManualTimer? FindNextTimer(long targetTimestamp)
    {
        ManualTimer? next = null;
        foreach (var timer in _timers)
        {
            if (timer.IsDisposed || timer.DueTimestamp > targetTimestamp)
            {
                continue;
            }

            if (next is null || timer.DueTimestamp < next.DueTimestamp)
            {
                next = timer;
            }
        }

        return next;
    }

    private void MoveClockUnderLock(long timestamp)
    {
        var delta = timestamp - _timestamp;
        _timestamp = timestamp;
        _utcNow = _utcNow.AddTicks(delta);
    }

    private void Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        ValidateTimerInterval(dueTime, nameof(dueTime));
        ValidateTimerInterval(period, nameof(period));

        lock (_sync)
        {
            timer.ChangeUnderLock(dueTime, period, _timestamp);
        }
    }

    private void Dispose(ManualTimer timer)
    {
        lock (_sync)
        {
            timer.DisposeUnderLock();
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        public bool IsDisposed { get; private set; }

        public long DueTimestamp { get; private set; } = long.MaxValue;

        private TimeSpan Period { get; set; } = Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (IsDisposed)
            {
                return false;
            }

            owner.Change(this, dueTime, period);
            return true;
        }

        public void Dispose() => owner.Dispose(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void ChangeUnderLock(TimeSpan dueTime, TimeSpan period, long timestamp)
        {
            if (IsDisposed)
            {
                return;
            }

            Period = period;
            DueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                ? long.MaxValue
                : checked(timestamp + dueTime.Ticks);
        }

        public void MarkFiredUnderLock(long timestamp)
        {
            DueTimestamp = Period is { Ticks: 0 } || Period == Timeout.InfiniteTimeSpan
                ? long.MaxValue
                : checked(timestamp + Period.Ticks);
        }

        public void Invoke()
        {
            if (!IsDisposed)
            {
                callback(state);
            }
        }

        public void DisposeUnderLock()
        {
            IsDisposed = true;
            DueTimestamp = long.MaxValue;
        }
    }
}
