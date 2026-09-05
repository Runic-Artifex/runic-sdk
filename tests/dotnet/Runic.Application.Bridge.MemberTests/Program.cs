using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Bridge;
using Runic.Application.Bridge.Generated;
using Runic.Application.Generated;

[assembly: ApplicationBridgeContract("runic.member.tests", 1, ContractName = "Member")]

var services = new ServiceCollection();
MemberBridgeContract.ConfigureServices(services);
services.AddScoped<ReferencedBridge.ScopedDependency>();
var dependencies = new List<ReferencedBridge.ScopedDependency>();
services.AddScoped(_ => { var dependency = new ReferencedBridge.ScopedDependency(); dependencies.Add(dependency); return dependency; });
// Explicit registration supersedes the generated TryAddScoped default.
services.AddScoped(_ => new State { Count = 7 });
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
await using var session = ApplicationBridgeSessionFactory.Create(provider);
var initial = await session.DispatchAsync(Frame("initialize", "{}"));
Check(initial.Kind == "snapshot" && initial.Payload.GetProperty("count").GetInt32() == 7, "Snapshot or explicit registration override failed.");
var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
session.EventProduced += (_, _) => published.TrySetResult();
var receipt = await session.DispatchAsync(Frame("dispatch", """{"_tag":"Increment","step":2}""", session.Id.Value));
Check(receipt.Kind == "receipt" && receipt.Payload.GetProperty("snapshot").GetProperty("count").GetInt32() == 9, "Private member dispatch failed.");
await published.Task.WaitAsync(TimeSpan.FromSeconds(2));
var firstModule = await session.DispatchAsync(Frame("dispatch", """{"_tag":"ReadModule"}""", session.Id.Value));
Check(firstModule.Kind == "receipt", "Referenced internal command was not discovered.");
Check(!firstModule.Payload.TryGetProperty("note", out _), "A missing optional property must stay missing.");
var unique = await session.DispatchAsync(Frame("dispatch", """{"_tag":"EchoValues","values":[{"value":1},{"value":2}]}""", session.Id.Value));
Check(unique.Kind == "receipt" && unique.Payload.GetProperty("values").GetArrayLength() == 2, "CLR equality must not reject structurally distinct wire values.");
var duplicate = await session.DispatchAsync(Frame("dispatch", """{"_tag":"EchoValues","values":[{"value":1},{"value":1}]}""", session.Id.Value));
Check(duplicate.Kind == "error", "Uniqueness must reject structurally identical wire values.");
Guid instanceId = firstModule.Payload.GetProperty("instanceId").GetGuid();
var reconnect = await session.DispatchAsync(Frame("initialize", "{}", session.Id.Value, 1));
Check(reconnect.Kind == "snapshot" && reconnect.Payload.GetProperty("count").GetInt32() == 9, "Scoped state lost across reconnect.");
var reconnectedModule = await session.DispatchAsync(Frame("dispatch", """{"_tag":"ReadModule","note":null}""", session.Id.Value, 1));
Check(reconnectedModule.Payload.GetProperty("instanceId").GetGuid() == instanceId, "Referenced scope changed across reconnect.");
Check(reconnectedModule.Payload.GetProperty("note").ValueKind == JsonValueKind.Null, "Explicit null must stay present.");
foreach (string invalid in new[] { """{"_tag":"Increment","step":0}""", """{"_tag":"Increment","step":11}""", """{"_tag":"Increment","step":2,"extra":true}""", """{"_tag":"Increment","step":1,"step":2}""" })
    Check((await session.DispatchAsync(Frame("dispatch", invalid, session.Id.Value, 1))).Kind == "error", "Strict codec accepted invalid JSON.");
Check((await session.DispatchAsync(Frame("initialize", """{"_tag":"ApplicationInitialize"}""", session.Id.Value, 2))).Kind == "error", "Initialization must only accept an empty object.");
await using (var second = ApplicationBridgeSessionFactory.Create(provider))
{
    var snapshot = await second.DispatchAsync(Frame("initialize", "{}"));
    Check(snapshot.Payload.GetProperty("count").GetInt32() == 7, "A new session must receive independent state.");
    var other = await second.DispatchAsync(Frame("dispatch", """{"_tag":"ReadModule","note":"value"}""", second.Id.Value));
    Check(other.Payload.GetProperty("instanceId").GetGuid() != instanceId, "Referenced dependency leaked across sessions.");
    Check(other.Payload.GetProperty("note").GetString() == "value", "Optional value round trip failed.");
}
Check(dependencies.Count == 2 && dependencies[1].Disposed && !dependencies[0].Disposed, "Async scope disposal did not isolate sessions.");
await session.DisposeAsync();
Check(dependencies[0].Disposed, "The logical session must own its async scope.");
var missing = new ServiceCollection();
MemberBridgeContract.ConfigureServices(missing);
await using var missingProvider = missing.BuildServiceProvider();
await using var missingSession = ApplicationBridgeSessionFactory.Create(missingProvider);
Check((await missingSession.DispatchAsync(Frame("initialize", "{}"))).Kind == "snapshot", "The missing dependency fixture must first establish a valid session.");
Check((await missingSession.DispatchAsync(Frame("dispatch", """{"_tag":"ReadModule"}""", missingSession.Id.Value))).Kind == "error", "Unregistered constructor dependencies must fail activation.");
Console.WriteLine("PASS: private sync/ValueTask/Task members, referenced modules, strict codecs, optional nulls, DI overrides, reconnection, isolation, async disposal, and missing dependency rejection.");
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static BridgeClientEnvelope Frame(string kind, string payload, Guid? session = null, long epoch = 0) => new() {
    Protocol = MemberBridgeContract.ProtocolIdentity, Version = 1, ContractFingerprint = MemberBridgeContract.Fingerprint,
    ConnectionEpoch = epoch, Kind = kind, CommandId = Guid.NewGuid(), SessionId = session,
    Payload = JsonDocument.Parse(payload).RootElement.Clone()
};
internal sealed partial class State
{
    public int Count { get; set; }
    [BridgeSnapshot] private Snapshot Snapshot => new(Count);
}
internal sealed partial class Commands(State state)
{
    [BridgeCommand]
    private ValuesEchoed Echo(EchoValues command) => new(command.Values, state.Count);

    [BridgeCommand(AdvancesRevision = true)]
    private async ValueTask<Incremented> Increment(Increment command, BridgeCommandContext context, CancellationToken cancellationToken)
    {
        state.Count += command.Step;
        var snapshot = new Snapshot(state.Count);
        await context.Events.PublishChangedAsync(new Changed(snapshot), cancellationToken: cancellationToken);
        return new(snapshot);
    }
}
internal sealed record Snapshot(int Count);
internal sealed record Increment([property: BridgeMinimum(1), BridgeMaximum(10)] int Step);
internal sealed record Incremented(Snapshot Snapshot);
[BridgeEvent] internal sealed record Changed(Snapshot Snapshot);

internal sealed record EchoValues([property: BridgeUnique] IReadOnlyList<ValueItem> Values);
internal sealed record ValuesEchoed([property: BridgeUnique] IReadOnlyList<ValueItem> Values, int Count);
internal sealed class ValueItem(int value) : IEquatable<ValueItem>
{
    public int Value { get; } = value;
    public bool Equals(ValueItem? other) => other is not null;
    public override bool Equals(object? obj) => obj is ValueItem;
    public override int GetHashCode() => 0;
}
