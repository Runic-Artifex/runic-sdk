using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using ReactiveUI.Reactive;
using Runic.Navigation;

using Runic.Application.Views.ReactiveUI.Reactive.Generated;

namespace Runic.Application.Views.ReactiveUI.Reactive;

/// <summary>
/// Provides System.Reactive schedulers bound to Runic model contexts, as a service: <see cref="ReactiveServiceCollectionExtensions.AddRunicReactiveModelContext"/>
/// registers it. Code without a container calls <see cref="ReactiveSchedulers.For(IRunicModelContext)"/>.
/// </summary>
public interface IReactiveSchedulerProvider
{
    /// <summary>Returns a scheduler that runs work on <paramref name="context"/>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "Published member name shared with Runic.Application.Views.ReactiveUI.")]
    IScheduler For(IRunicModelContext context);
}

/// <summary>
/// Creates schedulers that deliver work through a model context, and makes ReactiveUI's main-thread scheduler
/// deliver work scheduled in a model turn to that turn's context.
/// </summary>
/// <remarks>
/// Every call returns the same scheduler for one context for as long as the context is alive, and the
/// main-thread scheduler that <see cref="InstallMainThreadScheduler"/> installs uses that scheduler too.
/// </remarks>
public static class ReactiveSchedulers
{
    private static readonly ConditionalWeakTable<IRunicModelContext, RunicModelContextScheduler> Schedulers = new();
    private static readonly ConditionalWeakTable<object, ViewModelScheduler> ViewModelBound = new();
    private static readonly Lock InstallGate = new();
    // Whether an install was requested, whether Runic's scheduler was installed or given to ReactiveUI's builder,
    // and the scheduler it created last.
    private static bool _requested;
    private static bool _installed;
    private static ModelContextMainThreadScheduler? _created;

    /// <summary>Returns the scheduler that runs work on <paramref name="context"/>.</summary>
    /// <param name="context">The model context.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "The same name as IReactiveSchedulerProvider.For.")]
    public static IScheduler For(IRunicModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Shared(context);
    }

    /// <summary>
    /// Returns a scheduler that runs work on the model context that <paramref name="viewModel"/> is bound to when the work
    /// is scheduled, for a ViewModel that observes a source outside model turns, such as a service's events or a timer,
    /// without taking the context in its constructor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each call looks up <paramref name="viewModel"/> in <see cref="RunicModelContextRegistry.Shared"/>, where a window
    /// binds the ViewModels it presents and a navigator the entries it owns, and schedules on that context's
    /// scheduler, the one <see cref="For(IRunicModelContext)"/> returns, so work is ordered with the context's other work.
    /// While the ViewModel is not bound, for example in its constructor before the window binds it or after it was
    /// released, or when its context has closed, the work goes to <see cref="RxSchedulers.MainThreadScheduler"/>, read
    /// at that time: with <see cref="InstallMainThreadScheduler"/>, the context of the running turn, or the replaced
    /// scheduler with event 1043 (<c>ReactiveSchedulerOutsideModelTurn</c>). A source that replays when the constructor
    /// subscribes therefore needs <see cref="For(IRunicModelContext)"/> with the injected context instead.
    /// </para>
    /// <para>
    /// Every call returns the same scheduler for one ViewModel for as long as the ViewModel is alive; it does not keep the
    /// ViewModel alive by itself. When <paramref name="viewModel"/> is an <see cref="IRunicModelContext"/>, this is
    /// <see cref="For(IRunicModelContext)"/>.
    /// </para>
    /// </remarks>
    /// <param name="viewModel">The ViewModel, or any object a window or navigator binds to a model context.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "The same name as IReactiveSchedulerProvider.For.")]
    public static IScheduler For(object viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (viewModel is IRunicModelContext context) return Shared(context);
        return ViewModelBound.GetValue(viewModel, static model => new ViewModelScheduler(model));
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
    /// Work that a turn's async flow schedules after it left the turn, for example after an <c>await</c> with
    /// <c>ConfigureAwait(false)</c>, in <c>Task.Run</c> or from a timer created in the turn, runs in that turn's
    /// context too, as do the later notifications of a command that a turn executed with a plain <c>Execute</c>.
    /// Work whose flow never ran in a model turn, for example from a timer or service started outside any turn, and
    /// work whose context has closed, runs on the replaced scheduler, and event 1043
    /// (<c>ReactiveSchedulerOutsideModelTurn</c>) is logged once per ViewModel type that scheduled it, as found on
    /// the call stack, and once for work that no ViewModel on the stack scheduled.
    /// </para>
    /// <para>
    /// When a window opens, Runic Desktop, CS-WebUI and <c>RunicWindowTestHost.Create</c> pass the application's
    /// <see cref="ILoggerFactory"/> to the installed scheduler, as a call with <paramref name="loggerFactory"/> does.
    /// When another scheduler is in effect then, they log event 1090 (<c>ReactiveSchedulerKept</c>) or 1091
    /// (<c>ReactiveSchedulerReplaced</c>) naming it, unless it is a UI dispatcher's scheduler.
    /// </para>
    /// </remarks>
    /// <param name="loggerFactory">
    /// Creates the logger of event 1043, in the <see cref="RunicViewsTelemetry.LogCategory"/> category; without it,
    /// the warning goes to <see cref="System.Diagnostics.Trace"/> until a window opens. A later call with a logger
    /// factory replaces it.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the model-context main-thread scheduler is installed, also by an earlier call;
    /// <see langword="false"/> when another scheduler was kept.
    /// </returns>
    public static bool InstallMainThreadScheduler(ILoggerFactory? loggerFactory = null)
    {
        lock (InstallGate)
        {
            _requested = true;
            var current = RxSchedulers.MainThreadScheduler;
            if (current is ModelContextMainThreadScheduler installed)
            {
                if (loggerFactory is not null) installed.Logger = loggerFactory.CreateLogger(RunicViewsTelemetry.LogCategory);
                return true;
            }
            if (current is not (DefaultScheduler or TaskPoolScheduler or ThreadPoolScheduler or CurrentThreadScheduler
                or ImmediateScheduler))
                return false;
            // The scheduler WithRunicModelContext() gave ReactiveUI's builder, if it replaces the same default.
            var scheduler = _created is { } created && ReferenceEquals(created.Fallback, current)
                ? created
                : _created = new ModelContextMainThreadScheduler(current);
            if (loggerFactory is not null) scheduler.Logger = loggerFactory.CreateLogger(RunicViewsTelemetry.LogCategory);
            RxSchedulers.MainThreadScheduler = scheduler;
            _installed = true;
            RunicModelContext.FlowTurns = true;
            return true;
        }
    }

    // The scheduler that IReactiveUIBuilder.WithRunicModelContext() passes to WithMainThreadScheduler: the installed
    // one, or one that replaces ReactiveUI's default and that a later install also uses.
    internal static IScheduler BuilderScheduler()
    {
        lock (InstallGate)
        {
            _requested = true;
            _installed = true;
            RunicModelContext.FlowTurns = true;
            if (RxSchedulers.MainThreadScheduler is ModelContextMainThreadScheduler installed) return installed;
            return _created ??= new ModelContextMainThreadScheduler(DefaultScheduler.Instance);
        }
    }

    // Runic Desktop, CS-WebUI and the test host call this before a window resolves its ViewModel. The installed
    // scheduler logs event 1043 through the application's logger factory from then on; a scheduler that is not
    // Runic's is reported as kept (1090) or replaced (1091).
    internal static void OnWindowOpening(ILoggerFactory? loggerFactory)
    {
        bool replaced;
        IScheduler current;
        lock (InstallGate)
        {
            current = RxSchedulers.MainThreadScheduler;
            if (current is ModelContextMainThreadScheduler installed)
            {
                if (loggerFactory is not null) installed.Logger = loggerFactory.CreateLogger(RunicViewsTelemetry.LogCategory);
                return;
            }
            if (!_requested) return;
            replaced = _installed;
        }
        ReactiveSchedulerReports.NotInEffect(loggerFactory, current.GetType(), replaced);
    }

    // The open context a ViewModel is bound to, for For(object viewModel).
    internal static IRunicModelContext? BoundContext(object viewModel) =>
        RunicModelContextRegistry.Shared.TryGet(viewModel, out var context)
            && context is not null and not IRunicModelContextLifetime { Closed.IsCancellationRequested: true }
            ? context
            : null;

    internal static IScheduler Shared(IRunicModelContext context) =>
        Schedulers.GetValue(context, static context => new RunicModelContextScheduler(context));
}

/// <summary>
/// The <see cref="IReactiveSchedulerProvider"/> that <see cref="ReactiveServiceCollectionExtensions.AddRunicReactiveModelContext"/>
/// registers. It returns <see cref="ReactiveSchedulers.For(IRunicModelContext)"/>.
/// </summary>
public sealed class ReactiveSchedulerProvider : IReactiveSchedulerProvider
{
    internal static readonly ReactiveSchedulerProvider Instance = new();

    internal ReactiveSchedulerProvider()
    {
    }

    /// <inheritdoc />
    public IScheduler For(IRunicModelContext context) => ReactiveSchedulers.For(context);
}

// ReactiveUI's main-thread scheduler in a Runic app. Each call resolves its target: the shared scheduler of
// the model context whose turn is running or whose turn started the calling flow and is still open (ModelTurnFlow),
// or the scheduler this one replaced.
internal sealed class ModelContextMainThreadScheduler(IScheduler fallback) : IScheduler
{
    private readonly OutsideModelTurnReporter _outside = new();

    internal ILogger Logger { get; set; } = TraceLogger.Instance;

    internal IScheduler Fallback => fallback;

    public DateTimeOffset Now => (ModelTurnFlow.Current is { } context ? ReactiveSchedulers.Shared(context) : fallback).Now;

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action) =>
        Target(WorkOf(state)).Schedule(state, action);

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action) =>
        Target(WorkOf(state)).Schedule(state, dueTime, action);

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action) =>
        Target(WorkOf(state)).Schedule(state, dueTime, action);

    // An operator passes its sink as the state, which identifies one subscription. A value-type state with a static
    // action identifies nothing, so such work is inspected each time, within the reporter's bound.
    private static object? WorkOf<TState>(TState state) => typeof(TState).IsValueType ? null : state;

    private IScheduler Target(object? work)
    {
        if (ModelTurnFlow.Current is { } context) return ReactiveSchedulers.Shared(context);
        _outside.Report(Logger, work, fallback.GetType().Name);
        return fallback;
    }
}

// ReactiveSchedulers.For(object viewModel): each call targets the shared scheduler of the open context the ViewModel is
// bound to, or ReactiveUI's main-thread scheduler while it has none.
internal sealed class ViewModelScheduler(object viewModel) : IScheduler
{
    public DateTimeOffset Now => Target().Now;

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action) =>
        Target().Schedule(state, action);

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action) =>
        Target().Schedule(state, dueTime, action);

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action) =>
        Target().Schedule(state, dueTime, action);

    private IScheduler Target()
    {
        if (ReactiveSchedulers.BoundContext(viewModel) is { } context) return ReactiveSchedulers.Shared(context);
        var main = RxSchedulers.MainThreadScheduler;
        // An application that made this scheduler ReactiveUI's main-thread scheduler gets the task pool instead.
        return ReferenceEquals(main, this) ? TaskPoolScheduler.Default : main;
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
                // The item runs in its own turn's flow (RunicModelContext.EnterTurnFlow): work it starts belongs to
                // this context, also when it was scheduled from another context's flow. The scheduler posts each item
                // as its own turn with suppressed flow, which enters nothing, so the common case, an item scheduled
                // from this context's own flow, writes nothing.
                if (executionContext is null)
                    inner = action(scheduler, state) ?? Disposable.Empty;
                else
                    ExecutionContext.Run(executionContext, _ =>
                    {
                        RunicModelContext.EnterTurnFlow();
                        inner = action(scheduler, state) ?? Disposable.Empty;
                    }, null);
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
