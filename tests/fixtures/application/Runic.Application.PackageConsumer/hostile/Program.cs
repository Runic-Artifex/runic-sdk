using Runic.Application;
using Runic.Application.Bridge;

[assembly: RunicApplicationManifest("hostile", Version = "1.0.0", Provenance = "package")]
[assembly: ApplicationBridgeContract("hostile", 1)]

internal sealed partial class Handler
{
    [BridgeSnapshot]
    private Snapshot Snapshot => new(0);

    // Infrastructure parameters alone cannot define an application command.
    [BridgeCommand]
    private Receipt Execute(BridgeCommandContext context) => new();
}

internal sealed record Snapshot(int Revision);
internal sealed record Receipt;

internal static class Program
{
    private static void Main() { }
}
