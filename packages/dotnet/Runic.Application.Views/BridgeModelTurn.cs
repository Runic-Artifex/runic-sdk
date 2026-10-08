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

    // Releases subscriptions even after an application-owned context was
    // disposed before the bridge that captured it.
    public void RunForTeardown(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        RunicModelTurns.RunForTeardown(_context, work, _gate);
    }
}
