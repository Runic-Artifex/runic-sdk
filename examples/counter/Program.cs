using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Bridge;
using Runic.Application.Generated;

[assembly: ApplicationBridgeContract("runic.examples.counter", 1, ContractName = "Counter")]

var services = new ServiceCollection();
CounterBridgeContract.ConfigureServices(services);
await using var provider = services.BuildServiceProvider();
await using var session = ApplicationBridgeSessionFactory.Create(provider);

var initial = await session.DispatchAsync(Frame("initialize", "{}"));
Console.WriteLine($"Initial snapshot: {initial.Payload}");
var updated = await session.DispatchAsync(Frame("dispatch", """{"_tag":"Increment","step":2}"""));
Console.WriteLine($"Increment receipt: {updated.Payload}");
if (updated.Kind != "receipt" || updated.Payload.GetProperty("count").GetInt32() != 2)
    throw new InvalidOperationException("Counter did not advance.");

BridgeClientEnvelope Frame(string kind, string payload) => new()
{
    Protocol = CounterBridgeContract.ProtocolIdentity,
    Version = 1,
    ContractFingerprint = CounterBridgeContract.Fingerprint,
    ConnectionEpoch = 0,
    Kind = kind,
    CommandId = Guid.NewGuid(),
    SessionId = kind == "initialize" ? null : session.Id.Value,
    Payload = JsonDocument.Parse(payload).RootElement.Clone(),
};

internal sealed partial class Counter
{
    private int _count;
    [BridgeSnapshot] private Snapshot Snapshot => new(_count);
    [BridgeCommand(AdvancesRevision = true)]
    private Incremented Increment(Increment command) => new(_count += command.Step);
}

internal sealed record Snapshot(int Count);
internal sealed record Increment([property: BridgeMinimum(1), BridgeMaximum(10)] int Step);
internal sealed record Incremented(int Count);
