using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Bridge;

namespace Runic.Application.Tests;

internal sealed class BridgeCompositionHandler;

internal sealed class BridgeCompositionDispatcher(BridgeCompositionHandler handler) : IApplicationBridgeDispatcher
{
    public string ProtocolIdentity => "runic.application.tests";
    public int ProtocolVersion => 1;
    public string ManifestFingerprint => new('0', 64);
    internal BridgeCompositionHandler Handler { get; } = handler;

    public ValueTask<BridgeDispatchResult> DispatchAsync(
        JsonElement command,
        BridgeCommandContext context,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
