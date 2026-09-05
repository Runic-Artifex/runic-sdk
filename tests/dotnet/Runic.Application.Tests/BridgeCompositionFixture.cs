using Runic.Application.Bridge;
namespace Runic.Application.Tests;
internal sealed partial class BridgeCompositionState
{
    private readonly TestSnapshot _snapshot = new();
    [BridgeSnapshot] private TestSnapshot Snapshot => _snapshot;
}
internal sealed record TestSnapshot;
