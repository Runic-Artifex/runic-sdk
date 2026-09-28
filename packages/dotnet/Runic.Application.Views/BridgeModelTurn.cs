using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FieldWriteTurnProbe")]
[assembly: InternalsVisibleTo("FieldRegistryProviderProbe")]
[assembly: InternalsVisibleTo("OperationAcceptanceProbe")]
[assembly: InternalsVisibleTo("SourceBackedIndependentDraftProbe")]

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
        if (_context is IRunicSynchronousModelContext synchronous) return synchronous.Run(work);
        if (_context is not null) return _context.InvokeAsync(work).AsTask().GetAwaiter().GetResult();
        lock (_gate) return work();
    }

    public void Run(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_context is IRunicSynchronousModelContext synchronous) synchronous.Run(work);
        else if (_context is not null) _context.InvokeAsync(work).AsTask().GetAwaiter().GetResult();
        else lock (_gate) work();
    }
}
