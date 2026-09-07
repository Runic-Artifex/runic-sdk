using System.Text.Json;
using Runic.Application.Bridge;
using Runic.Application.CsWebUi;

internal static class PresentationStopTests
{
    internal static async Task RunAsync()
    {
        var lifetime = new Lifetime();
        var dispatcher = new WaitingDispatcher(lifetime);
        await using var session = new ApplicationBridgeSession(dispatcher, presentationLifetimes: [lifetime]);
        await using var mailbox = new BridgeMailbox(session, BridgeLimits.Default);
        byte[] Frame(string kind, int epoch) => JsonSerializer.SerializeToUtf8Bytes(new BridgeClientEnvelope()
        {
            Protocol = dispatcher.ProtocolIdentity, Version = 1, ContractFingerprint = dispatcher.ManifestFingerprint,
            ConnectionEpoch = epoch, Kind = kind, CommandId = Guid.NewGuid(),
            SessionId = kind == "initialize" ? null : session.Id.Value, ExpectedRevision = kind == "initialize" ? null : 0,
            Payload = JsonDocument.Parse("{}").RootElement.Clone(),
        }, JsonSerializerOptions.Web);
        await mailbox.DispatchAsync(1, 1, Frame("initialize", 0), default);
        await mailbox.DisconnectAsync(1, 1);
        await mailbox.DispatchAsync(1, 2, Frame("initialize", 1), default);
        if (lifetime.Stops != 0) throw new InvalidOperationException("Reconnect stopped the logical owner.");
        var pending = mailbox.DispatchAsync(1, 2, Frame("dispatch", 1), default).AsTask();
        await dispatcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stop = mailbox.DisposeAsync().AsTask();
        await Task.WhenAll(stop, pending).WaitAsync(TimeSpan.FromSeconds(3));
        if (lifetime.Stops != 1) throw new InvalidOperationException("Transport did not stop the owner before waiting for its command.");
    }
    private sealed class Lifetime : IApplicationPresentationLifetime
    {
        internal int Stops;
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask StopAsync() { Stops++; Stopped.TrySetResult(); return ValueTask.CompletedTask; }
    }
    private sealed class WaitingDispatcher(Lifetime lifetime) : IApplicationBridgeDispatcher
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ProtocolIdentity => "runic.test";
        public int ProtocolVersion => 1;
        public string ManifestFingerprint => new('a', 64);
        public ValueTask<JsonElement> GetSnapshotAsync(BridgeSnapshotContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(JsonDocument.Parse("{}").RootElement.Clone());
        public async ValueTask<BridgeDispatchResult> DispatchAsync(JsonElement command, BridgeCommandContext context, CancellationToken cancellationToken)
        { Entered.SetResult(); await lifetime.Stopped.Task; return new(JsonDocument.Parse("{}").RootElement.Clone()); }
    }
}
