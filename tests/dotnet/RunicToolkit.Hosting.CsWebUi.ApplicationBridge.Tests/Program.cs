using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RunicToolkit.ApplicationBridge;
using RunicToolkit.Hosting.CsWebUi.ApplicationBridge;

namespace RunicToolkit.Hosting.CsWebUi.ApplicationBridge.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("one binding carries named domain commands and host events", RoundTrip),
            ("native identity is pinned and reconnect requires initialization", Identity),
            ("invalid frames close the untrusted client before dispatch", InvalidFrames),
            ("bridge disposal owns exact session teardown", Disposal),
        ];
        foreach ((string name, Func<Task> run) in tests)
        {
            try { await run(); Console.WriteLine($"ok - {name}"); }
            catch (Exception exception) { Console.Error.WriteLine($"not ok - {name}\n{exception}"); return 1; }
        }
        return 0;
    }

    private static async Task RoundTrip()
    {
        var window = new FakeWindow();
        await using var session = new ApplicationBridgeSession(new FakeDispatcher());
        await using var bridge = CsWebUiApplicationBridge.Attach(window, session);
        Equal(1, window.BindCount);
        Equal("__runicToolkit_applicationBridge_send", window.BindingName);
        FakeEvent initialized = await window.InvokeAsync(Initialize(), 7, 11);
        JsonElement snapshot = Decode(initialized.Sent.Single().Frame);
        Equal("snapshot", snapshot.GetProperty("kind").GetString());
        string sessionId = snapshot.GetProperty("sessionId").GetString()!;

        FakeEvent dispatched = await window.InvokeAsync(Dispatch(sessionId), 7, 11);
        Equal("receipt", Decode(dispatched.Sent.Single().Frame).GetProperty("kind").GetString());
        Equal(1, window.Sent.Count);
        Equal("event", Decode(window.Sent.Single().Frame).GetProperty("kind").GetString());
    }

    private static async Task Identity()
    {
        var window = new FakeWindow();
        await using var session = new ApplicationBridgeSession(new FakeDispatcher());
        await using var bridge = CsWebUiApplicationBridge.Attach(window, session);
        _ = await window.InvokeAsync(Initialize(), 7, 11);
        Equal((7UL, 11UL), bridge.ConnectionIdentity!.Value);
        FakeEvent attacker = await window.InvokeAsync(Initialize(), 8, 12);
        True(attacker.Closed);
        FakeEvent reconnectMutation = await window.InvokeAsync(Dispatch(session.Id.Value.ToString()), 7, 12);
        True(reconnectMutation.Closed);
        FakeEvent reconnect = await window.InvokeAsync(Initialize(Guid.Parse("00000000-0000-4000-8000-000000000006")), 7, 12);
        False(reconnect.Closed);
        Equal((7UL, 12UL), bridge.ConnectionIdentity!.Value);
    }

    private static async Task InvalidFrames()
    {
        var window = new FakeWindow();
        await using var session = new ApplicationBridgeSession(new FakeDispatcher());
        await using var bridge = CsWebUiApplicationBridge.Attach(window, session, new() { Limits = new BridgeLimits { MaxFrameBytes = 1024 } });
        FakeEvent invalid = await window.InvokeAsync(Encoding.UTF8.GetBytes("not-json"));
        True(invalid.Closed);
        FakeEvent oversized = await window.InvokeAsync(new byte[1025]);
        True(oversized.Closed);
    }

    private static async Task Disposal()
    {
        var window = new FakeWindow();
        var session = new ApplicationBridgeSession(new FakeDispatcher());
        var bridge = CsWebUiApplicationBridge.Attach(window, session);
        await bridge.DisposeAsync();
        await bridge.DisposeAsync();
        Equal(1, window.BindingDisposeCount);
        bool disposed = false;
        try { _ = await session.DispatchAsync(JsonEnvelope("initialize", Guid.NewGuid(), null, null, "{}")); }
        catch (ObjectDisposedException) { disposed = true; }
        True(disposed);
    }

    private static byte[] Initialize(Guid? command = null) => Encoding.UTF8.GetBytes($$$"""
        {"protocol":"runic.test","version":1,"kind":"initialize","commandId":"{{{command ?? Guid.Parse("00000000-0000-4000-8000-000000000001")}}}","payload":{"_tag":"InitializeApplication"}}
        """);

    private static byte[] Dispatch(string sessionId) => Encoding.UTF8.GetBytes($$$"""
        {"protocol":"runic.test","version":1,"kind":"dispatch","commandId":"00000000-0000-4000-8000-000000000002","sessionId":"{{{sessionId}}}","expectedRevision":0,"payload":{"_tag":"Navigate","target":"Complete"}}
        """);

    private static BridgeClientEnvelope JsonEnvelope(string kind, Guid command, Guid? session, long? revision, string payload) => new()
    {
        Protocol = "runic.test", Version = 1, Kind = kind, CommandId = command,
        SessionId = session, ExpectedRevision = revision,
        Payload = JsonDocument.Parse(payload).RootElement.Clone(),
    };

    private static JsonElement Decode(byte[] frame) => JsonDocument.Parse(frame).RootElement.Clone();
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, received {actual}."); }
    private static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
    private static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
}

internal sealed class FakeDispatcher : IApplicationBridgeDispatcher
{
    public string ProtocolIdentity => "runic.test";
    public int ProtocolVersion => 1;
    public string ManifestFingerprint => new('a', 64);

    public async ValueTask<BridgeDispatchResult> DispatchAsync(JsonElement command, BridgeCommandContext context, CancellationToken cancellationToken)
    {
        string tag = command.GetProperty("_tag").GetString()!;
        if (tag == "InitializeApplication")
        {
            return new(JsonDocument.Parse("""{"_tag":"ApplicationInitialized","snapshot":{"revision":0,"view":"Welcome"}}""").RootElement.Clone());
        }
        await context.Events.PublishAsync(new(
            JsonDocument.Parse("""{"_tag":"NavigationChanged","revision":1,"view":"Complete"}""").RootElement.Clone(),
            AdvancesRevision: true), cancellationToken);
        return new(JsonDocument.Parse("""{"_tag":"NavigationAccepted","revision":1}""").RootElement.Clone());
    }
}

internal sealed class FakeWindow : IApplicationBridgeWindow
{
    private Func<IApplicationBridgeEvent, CancellationToken, ValueTask>? _callback;
    public int BindCount { get; private set; }
    public int BindingDisposeCount { get; private set; }
    public string? BindingName { get; private set; }
    public List<SentFrame> Sent { get; } = [];

    public IDisposable Bind(string name, Func<IApplicationBridgeEvent, CancellationToken, ValueTask> callback)
    {
        BindCount++; BindingName = name; _callback = callback;
        return new ActionDisposable(() => BindingDisposeCount++);
    }
    public void SendRaw(string functionName, ReadOnlySpan<byte> data) => Sent.Add(new(functionName, data.ToArray()));
    public async Task<FakeEvent> InvokeAsync(byte[] frame, ulong client = 1, ulong connection = 1)
    {
        var value = new FakeEvent(frame, client, connection);
        await _callback!(value, CancellationToken.None);
        return value;
    }
}

internal sealed class FakeEvent(byte[] frame, ulong client, ulong connection) : IApplicationBridgeEvent
{
    public ulong ClientId => client;
    public ulong ConnectionId => connection;
    public int ArgumentCount => 1;
    public bool Closed { get; private set; }
    public List<SentFrame> Sent { get; } = [];
    public byte[] GetBytes(int index) => index == 0 ? frame : throw new ArgumentOutOfRangeException(nameof(index));
    public void SendRaw(string functionName, ReadOnlySpan<byte> data) => Sent.Add(new(functionName, data.ToArray()));
    public void CloseClient() => Closed = true;
}

internal sealed record SentFrame(string Function, byte[] Frame);
internal sealed class ActionDisposable(Action dispose) : IDisposable { public void Dispose() => dispose(); }
