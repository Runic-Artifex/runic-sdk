using System.Runtime.CompilerServices;
using ReactiveUI.Primitives.Advanced;
using ReactiveUI.Primitives.Concurrency;

namespace Runic.Navigation.ReactiveUI;

/// <summary>Provides ReactiveUI schedulers bound to Runic model contexts.</summary>
public interface IRunicReactiveSchedulerProvider
{
    /// <summary>Returns a sequencer that runs work on <paramref name="context"/>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "Published member name shared with Runic.Navigation.ReactiveUI.Reactive.")]
    ISequencer For(IRunicModelContext context);
}

/// <summary>
/// Creates an <see cref="ISequencer"/> that delivers scheduled work through a model context.
/// It intentionally does not alter process-global ReactiveUI scheduler configuration.
/// </summary>
/// <remarks>
/// A sequencer keeps its ordering state, so the provider returns one sequencer per context for
/// as long as the context is alive.
/// </remarks>
public sealed class RunicReactiveSchedulerProvider : IRunicReactiveSchedulerProvider
{
    private readonly ConditionalWeakTable<IRunicModelContext, RunicModelContextSequencer> _sequencers = new();

    /// <inheritdoc />
    public ISequencer For(IRunicModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _sequencers.GetValue(context, static context => new RunicModelContextSequencer(context));
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
