using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using ReactiveUI.Reactive;
using Runic.Navigation;

namespace Runic.Application.Views.ReactiveUI.Reactive;

/// <summary>Provides System.Reactive schedulers bound to Runic model contexts.</summary>
public interface IRunicReactiveSchedulerProvider
{
    /// <summary>Returns a scheduler that runs work on <paramref name="context"/>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "Published member name shared with Runic.Application.ReactiveUI.")]
    IScheduler For(IRunicModelContext context);
}

/// <summary>
/// Creates schedulers that deliver work through a model context, and makes ReactiveUI's main-thread scheduler
/// deliver work scheduled in a model turn to that turn's context.
/// </summary>
/// <remarks>
/// Every provider returns the same scheduler for one context for as long as the context is alive, and the
/// main-thread scheduler that <see cref="InstallMainThreadScheduler"/> installs uses that scheduler too.
/// </remarks>
public sealed class RunicReactiveSchedulerProvider : IRunicReactiveSchedulerProvider
{
    private static readonly ConditionalWeakTable<IRunicModelContext, RunicModelContextScheduler> Schedulers = new();
    private static readonly Lock InstallGate = new();

    /// <inheritdoc />
    public IScheduler For(IRunicModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Shared(context);
    }

    /// <summary>
    /// Sets <see cref="RxSchedulers.MainThreadScheduler"/>, the default output scheduler of every ReactiveUI
    /// command, to a scheduler that runs work scheduled in a turn of a <see cref="RunicModelContext"/> in that
    /// context, and other work on the scheduler it replaces. A command created without an output scheduler then
    /// delivers its results and <c>IsExecuting</c> on the model context of the turn that executes it, which for a
    /// bridged command is the window's context, so two windows never share one.
    /// <see cref="ReactiveServiceCollectionExtensions.AddRunicReactiveModelContext"/> calls it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scheduler is installed only while ReactiveUI's main-thread scheduler is a System.Reactive default
    /// (<see cref="DefaultScheduler"/>, <see cref="TaskPoolScheduler"/>, <see cref="ThreadPoolScheduler"/>,
    /// <see cref="CurrentThreadScheduler"/> or <see cref="ImmediateScheduler"/>). A scheduler that an application or
    /// UI platform set, such as a WPF dispatcher scheduler or a test scheduler, is kept; pass the context's scheduler
    /// explicitly there. To opt out, set <see cref="RxSchedulers.MainThreadScheduler"/> before this call, or replace
    /// it afterwards. Call this before ViewModels create their commands.
    /// </para>
    /// <para>
    /// The first time work is scheduled outside a model turn, for example from a timer thread or after an
    /// <c>await</c> with <c>ConfigureAwait(false)</c>, it runs on the replaced scheduler and event 1043
    /// (<c>ReactiveSchedulerOutsideModelTurn</c>) is logged once.
    /// </para>
    /// </remarks>
    /// <param name="loggerFactory">
    /// Creates the logger of event 1043, in the <see cref="RunicViewsTelemetry.LogCategory"/> category; without it,
    /// the warning goes to <see cref="System.Diagnostics.Trace"/>. A later call with a logger factory replaces it.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the model-context main-thread scheduler is installed, also by an earlier call;
    /// <see langword="false"/> when another scheduler was kept.
    /// </returns>
    public static bool InstallMainThreadScheduler(ILoggerFactory? loggerFactory = null)
    {
        lock (InstallGate)
        {
            var current = RxSchedulers.MainThreadScheduler;
            if (current is ModelContextMainThreadScheduler installed)
            {
                if (loggerFactory is not null) installed.Logger = loggerFactory.CreateLogger(RunicViewsTelemetry.LogCategory);
                return true;
            }
            if (current is not (DefaultScheduler or TaskPoolScheduler or ThreadPoolScheduler or CurrentThreadScheduler
                or ImmediateScheduler))
                return false;
            RxSchedulers.MainThreadScheduler = new ModelContextMainThreadScheduler(current)
            {
                Logger = loggerFactory?.CreateLogger(RunicViewsTelemetry.LogCategory) ?? TraceLogger.Instance,
            };
            return true;
        }
    }

    internal static IScheduler Shared(IRunicModelContext context) =>
        Schedulers.GetValue(context, static context => new RunicModelContextScheduler(context));
}

// ReactiveUI's main-thread scheduler in a Runic app. Each call resolves its target: the shared scheduler of
// the model context whose turn is running, or the scheduler this one replaced.
internal sealed class ModelContextMainThreadScheduler(IScheduler fallback) : IScheduler
{
    private int _reported;

    internal ILogger Logger { get; set; } = TraceLogger.Instance;

    public DateTimeOffset Now => (ModelTurnFlow.Current is { } context ? RunicReactiveSchedulerProvider.Shared(context) : fallback).Now;

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action) =>
        Target().Schedule(state, action);

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action) =>
        Target().Schedule(state, dueTime, action);

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action) =>
        Target().Schedule(state, dueTime, action);

    private IScheduler Target()
    {
        if (ModelTurnFlow.Current is { } context) return RunicReactiveSchedulerProvider.Shared(context);
        if (Interlocked.Exchange(ref _reported, 1) == 0) ReactiveLog.OutsideModelTurn(Logger, fallback.GetType().Name);
        return fallback;
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

    private ScheduledWork ScheduleCore<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var work = new ScheduledWork();
        // Schedule calls may arrive under distinct trusted interaction scopes.
        // Keep the caller's context with this individual work item; the model
        // queue is deliberately batched and cannot capture one drain context.
        var executionContext = ExecutionContext.Capture();
        if (dueTime <= TimeSpan.Zero)
        {
            if (!PostWithoutAmbientFlow(() => work.Run(this, state, action, executionContext))) work.Dispose();
            return work;
        }

        var timer = new Timer(
            static callback => ((TimerState<TState>)callback!).Run(),
            new TimerState<TState>(work, this, state, action, executionContext),
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        work.SetTimer(timer);
        timer.Change(dueTime, Timeout.InfiniteTimeSpan);
        return work;
    }

    private bool PostWithoutAmbientFlow(Action work)
    {
        if (ExecutionContext.IsFlowSuppressed()) return _context.TryPost(work);
        using (ExecutionContext.SuppressFlow()) return _context.TryPost(work);
    }

    private sealed class TimerState<TState>(
        ScheduledWork work,
        RunicModelContextScheduler scheduler,
        TState state,
        Func<IScheduler, TState, IDisposable> action,
        ExecutionContext? executionContext)
    {
        public void Run()
        {
            if (!scheduler.PostWithoutAmbientFlow(() => work.Run(scheduler, state, action, executionContext))) work.Dispose();
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

        public void Run<TState>(IScheduler scheduler, TState state, Func<IScheduler, TState, IDisposable> action,
            ExecutionContext? executionContext)
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
                if (_disposed) return;
            }

            IDisposable? inner = null;
            try
            {
                if (executionContext is null)
                    inner = action(scheduler, state) ?? Disposable.Empty;
                else
                    ExecutionContext.Run(executionContext, _ => inner = action(scheduler, state) ?? Disposable.Empty, null);
            }
            catch
            {
                Dispose();
                throw;
            }
            inner ??= Disposable.Empty;

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
