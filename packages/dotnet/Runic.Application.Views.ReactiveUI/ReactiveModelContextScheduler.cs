using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives.Advanced;
using ReactiveUI.Primitives.Concurrency;
using Runic.Navigation;

namespace Runic.Application.Views.ReactiveUI;

/// <summary>
/// Provides ReactiveUI schedulers bound to Runic model contexts, as a service: <see cref="ReactiveServiceCollectionExtensions.AddRunicReactiveModelContext"/>
/// registers it. Code without a container calls <see cref="ReactiveSchedulers.For"/>.
/// </summary>
public interface IReactiveSchedulerProvider
{
    /// <summary>Returns a sequencer that runs work on <paramref name="context"/>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "Published member name shared with Runic.Application.Views.ReactiveUI.Reactive.")]
    ISequencer For(IRunicModelContext context);
}

/// <summary>
/// Creates an <see cref="ISequencer"/> that delivers scheduled work through a model context, and makes
/// ReactiveUI's main-thread scheduler deliver work scheduled in a model turn to that turn's context.
/// </summary>
/// <remarks>
/// A sequencer keeps its ordering state, so every call returns the same sequencer for one context for as
/// long as the context is alive, and the main-thread scheduler that
/// <see cref="InstallMainThreadScheduler"/> installs uses that sequencer too.
/// </remarks>
public static class ReactiveSchedulers
{
    private static readonly ConditionalWeakTable<IRunicModelContext, RunicModelContextSequencer> Sequencers = new();
    private static readonly Lock InstallGate = new();
    // Whether an install was requested, whether Runic's scheduler was installed or given to ReactiveUI's builder,
    // and the scheduler it created last.
    private static bool _requested;
    private static bool _installed;
    private static ModelContextMainThreadSequencer? _created;

    /// <summary>Returns the sequencer that runs work on <paramref name="context"/>.</summary>
    /// <param name="context">The model context.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "The same name as IReactiveSchedulerProvider.For.")]
    public static ISequencer For(IRunicModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Shared(context);
    }

    /// <summary>
    /// Sets <see cref="RxSchedulers.MainThreadScheduler"/>, the default output scheduler of every
    /// <see cref="ReactiveCommand"/>, to a sequencer that runs work scheduled in a turn of a
    /// <see cref="RunicModelContext"/> in that context, and other work on the scheduler it replaces. A command
    /// created without an output scheduler then delivers its results and <c>IsExecuting</c> on the model context of
    /// the turn that executes it, which for a bridged command is the window's context, so two windows never share
    /// one. <see cref="ReactiveServiceCollectionExtensions.AddRunicReactiveModelContext"/> calls it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scheduler is installed only while ReactiveUI's main-thread scheduler is one of its defaults
    /// (<see cref="Sequencer.Default"/>, <see cref="Sequencer.CurrentThread"/> or <see cref="Sequencer.Immediate"/>).
    /// A scheduler that an application or UI platform set, such as a WPF dispatcher sequencer or a test scheduler, is
    /// kept, because native UI state belongs on that dispatcher; pass the context's sequencer explicitly there. To
    /// opt out, set <see cref="RxSchedulers.MainThreadScheduler"/> (or call ReactiveUI's builder
    /// <c>WithMainThreadScheduler</c>) before this call, or replace it afterwards. Call this before ViewModels create
    /// their commands: a command reads the main-thread scheduler when it is created.
    /// </para>
    /// <para>
    /// Work scheduled outside a model turn, for example from a timer thread or after an <c>await</c> with
    /// <c>ConfigureAwait(false)</c>, runs on the replaced scheduler, and event 1043
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
            if (current is ModelContextMainThreadSequencer installed)
            {
                if (loggerFactory is not null) installed.Logger = loggerFactory.CreateLogger(RunicViewsTelemetry.LogCategory);
                return true;
            }
            if (current is not TaskPoolSequencer && !ReferenceEquals(current, Sequencer.CurrentThread)
                && !ReferenceEquals(current, Sequencer.Immediate))
                return false;
            // The scheduler WithRunicModelContext() gave ReactiveUI's builder, if it replaces the same default.
            var scheduler = _created is { } created && ReferenceEquals(created.Fallback, current)
                ? created
                : _created = new ModelContextMainThreadSequencer(current);
            if (loggerFactory is not null) scheduler.Logger = loggerFactory.CreateLogger(RunicViewsTelemetry.LogCategory);
            RxSchedulers.MainThreadScheduler = scheduler;
            _installed = true;
            return true;
        }
    }

    // The scheduler that IReactiveUIBuilder.WithRunicModelContext() passes to WithMainThreadScheduler: the installed
    // one, or one that replaces ReactiveUI's default and that a later install also uses.
    internal static ISequencer BuilderScheduler()
    {
        lock (InstallGate)
        {
            _requested = true;
            _installed = true;
            if (RxSchedulers.MainThreadScheduler is ModelContextMainThreadSequencer installed) return installed;
            return _created ??= new ModelContextMainThreadSequencer(Sequencer.Default);
        }
    }

    // Runic Desktop, CS-WebUI and the test host call this before a window resolves its ViewModel. The installed
    // scheduler logs event 1043 through the application's logger factory from then on; a scheduler that is not
    // Runic's is reported as kept (1090) or replaced (1091).
    internal static void OnWindowOpening(ILoggerFactory? loggerFactory)
    {
        bool replaced;
        ISequencer current;
        lock (InstallGate)
        {
            current = RxSchedulers.MainThreadScheduler;
            if (current is ModelContextMainThreadSequencer installed)
            {
                if (loggerFactory is not null) installed.Logger = loggerFactory.CreateLogger(RunicViewsTelemetry.LogCategory);
                return;
            }
            if (!_requested) return;
            replaced = _installed;
        }
        ReactiveSchedulerReports.NotInEffect(loggerFactory, current.GetType(), replaced);
    }

    internal static ISequencer Shared(IRunicModelContext context) =>
        Sequencers.GetValue(context, static context => new RunicModelContextSequencer(context));
}

/// <summary>
/// The <see cref="IReactiveSchedulerProvider"/> that <see cref="ReactiveServiceCollectionExtensions.AddRunicReactiveModelContext"/>
/// registers. It returns <see cref="ReactiveSchedulers.For"/>.
/// </summary>
public sealed class ReactiveSchedulerProvider : IReactiveSchedulerProvider
{
    internal static readonly ReactiveSchedulerProvider Instance = new();

    internal ReactiveSchedulerProvider()
    {
    }

    /// <inheritdoc />
    public ISequencer For(IRunicModelContext context) => ReactiveSchedulers.For(context);
}

// ReactiveUI's main-thread scheduler in a Runic app. Each call resolves its target: the shared sequencer of
// the model context whose turn is running, or the scheduler this one replaced. Now and Timestamp come from the
// same target, so a due time an operator computes and schedules in one call is read on the same clock.
internal sealed class ModelContextMainThreadSequencer(ISequencer fallback) : ISequencer
{
    private readonly OutsideModelTurnReporter _outside = new();

    internal ILogger Logger { get; set; } = TraceLogger.Instance;

    internal ISequencer Fallback => fallback;

    public DateTimeOffset Now => (ModelTurnFlow.Current is { } context ? ReactiveSchedulers.Shared(context) : fallback).Now;
    public long Timestamp => (ModelTurnFlow.Current is { } context ? ReactiveSchedulers.Shared(context) : fallback).Timestamp;

    public void Schedule(IWorkItem item) => Target(item).Schedule(item);

    public void Schedule(IWorkItem item, long dueTimestamp) => Target(item).Schedule(item, dueTimestamp);

    private ISequencer Target(IWorkItem item)
    {
        if (ModelTurnFlow.Current is { } context) return ReactiveSchedulers.Shared(context);
        _outside.Report(Logger, item, fallback.GetType().Name);
        return fallback;
    }
}

internal sealed class RunicModelContextSequencer : ISequencer
{
    private readonly IRunicModelContext _context;
    private DispatchSequencerState _state;

    internal RunicModelContextSequencer(IRunicModelContext context)
    {
        _context = context;
        _state = new(this, PostDrain, RunDrain);
    }

    public DateTimeOffset Now => DispatchSequencerState.Now;
    public long Timestamp => DispatchSequencerState.Timestamp;

    public void Schedule(IWorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _state.Schedule(new ExecutionContextWorkItem(item, ExecutionContext.Capture()));
    }

    public void Schedule(IWorkItem item, long dueTimestamp)
    {
        ArgumentNullException.ThrowIfNull(item);
        _state.Schedule(new ExecutionContextWorkItem(item, ExecutionContext.Capture()), dueTimestamp);
    }

    private bool PostDrain(Action drain)
    {
        // The state object batches multiple scheduled items behind this one
        // drain. Its own queued callback must not carry the first caller's
        // scope; each wrapped IWorkItem restores its captured scope instead.
        if (ExecutionContext.IsFlowSuppressed()) return _context.TryPost(drain);
        using (ExecutionContext.SuppressFlow()) return _context.TryPost(drain);
    }

    private void RunDrain() => _state.RunDrain();

    // DispatchSequencerState batches several IWorkItems behind one posted
    // drain. Each caller must retain its own ambient bridge invocation rather
    // than inheriting the context of whichever item posted that drain first.
    private sealed class ExecutionContextWorkItem(IWorkItem inner, ExecutionContext? context) : IWorkItem
    {
        public void Execute()
        {
            if (context is null)
            {
                inner.Execute();
                return;
            }
            ExecutionContext.Run(context, static state => ((IWorkItem)state!).Execute(), inner);
        }
    }
}
