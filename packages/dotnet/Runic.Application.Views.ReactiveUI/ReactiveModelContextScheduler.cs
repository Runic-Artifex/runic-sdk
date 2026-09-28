using ReactiveUI.Primitives.Advanced;
using ReactiveUI.Primitives.Concurrency;
using Runic.Application.Views;

namespace Runic.Application.Views.ReactiveUI;

/// <summary>Provides ReactiveUI schedulers bound to Runic model contexts.</summary>
public interface IRunicReactiveSchedulerProvider
{
    ISequencer For(IRunicModelContext context);
}

/// <summary>
/// Creates an <see cref="ISequencer"/> that delivers scheduled work through a model context.
/// It intentionally does not alter process-global ReactiveUI scheduler configuration.
/// </summary>
public sealed class RunicReactiveSchedulerProvider : IRunicReactiveSchedulerProvider
{
    public ISequencer For(IRunicModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new RunicModelContextSequencer(context);
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
        _state.Schedule(item);
    }

    public void Schedule(IWorkItem item, long dueTimestamp)
    {
        ArgumentNullException.ThrowIfNull(item);
        _state.Schedule(item, dueTimestamp);
    }

    private bool PostDrain(Action drain) => _context.TryPost(drain);

    private void RunDrain() => _state.RunDrain();
}
