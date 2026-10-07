namespace Runic.Application.Testing;

/// <summary>
/// A clock that moves only when a test calls <see cref="Advance"/> or <see cref="SetUtcNow"/>.
/// Timers created from it, including <see cref="CancellationTokenSource"/> timeouts and
/// <see cref="Task.Delay(TimeSpan, TimeProvider)"/>, fire synchronously in due order
/// while the clock moves.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;
    private long _timestamp;
    private long _sequence;

    /// <summary>Starts the clock at 2026-01-01T00:00:00Z.</summary>
    public ManualTimeProvider() : this(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    {
    }

    /// <summary>Starts the clock at <paramref name="start"/>.</summary>
    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        lock (_gate) return _timestamp;
    }

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <summary>Moves the clock forward, firing every timer that falls due on the way.</summary>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        DateTimeOffset target;
        lock (_gate) target = _now + delta;
        MoveTo(target);
    }

    /// <summary>Sets the clock to a later time, firing every timer that falls due on the way.</summary>
    public void SetUtcNow(DateTimeOffset value)
    {
        lock (_gate)
            if (value < _now) throw new ArgumentOutOfRangeException(nameof(value), "The manual clock cannot move backwards.");
        MoveTo(value);
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private void MoveTo(DateTimeOffset target)
    {
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(timer => timer.Due <= target)
                    .OrderBy(timer => timer.Due).ThenBy(timer => timer.Sequence).FirstOrDefault();
                if (next is null)
                {
                    Set(target);
                    return;
                }
                Set(next.Due);
                // A periodic timer is rescheduled before its callback, which may change it again.
                if (next.Period > TimeSpan.Zero) next.Schedule(next.Due + next.Period, ++_sequence);
                else _timers.Remove(next);
            }
            next.Fire();
        }
    }

    private void Set(DateTimeOffset value)
    {
        if (value <= _now) return;
        _timestamp += (value - _now).Ticks;
        _now = value;
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        internal DateTimeOffset Due { get; private set; }
        internal TimeSpan Period { get; private set; }
        internal long Sequence { get; private set; }

        internal void Schedule(DateTimeOffset due, long sequence)
        {
            Due = due;
            Sequence = sequence;
        }

        internal void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, TimeSpan.Zero);
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                Schedule(owner._now + dueTime, ++owner._sequence);
                owner._timers.Add(this);
                return true;
            }
        }

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
