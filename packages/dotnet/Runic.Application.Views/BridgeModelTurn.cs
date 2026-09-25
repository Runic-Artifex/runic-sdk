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

    private BridgeModelTurn(object gate) => _gate = gate;

    internal static BridgeModelTurn For(object model) => new(BridgeModelGates.For(model));

    public T Run<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate) return work();
    }

    public void Run(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate) work();
    }
}
