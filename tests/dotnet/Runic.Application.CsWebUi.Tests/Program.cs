using System.Text;
using System.Text.Json;
using Runic.Application.Bridge;
using Runic.Application.CsWebUi;

try
{
    await RunAsync();
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"not ok - CS-WebUI bridge tests\n{exception}");
    return 1;
}

static async Task RunAsync()
{
    var limits = BridgeLimits.Default;
    await using var session = new ApplicationBridgeSession(new Dispatcher());
    await using var mailbox = new BridgeMailbox(session, limits);
    byte[] Frame(string kind, int epoch, string fingerprint = "") => JsonSerializer.SerializeToUtf8Bytes(new {
        protocol = "runic.test", version = 1, contractFingerprint = fingerprint == "" ? new string('a', 64) : fingerprint,
        connectionEpoch = epoch, kind, commandId = Guid.NewGuid(), sessionId = kind == "initialize" ? (Guid?)null : session.Id.Value,
        expectedRevision = kind == "initialize" ? (long?)null : 0, payload = new { }
    });
    await Reject(() => mailbox.DispatchAsync(1, 1, Frame("dispatch", 0), default).AsTask());
    await Reject(() => mailbox.DispatchAsync(1, 1, new byte[limits.MaxFrameBytes + 1], default).AsTask());
    var first = JsonDocument.Parse(await mailbox.DispatchAsync(1, 1, Frame("initialize", 0), default));
    Check(first.RootElement[0].GetProperty("kind").GetString() == "snapshot", "initialization");
    await Reject(() => mailbox.PollAsync(2, 1, default).AsTask());
    await Reject(() => mailbox.DispatchAsync(2, 2, Frame("initialize", 0), default).AsTask());
    await session.PublishAsync(new BridgeEventPayload(JsonDocument.Parse("{\"value\":1}").RootElement));
    string events = await mailbox.PollAsync(1, 1, default);
    Check(JsonDocument.Parse(events).RootElement.GetArrayLength() == 1, "asynchronous events");
    await mailbox.DisconnectAsync(1, 1);
    await Reject(() => mailbox.PollAsync(1, 1, default).AsTask());
    await mailbox.DispatchAsync(1, 2, Frame("initialize", 1), default);
    await Reject(() => mailbox.DispatchAsync(1, 1, Frame("dispatch", 0), default).AsTask());
    var receipt = JsonDocument.Parse(await mailbox.DispatchAsync(1, 2, Frame("dispatch", 1), default));
    Check(receipt.RootElement[0].GetProperty("kind").GetString() == "receipt", "dispatch after reconnect");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    await Reject(() => mailbox.PollAsync(1, 2, cancelled.Token).AsTask());
    // Queue pressure must be explicit, not silent event loss.
    for (int i = 0; i < 70; i++) { await session.PublishAsync(new BridgeEventPayload(JsonDocument.Parse("{}").RootElement)); await Task.Delay(5); }
    await Task.Delay(100);
    await Reject(() => mailbox.PollAsync(1, 2, default).AsTask());
    await mailbox.DispatchAsync(1, 3, Frame("initialize", 2), default);
    Check(await mailbox.PollAsync(1, 3, default) == "[]", "resynchronization clears overflow");
    await mailbox.DisposeAsync();
    await Reject(() => mailbox.PollAsync(1, 3, default).AsTask());
    Console.WriteLine("CS-WebUI admission, reconnect, bounded events, cancellation and disposal passed.");

}

static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
static async Task Reject(Func<Task> action)
{
    bool rejected = false;
    try { await action(); } catch (InvalidOperationException) { rejected = true; } catch (OperationCanceledException) { rejected = true; }
    Check(rejected, "Expected rejection");
}
sealed class Dispatcher : IApplicationBridgeDispatcher
{
    public string ProtocolIdentity => "runic.test";
    public int ProtocolVersion => 1;
    public string ManifestFingerprint => new('a', 64);
    public ValueTask<JsonElement> GetSnapshotAsync(BridgeSnapshotContext context, CancellationToken token) => ValueTask.FromResult(JsonDocument.Parse("{}").RootElement.Clone());
    public ValueTask<BridgeDispatchResult> DispatchAsync(JsonElement command, BridgeCommandContext context, CancellationToken token) => ValueTask.FromResult(new BridgeDispatchResult(JsonDocument.Parse("{}").RootElement.Clone()));
}
