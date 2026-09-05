using Runic.Application.Bridge;
namespace ReferencedBridge;

public sealed class ScopedDependency : IAsyncDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public bool Disposed { get; private set; }
    public ValueTask DisposeAsync() { Disposed = true; return default; }
}
internal sealed partial class ModuleCommands(ScopedDependency dependency)
{
    [BridgeCommand]
    private Task<ModuleReceipt> Read(ModuleRequest request) => Task.FromResult(new ModuleReceipt(dependency.Id, request.Note));
}
[BridgeTag("ReadModule"), BridgeName("ModuleRequest")]
internal sealed record ModuleRequest(BridgeOptional<string?> Note);
internal sealed record ModuleReceipt(Guid InstanceId, BridgeOptional<string?> Note);
