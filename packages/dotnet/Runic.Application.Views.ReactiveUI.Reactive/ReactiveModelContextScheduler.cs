using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using Runic.Application.Views;

namespace Runic.Application.Views.ReactiveUI.Reactive;

/// <summary>Provides System.Reactive schedulers bound to Runic model contexts.</summary>
public interface IRunicReactiveSchedulerProvider
{
    IScheduler For(IRunicModelContext context);
}

/// <summary>
/// Creates schedulers that deliver work through a model context. The provider
/// does not change ReactiveUI's process-global scheduler configuration.
/// </summary>
public sealed class RunicReactiveSchedulerProvider : IRunicReactiveSchedulerProvider
{
    public IScheduler For(IRunicModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new RunicModelContextScheduler(context);
    }
}

internal sealed class RunicModelContextScheduler(IRunicModelContext context) : IScheduler
{
    private readonly IRunicModelContext _context = context;

    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action) =>
        ScheduleCore(state, TimeSpan.Zero, action);

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action) =>
        ScheduleCore(state, dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime, action);

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action) =>
        ScheduleCore(state, dueTime - Now, action);

    private IDisposable ScheduleCore<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var work = new ScheduledWork();
        if (dueTime <= TimeSpan.Zero)
        {
            if (!_context.TryPost(() => work.Run(this, state, action))) work.Dispose();
            return work;
        }

        var timer = new Timer(
            static callback => ((TimerState<TState>)callback!).Run(),
            new TimerState<TState>(_context, work, this, state, action),
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        work.SetTimer(timer);
        timer.Change(dueTime, Timeout.InfiniteTimeSpan);
        return work;
    }

    private sealed class TimerState<TState>(
        IRunicModelContext context,
        ScheduledWork work,
        RunicModelContextScheduler scheduler,
        TState state,
        Func<IScheduler, TState, IDisposable> action)
    {
        public void Run()
        {
            if (!context.TryPost(() => work.Run(scheduler, state, action))) work.Dispose();
        }
    }

    private sealed class ScheduledWork : IDisposable
    {
        private readonly object _gate = new();
        private IDisposable? _inner;
        private Timer? _timer;
        private bool _disposed;

        public void SetTimer(Timer timer)
        {
            lock (_gate)
            {
                if (_disposed) timer.Dispose();
                else _timer = timer;
            }
        }

        public void Run<TState>(IScheduler scheduler, TState state, Func<IScheduler, TState, IDisposable> action)
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
                if (_disposed) return;
            }

            IDisposable inner;
            try { inner = action(scheduler, state) ?? Disposable.Empty; }
            catch
            {
                Dispose();
                throw;
            }

            lock (_gate)
            {
                if (_disposed) inner.Dispose();
                else _inner = inner;
            }
        }

        public void Dispose()
        {
            IDisposable? inner;
            Timer? timer;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                inner = _inner;
                _inner = null;
                timer = _timer;
                _timer = null;
            }
            timer?.Dispose();
            inner?.Dispose();
        }
    }
}
