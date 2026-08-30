using System.Text.Json;
using Runic.Application.Bridge;
using Runic.Application.Template.Contract;

namespace RunicDesktopApp;

internal static class CounterSmokeTest
{
    internal static async Task<int> RunAsync()
    {
        await using var session = new ApplicationBridgeSession(
            new CounterBridgeDispatcher(new CounterBridgeHandler()));
        BridgeHostEnvelope snapshot = await session.DispatchAsync(Envelope(
            "initialize", null, null, """{"_tag":"InitializeApplication"}"""));
        BridgeHostEnvelope incremented = await session.DispatchAsync(Envelope(
            "dispatch", session.Id.Value, 0, """{"_tag":"IncrementCounter","step":2}"""));
        bool passed = snapshot.Kind == "snapshot" &&
            incremented.Kind == "receipt" &&
            incremented.Payload.GetProperty("snapshot").GetProperty("count").GetInt64() == 2 &&
            incremented.Revision == 1;
        Console.WriteLine(passed
            ? "Named Application Bridge command and authoritative snapshot smoke test passed."
            : "Native counter smoke test failed.");
        return passed ? 0 : 1;
    }

    private static BridgeClientEnvelope Envelope(string kind, Guid? sessionId, long? revision, string payload) => new()
    {
        Protocol = "runic.artifex.counter",
        Version = 1,
        ContractFingerprint = "4e873f5967e86eeded5e26d8faf27c305464f1272b90935cc8a1b09365471508",
        ConnectionEpoch = 0,
        Kind = kind,
        CommandId = Guid.NewGuid(),
        SessionId = sessionId,
        ExpectedRevision = revision,
        Payload = JsonDocument.Parse(payload).RootElement.Clone(),
    };
}
