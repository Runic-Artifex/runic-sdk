using Runic.Navigation;

namespace Runic.Application.Views;

// A short, re-entrant synchronous turn over one actual ViewModel. It shares
// the same weakly-owned monitor used by ViewModelBridge, so an auxiliary host
// service can participate without introducing a second model lock.
internal sealed class BridgeModelTurn : IBridgeModelTurn
{
    private readonly object _gate;
    private readonly IRunicModelContext? _context;

    private BridgeModelTurn(object gate, IRunicModelContext? context)
    {
        _gate = gate;
        _context = context;
    }

    internal static BridgeModelTurn For(object model)
    {
        ArgumentNullException.ThrowIfNull(model);
        RunicModelContextRegistry.Shared.TryGet(model, out var context);
        return new(BridgeModelGates.For(model), context);
    }

    // Whether the calling thread is inside this model's turn, where waiting
    // for another turn of the model would deadlock.
    public bool IsCurrent => _context?.IsExecuting ?? Monitor.IsEntered(_gate);

    public T Run<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_context is not null) return RunicModelTurns.Run(_context, work);
        lock (_gate) return work();
    }

    public void Run(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_context is not null) RunicModelTurns.Run(_context, work);
        else lock (_gate) work();
    }

    // Whether the calling thread is outside the model context's turn: a change
    // raised here, such as a DynamicData Bind on a background scheduler, waits
    // for a turn and can race the window's own work. False without a context.
    public bool IsOutsideContextTurn => _context is { IsExecuting: false };

    // A change notification's turn. A context that was closed rejects the turn
    // before it starts with ObjectDisposedException; the notification then counts
    // as one for a released Bridge (it returns false) instead of throwing into the
    // code that raised it, such as a DynamicData Bind. An exception of the work
    // itself still propagates.
    public bool TryRunNotification(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_context is null)
        {
            lock (_gate) work();
            return true;
        }
        var started = 0;
        try
        {
            RunicModelTurns.Run(_context, () =>
            {
                Volatile.Write(ref started, 1);
                work();
            });
            return true;
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref started) == 0) { return false; }
    }

    // TryRunNotification with a result; closed is the result for a closed context.
    public T RunNotification<T>(Func<T> work, T closed)
    {
        ArgumentNullException.ThrowIfNull(work);
        var result = closed;
        return TryRunNotification(() => result = work()) ? result : closed;
    }

    // A turn for work that can wait for it without holding a thread, as a route's delivery does: awaited, not blocked
    // on. Inline inside the turn; a model without a context takes the model's lock, which turns hold only briefly.
    public ValueTask RunAsync(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_context is null)
        {
            lock (_gate) work();
            return ValueTask.CompletedTask;
        }
        if (_context.IsExecuting)
        {
            work();
            return ValueTask.CompletedTask;
        }
        return _context.InvokeAsync(work);
    }

    // Releases subscriptions even after an application-owned context was
    // disposed before the bridge that captured it.
    public void RunForTeardown(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        RunicModelTurns.RunForTeardown(_context, work, _gate);
    }
}
