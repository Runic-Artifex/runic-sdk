using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Views protocol 4, P3: the window's request dispatcher (specs/application/README.md, "Handshake", "Errors",
// "receive" and "Transport"), through the in-memory transport and a transport without targeted pushes.
public sealed class BridgeDispatcherTests
{
    private const string Hello = """{"kind":"hello","protocol":4,"session":"s-1","client":{"name":"test-client","version":"1.0.0"}}""";

    [Test]
    public void HelloNamesTheProtocolAndTheImplementation()
    {
        using var window = new Window();
        var reply = Parse(window.Transport.Request(Hello));
        Require(reply.GetProperty("ok").GetBoolean() && reply.GetProperty("protocol").GetInt32() == 4,
            $"The hello was answered {reply}.");
        var implementation = reply.GetProperty("implementation");
        Require(implementation.GetProperty("name").GetString() == "Runic.Application.Views"
            && implementation.GetProperty("version").GetString() is { Length: > 0 } version && !version.Contains('+'),
            $"The implementation identity is {implementation}.");
    }

    [Test]
    public void RequestsBeforeHelloAreRejectedAndTheConnectionReceivesNoPushes()
    {
        using var window = new Window();
        RequireError(window.Transport.Request("""{"kind":"snapshot","route":"notes"}"""), "rejected", "no-hello");
        RequireError(window.Transport.Request("""{"kind":"receive"}"""), "rejected", "no-hello");
        Require(!window.Dispatcher.SendAsync(InMemoryBridgeTransport.DefaultConnectionKey, "{}"u8.ToArray()).AsTask().Result,
            "A connection without a hello received a targeted push.");
        window.Dispatcher.BroadcastAsync("""{"kind":"state"}"""u8.ToArray()).AsTask().Wait();
        Require(window.Transport.Messages.Count == 0, "A broadcast reached a window whose connections completed no hello.");
        Require(Parse(window.Transport.Request(Hello)).GetProperty("ok").GetBoolean(), "The hello after a rejected request failed.");
        window.Dispatcher.BroadcastAsync("""{"kind":"state"}"""u8.ToArray()).AsTask().Wait();
        Require(window.Transport.Messages.Count == 1, "A broadcast after the hello was not sent.");
    }

    [Test]
    public void AHelloOfAnotherProtocolIsRefusedWithBothVersionsAndLogsOnce()
    {
        var log = new LogCapture();
        using var window = new Window(log: log);
        var refused = window.Transport.Request("""{"kind":"hello","protocol":5,"session":"s-1","client":{"name":"c","version":"5.0.0"}}""");
        var reply = RequireError(refused, "failed", "protocol-mismatch");
        Require(reply.GetProperty("protocol").GetInt32() == 4 && reply.GetProperty("implementation").ValueKind == JsonValueKind.Object,
            $"The refusal does not name the producer: {refused}");
        var message = reply.GetProperty("error").GetProperty("message").GetString()!;
        Require(message.Contains('4') && message.Contains('5'), $"The message does not name both versions: {message}");
        // A refused hello of another shape still names the mismatch rather than the shape.
        RequireError(window.Transport.Request("""{"kind":"hello","protocol":3}"""), "failed", "protocol-mismatch");
        RequireError(window.Transport.Request("""{"kind":"snapshot","route":"notes"}"""), "rejected", "no-hello");
        Require(log.Count("BridgeProtocolMismatch") == 1, "The mismatch was not logged once per connection.");
    }

    [Test]
    public void MalformedHellosAndASecondHelloAreInvalidRequests()
    {
        using var window = new Window();
        foreach (var hello in new[]
        {
            """{"kind":"hello","session":"s-1","client":{"name":"c","version":"1"}}""",
            """{"kind":"hello","protocol":4,"client":{"name":"c","version":"1"}}""",
            """{"kind":"hello","protocol":4,"session":"","client":{"name":"c","version":"1"}}""",
            """{"kind":"hello","protocol":4,"session":"s 1","client":{"name":"c","version":"1"}}""",
            """{"kind":"hello","protocol":4,"session":"s-1"}""",
            """{"kind":"hello","protocol":4,"session":"s-1","client":{"name":"c","version":""}}""",
        })
        {
            var reply = RequireError(window.Transport.Request(hello), "rejected", "invalid-request");
            Require(reply.GetProperty("protocol").GetInt32() == 4, $"{hello}: the reply does not carry the producer's identity.");
        }
        Require(Parse(window.Transport.Request(Hello)).GetProperty("ok").GetBoolean(), "A valid hello failed.");
        RequireError(window.Transport.Request(Hello.Replace("s-1", "s-2", StringComparison.Ordinal)), "rejected", "invalid-request");
    }

    [Test]
    public void ASessionOfAClosedConnectionIsReleasedAndAnOpenOneCannotBeShared()
    {
        using var window = new Window();
        Require(Parse(window.Transport.Request(Hello)).GetProperty("ok").GetBoolean(), "The hello failed.");
        RequireError(window.Transport.Request(Hello, "connection-2"), "rejected", "invalid-request");
        var closed = new List<string>();
        using var observer = window.Dispatcher.OnConnectionClosed(closed.Add);
        window.Transport.CloseConnection();
        Require(closed.SequenceEqual([InMemoryBridgeTransport.DefaultConnectionKey]), "The closed connection was not reported.");
        RequireError(window.Transport.Request(Hello, "connection-3"), "released", null);
        // A connection key can be reused by a new connection, as CS-WebUI reuses its slots: it starts without a hello.
        RequireError(window.Transport.Request("""{"kind":"snapshot","route":"notes"}"""), "rejected", "no-hello");
        Require(Parse(window.Transport.Request(Hello.Replace("s-1", "s-4", StringComparison.Ordinal))).GetProperty("ok").GetBoolean(),
            "A new session on a reused connection key failed.");
    }

    [Test]
    public void EnvelopesAreCheckedAfterTheHello()
    {
        using var window = new Window(maximumInputBytes: 1024);
        window.Transport.Request(Hello);
        RequireError(window.Transport.Request("""{"kind":"teleport","route":"notes"}"""), "rejected", "invalid-request");
        RequireError(window.Transport.Request("""{"route":"notes"}"""), "rejected", "invalid-request");
        RequireError(window.Transport.Request("""[{"kind":"snapshot","route":"notes"}]"""), "rejected", "invalid-request");
        RequireError(window.Transport.Request("{\"kind\":\"snapshot\",\"route\":\"notes\""), "rejected", "invalid-request");
        RequireError(window.Transport.Request("""{"kind":"snapshot","route":"my notes"}"""), "rejected", "invalid-request");
        RequireError(window.Transport.Request("""{"kind":"set","route":"notes","member":"title","value":"a","value":"b","edit":1}"""),
            "rejected", "invalid-request");
        RequireError(window.Transport.Request($$"""{"kind":"set","route":"notes","member":"title","value":"{{new string('x', 1024)}}","edit":1}"""),
            "rejected", "invalid-input");
        // A route nothing serves is released, whatever the request.
        Require(window.Transport.Request("""{"kind":"snapshot","route":"content0123456789abcdef0123456789abcdef"}""")
            == """{"ok":false,"error":{"kind":"released","message":"This View was released."}}""", "An unserved route was not released.");
        Require(window.Transport.Request("""{"kind":"set","route":"notes","member":"title","value":null,"edit":7}""")
            == """{"ok":false,"error":{"kind":"released","message":"This View was released."}}""", "An unserved route was not released.");
    }

    [Test]
    public async Task RoutesReceiveCheckedRequestsUntilReleasedAsync()
    {
        using var window = new Window();
        window.Transport.Request(Hello);
        var handler = new RecordingRoute();
        var lease = window.Dispatcher.RegisterRoute("notes", handler);
        Require(Throws<InvalidOperationException>(() => window.Dispatcher.RegisterRoute("notes", handler)), "A route was served twice.");
        Require(window.Transport.Request("""{"kind":"set","route":"notes","member":"title","value":"x","edit":3}""") == """{"ok":true}""",
            "The route's reply was not returned.");
        Require(handler.Requests.SequenceEqual(["set notes title connection-1 client-1"]), $"The route received [{string.Join(", ", handler.Requests)}].");
        // A View route receives only the View request kinds; a reserved route receives every kind.
        RequireError(window.Transport.Request("""{"kind":"menu-bar-watch","route":"notes"}"""), "rejected", "invalid-request");
        using (window.Dispatcher.RegisterRoute("runic.test", handler))
            Require(await window.Transport.RequestAsync("""{"kind":"anything","route":"runic.test"}""") == """{"ok":true}""",
                "A reserved route did not receive its own kind.");
        lease.Dispose();
        RequireError(window.Transport.Request("""{"kind":"snapshot","route":"notes"}"""), "released", null);
        // A failing route answers failed and the dispatcher keeps serving.
        using (window.Dispatcher.RegisterRoute("broken", new ThrowingRoute()))
            RequireError(window.Transport.Request("""{"kind":"snapshot","route":"broken"}"""), "failed", null);
    }

    [Test]
    public async Task ATransportWithTargetedPushesAnswersReceiveAtOnceAsync()
    {
        using var window = new Window();
        window.Transport.Request(Hello);
        Require(await window.Transport.RequestAsync("""{"kind":"receive"}""") == """{"ok":true,"messages":[]}""",
            "A receive on a transport with targeted pushes did not answer at once.");
        Require(await window.Dispatcher.SendAsync(InMemoryBridgeTransport.DefaultConnectionKey, """{"kind":"operation"}"""u8.ToArray()),
            "The targeted push was not sent.");
        Require(window.Transport.TakeMessages().Single() == new RunicBridgeMessage("""{"kind":"operation"}""", InMemoryBridgeTransport.DefaultConnectionKey),
            "The targeted push was not recorded for its connection.");
    }

    [Test]
    public async Task TheMailboxQueuesTargetedPushesForReceiveAsync()
    {
        var time = new FakeTimeProvider();
        using var transport = new MailboxTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { TimeProvider = time });
        var dispatcher = BridgeDispatcher.For(transport);
        Require(dispatcher.UsesMailbox, "A transport without targeted pushes does not use the mailbox.");
        await transport.RequestAsync(Hello, "c1");

        // Queued pushes are answered at once, in order.
        Require(await dispatcher.SendAsync("c1", """{"n":1}"""u8.ToArray()) && await dispatcher.SendAsync("c1", """{"n":2}"""u8.ToArray()),
            "The mailbox refused a push.");
        Require(await transport.RequestAsync("""{"kind":"receive"}""", "c1") == """{"ok":true,"messages":[{"n":1},{"n":2}]}""",
            "The queued pushes were not answered in order.");

        // A pending receive answers when a push is queued; a second pending one answers at once with nothing.
        var pending = transport.RequestAsync("""{"kind":"receive"}""", "c1").AsTask();
        Require(!pending.IsCompleted, "An empty mailbox answered at once.");
        Require(await transport.RequestAsync("""{"kind":"receive"}""", "c1") == """{"ok":true,"messages":[]}""",
            "A second pending receive did not answer at once.");
        await dispatcher.SendAsync("c1", """{"n":3}"""u8.ToArray());
        Require(await pending.WaitAsync(TimeSpan.FromSeconds(5)) == """{"ok":true,"messages":[{"n":3}]}""",
            "The pending receive did not answer with the queued push.");

        // A receive answers with an empty list after 25 seconds.
        pending = transport.RequestAsync("""{"kind":"receive"}""", "c1").AsTask();
        time.Advance(TimeSpan.FromSeconds(24));
        await Task.Delay(20);
        Require(!pending.IsCompleted, "A receive answered before its bound.");
        time.Advance(TimeSpan.FromSeconds(1));
        Require(await pending.WaitAsync(TimeSpan.FromSeconds(5)) == """{"ok":true,"messages":[]}""",
            "A receive did not answer after 25 seconds.");

        // Another connection's pushes stay in its own mailbox; closing a connection ends its pending receive.
        await transport.RequestAsync(Hello.Replace("s-1", "s-2", StringComparison.Ordinal), "c2");
        await dispatcher.SendAsync("c2", """{"n":4}"""u8.ToArray());
        pending = transport.RequestAsync("""{"kind":"receive"}""", "c1").AsTask();
        transport.Close("c1");
        Require(await pending.WaitAsync(TimeSpan.FromSeconds(5)) == """{"ok":true,"messages":[]}""",
            "Closing the connection did not end its pending receive.");
        Require(!await dispatcher.SendAsync("c1", "{}"u8.ToArray()), "A closed connection accepted a push.");
        Require(await transport.RequestAsync("""{"kind":"receive"}""", "c2") == """{"ok":true,"messages":[{"n":4}]}""",
            "The other connection's push was lost.");
    }

    [Test]
    public async Task AFullMailboxDropsItsOldestPushesAsync()
    {
        var log = new LogCapture();
        using var transport = new MailboxTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { LoggerFactory = log.Factory });
        var dispatcher = BridgeDispatcher.For(transport);
        await transport.RequestAsync(Hello, "c1");
        for (var index = 0; index < BridgeDispatcher.MaximumMailboxMessages + 2; index++)
            await dispatcher.SendAsync("c1", Encoding.UTF8.GetBytes($$"""{"n":{{index}}}"""));
        var messages = Parse(await transport.RequestAsync("""{"kind":"receive"}""", "c1")).GetProperty("messages");
        Require(messages.GetArrayLength() == BridgeDispatcher.MaximumMailboxMessages && messages[0].GetProperty("n").GetInt32() == 2,
            $"The mailbox kept {messages.GetArrayLength()} pushes from {messages[0]}.");
        Require(log.Count("BridgeMailboxOverflowed") == 1, "The overflow was not logged once.");
    }

    // The window session releases a connection's mounts when the transport reports it closed.
    [Test]
    public void TheWindowReleasesAClosedConnection()
    {
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport);
        transport.Request(Hello, "c1");
        var released = new List<string>();
        using var observer = BridgeDispatcher.For(transport).OnConnectionClosed(released.Add);
        transport.CloseConnection("c1");
        transport.CloseConnection("c1");
        Require(released.SequenceEqual(["c1"]), $"The close was reported as [{string.Join(", ", released)}].");
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement RequireError(string json, string kind, string? reason)
    {
        var reply = Parse(json);
        Require(!reply.GetProperty("ok").GetBoolean(), $"Expected {kind}/{reason}, got {json}.");
        var error = reply.GetProperty("error");
        Require(error.GetProperty("kind").GetString() == kind
            && (reason is null ? !error.TryGetProperty("reason", out _) : error.GetProperty("reason").GetString() == reason)
            && error.GetProperty("message").GetString() is { Length: > 0 },
            $"Expected {kind}/{reason}, got {json}.");
        return reply;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Window : IDisposable
    {
        public Window(int? maximumInputBytes = null, LogCapture? log = null)
        {
            Transport = new InMemoryBridgeTransport();
            Session = new WindowContentSession(Transport, new WindowContentSessionOptions
            {
                MaximumInputBytes = maximumInputBytes,
                LoggerFactory = log?.Factory,
            });
            Dispatcher = BridgeDispatcher.For(Transport);
        }

        public InMemoryBridgeTransport Transport { get; }
        public WindowContentSession Session { get; }
        public BridgeDispatcher Dispatcher { get; }

        public void Dispose()
        {
            Session.Dispose();
            Transport.Dispose();
        }
    }

    private sealed class RecordingRoute : IBridgeRouteHandler
    {
        public List<string> Requests { get; } = [];

        public ValueTask<ReadOnlyMemory<byte>> HandleAsync(BridgeRouteRequest request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Kind} {request.Route} {request.Member} {request.Connection.Key} {request.Connection.ClientKey}");
            return ValueTask.FromResult(BridgeMessages.Ok);
        }
    }

    private sealed class ThrowingRoute : IBridgeRouteHandler
    {
        public ValueTask<ReadOnlyMemory<byte>> HandleAsync(BridgeRouteRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The route failed.");
    }

    private sealed class LogCapture
    {
        private readonly OperationRetirementTests.LogCapture _capture = new();
        public Microsoft.Extensions.Logging.ILoggerFactory Factory => _capture;
        // The number of entries of the event: 1018 BridgeProtocolMismatch, 1023 BridgeMailboxOverflowed.
        public int Count(string eventName) => _capture.All(eventName switch
        {
            "BridgeProtocolMismatch" => 1018,
            "BridgeMailboxOverflowed" => 1023,
            _ => throw new ArgumentOutOfRangeException(nameof(eventName)),
        }).Count();
    }

    // A transport without targeted pushes, as CS-WebUI's.
    private sealed class MailboxTransport : IBridgeTransport, IDisposable
    {
        private IBridgeRequestHandler? _handler;

        public IDisposable Serve(IBridgeRequestHandler handler)
        {
            _handler = handler;
            return this;
        }

        public ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public async ValueTask<string> RequestAsync(string json, string connection) =>
            Encoding.UTF8.GetString((await _handler!.HandleAsync(
                new BridgeRequest(Encoding.UTF8.GetBytes(json), "client", connection), CancellationToken.None)).Span);

        public void Close(string connection) => _handler!.ConnectionClosed(connection);

        public void Dispose() { }
    }
}
