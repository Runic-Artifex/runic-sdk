using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Bridge;
using Runic.Application.Setup.Contract;

var handler = new AotSetupHandler();
await using var session = new ApplicationBridgeSession(new SetupBridgeDispatcher(handler));
BridgeHostEnvelope initialized = await session.DispatchAsync(new BridgeClientEnvelope
{
    Protocol = "runic.artifex.setup",
    Version = 1,
    ContractFingerprint = "a95762f0da02103aa9198c6cfff1f247596d48c9f3154adb3cd97ea31cb39725",
    ConnectionEpoch = 0,
    Kind = "initialize",
    CommandId = Guid.Parse("00000000-0000-4000-8000-000000000001"),
    Payload = JsonDocument.Parse("""{"_tag":"InitializeApplication"}""").RootElement.Clone(),
});
if (initialized.Kind != "snapshot" || initialized.Payload.GetProperty("viewId").GetString() != "Welcome")
{
    return 1;
}
Console.WriteLine("application-bridge-aot-ok");
return 0;

internal sealed class AotSetupHandler : ISetupBridgeHandler
{
    public ValueTask<ApplicationInitialized> InitializeApplicationAsync(InitializeApplication command, BridgeCommandContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ApplicationInitialized
        {
            Tag = "ApplicationInitialized",
            Snapshot = new ApplicationInitializedSnapshot
            {
                ViewId = "Welcome", Revision = 0, SelectedFeatures = [],
                CanNavigateBack = false, CanNavigateNext = true,
            },
        });

    public ValueTask<OperationCancellationAccepted> CancelOperationAsync(CancelOperation command, BridgeCommandContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new OperationCancellationAccepted { Tag = "OperationCancellationAccepted", OperationId = command.OperationId, Accepted = false, Revision = context.CurrentRevision });

    public ValueTask<NavigationAccepted> NavigateAsync(Navigate command, BridgeCommandContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new NavigationAccepted
        {
            Tag = "NavigationAccepted",
            Snapshot = new NavigationAcceptedSnapshot
            {
                ViewId = command.Target, Revision = context.CurrentRevision + 1, SelectedFeatures = [],
                CanNavigateBack = true, CanNavigateNext = true,
            },
        });

    public ValueTask<DestinationSelected> SelectDestinationAsync(SelectDestination command, BridgeCommandContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new DestinationSelected
        {
            Tag = "DestinationSelected",
            Destination = new DestinationSelectedDestination { SelectionId = Guid.NewGuid(), DisplayName = "AOT", AvailableBytes = 1 },
            Revision = context.CurrentRevision + 1,
        });

    public ValueTask<InstallationStarted> StartInstallationAsync(StartInstallation command, BridgeCommandContext context, CancellationToken cancellationToken)
    {
        BridgeOperationId operation = context.Operations.Start(static (_, _) => ValueTask.CompletedTask, cancellationToken);
        return ValueTask.FromResult(new InstallationStarted
        {
            Tag = "InstallationStarted", CommandId = context.CommandId.Value,
            OperationId = operation.Value, Revision = context.CurrentRevision + 1,
        });
    }
}
