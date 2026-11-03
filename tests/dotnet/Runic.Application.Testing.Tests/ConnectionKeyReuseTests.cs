using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// A transport may reuse a connection key: CS-WebUI's key is "clientId:connectionId", WebUI reuses connection slots, and
// without its cookie every client id is 0. WebUI frees a slot when the socket closes but reports the close later, so a new
// page can send requests on the key while the old connection is still open. The dispatcher gives every connection a key
// of its own ("{transportKey}#{n}", never handed out twice), and with request-session/1 a request acts for a connection
// only when it names the session of the connection's hello: a new page's requests, its mailbox receive included, never
// touch the old connection, and its hello replaces the old connection.
internal sealed class ConnectionKeyReuseTests
{
    private const string Route = "reuse";
    private const string Contract = "testing.reuse.confirm";
    // A CS-WebUI key without the cookie: client 0, connection slot 1.
    private const string Slot = "0:1";

    [Test]
    public async Task ACommandResumedAfterItsConnectionClosedNeverReachesTheConnectionThatReusedItsKeyAsync()
    {
        using var transport = new InMemoryBridgeTransport();
        using var window = new ReuseWindow(transport, transport.RequestAsync);
        await window.HelloAsync("s-alice");
        var alice = window.Open()!;
        Require(alice.Key == Slot + "#1" && alice.TransportKey == Slot, $"The connection's key is not its own: {alice.Key}");
        await window.MountAsync("alice-page", alice, "s-alice");

        // Alice's command awaits across her disconnect, then asks the page.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = Task.Run(async () =>
        {
            using (RunicInteractionInvocation.Enter(window.Session, Route, alice.ClientKey, alice.Key))
            {
                await gate.Task;
                return window.Ask("Delete everything?");
            }
        });

        transport.CloseConnection(Slot);
        // Another browser gets the same WebUI slot and client id 0.
        await window.HelloAsync("s-bob");
        var bob = window.Open()!;
        Require(bob.TransportKey == Slot && bob.Key == Slot + "#2", $"The reused slot did not get a new connection key: {bob.Key}");
        await window.MountAsync("bob-page", bob, "s-bob");

        gate.SetResult();
        Require(await resumed is null, "Alice's resumed command asked Bob's page.");
        Require(!await window.Dispatcher.SendAsync(alice.Key, "{\"kind\":\"probe\"}"u8.ToArray()),
            "A push for the closed connection was accepted.");
        await BridgeTargetedPushes.For(transport).WhenSent().WaitScaledAsync(TimeSpan.FromSeconds(5));
        Require(transport.TakeMessages().All(message => message.ConnectionKey != Slot),
            "Bob's page received a push meant for Alice's connection.");

        // Bob's own command still reaches Bob's page, through the transport's key.
        using (RunicInteractionInvocation.Enter(window.Session, Route, bob.ClientKey, bob.Key))
            Require(window.Ask("Bob's question") is not null, "Bob's own command did not reach his page.");
        await BridgeTargetedPushes.For(transport).WhenSent().WaitScaledAsync(TimeSpan.FromSeconds(5));
        Require(transport.TakeMessages().Any(message => message.ConnectionKey == Slot && message.Json.Contains("\"interaction\"", StringComparison.Ordinal)),
            "Bob's interaction was not sent with the transport's key.");
    }

    // The new page's requests arrive before the transport reports the old connection closed.
    [Test]
    public async Task ANewPageOnAKeyWhoseCloseIsNotReportedYetReplacesTheOldConnectionAsync()
    {
        using var transport = new InMemoryBridgeTransport();
        using var window = new ReuseWindow(transport, transport.RequestAsync);
        await window.HelloAsync("s-alice");
        var alice = window.Open()!;
        await window.MountAsync("alice-page", alice, "s-alice");
        Task<bool> question;
        using (RunicInteractionInvocation.Enter(window.Session, Route, alice.ClientKey, alice.Key))
            question = window.Ask("Alice's question") ?? throw new InvalidOperationException("Alice's question reached no page.");
        await BridgeTargetedPushes.For(transport).WhenSent().WaitScaledAsync(TimeSpan.FromSeconds(5));
        transport.TakeMessages();

        // Bob's socket took Alice's slot; WebUI has not reported Alice's close. His requests carry his session or none.
        RequireNoHello(await window.RequestAsync(new JsonObject { ["kind"] = "snapshot", ["route"] = Route }), "a request without session");
        RequireNoHello(await window.RequestAsync(new JsonObject { ["kind"] = "snapshot", ["route"] = Route, ["session"] = "s-bob" }),
            "a request with another session");
        Require(window.Dispatcher.FindConnection(alice.Key) is { IsOpen: true } && !question.IsCompleted,
            "A request of another session touched Alice's connection.");
        await window.HelloAsync("s-bob");
        var bob = window.Open()!;
        Require(bob.Key != alice.Key && !alice.IsOpen && window.Dispatcher.FindConnection(alice.Key) is null,
            "Bob's hello did not replace Alice's connection.");
        Require(await ThrowsAsync<RunicInteractionCancelledException>(question), "Alice's question did not end with her connection.");

        // WebUI's late report of Alice's close does not close Bob's connection; Bob's own close does.
        transport.CloseConnection(Slot);
        Require(window.Dispatcher.FindConnection(bob.Key) is { IsOpen: true }, "Alice's late close closed Bob's connection.");
        Require(Ok(await window.RequestAsync(new JsonObject { ["kind"] = "receive", ["session"] = "s-bob" })),
            "Bob's request did not run after Alice's late close.");
        await BridgeTargetedPushes.For(transport).WhenSent().WaitScaledAsync(TimeSpan.FromSeconds(5));
        Require(transport.TakeMessages().All(message => !message.Json.Contains("Alice", StringComparison.Ordinal)),
            "Bob received something of Alice's.");
        transport.CloseConnection(Slot);
        Require(window.Dispatcher.FindConnection(bob.Key) is null, "Bob's own close was ignored.");
    }

    // CS-WebUI's script sends receive as soon as its socket opens, before the page's hello.
    [Test]
    public async Task AReceiveOfANewPageNeverDrainsTheOldConnectionsMailboxAsync()
    {
        using var transport = new MailboxTransport();
        using var window = new ReuseWindow(transport, (json, key, client, token) => transport.RequestAsync(json, key));
        Require(window.Dispatcher.UsesMailbox, "The transport has targeted pushes.");
        await window.HelloAsync("s-alice");
        var alice = window.Open()!;
        await window.MountAsync("alice-page", alice, "s-alice");
        using (RunicInteractionInvocation.Enter(window.Session, Route, alice.ClientKey, alice.Key))
            _ = window.Ask("Alice's secret question");
        await BridgeTargetedPushes.For(transport).WhenSent().WaitScaledAsync(TimeSpan.FromSeconds(5));
        Require(alice.Mailbox.Count == 1, "Alice's question was not queued in her mailbox.");

        // Bob's receive, before his hello and with his own session, is refused and leaves Alice's mailbox alone.
        RequireNoHello(await window.RequestAsync(new JsonObject { ["kind"] = "receive" }), "a receive without session");
        RequireNoHello(await window.RequestAsync(new JsonObject { ["kind"] = "receive", ["session"] = "s-bob" }),
            "a receive with another session");
        Require(alice.Mailbox.Count == 1, "Another page's receive drained Alice's mailbox.");

        // Bob's hello replaces Alice's connection, whose mailbox goes with it; Bob's receive has nothing of hers.
        await window.HelloAsync("s-bob");
        Require(alice.Mailbox.Count == 0 && !alice.IsOpen, "Alice's mailbox outlived her replaced connection.");
        var received = JsonNode.Parse(await window.RequestAsync(new JsonObject { ["kind"] = "receive", ["session"] = "s-bob" }))!;
        Require(received["messages"]!.AsArray().Count == 0, $"Bob's receive returned Alice's pushes: {received}");
    }

    [Test]
    public async Task EveryConnectionOfAReusingTransportGetsAKeyNoOtherConnectionHadAsync()
    {
        using var transport = new InMemoryBridgeTransport();
        using var window = new ReuseWindow(transport, transport.RequestAsync);
        var keys = new List<string>();
        foreach (var (transportKey, session) in new[] { ("a", "s-1"), ("a#2", "s-2"), ("a#2#2", "s-3") })
        {
            await window.HelloAsync(session, transportKey);
            keys.Add(window.Dispatcher.ConnectionKeyOf(transportKey)!);
        }
        foreach (var session in new[] { "s-4", "s-5", "s-6" })
        {
            transport.CloseConnection("a");
            await window.HelloAsync(session, "a");
            keys.Add(window.Dispatcher.ConnectionKeyOf("a")!);
        }
        Require(keys.Distinct(StringComparer.Ordinal).Count() == keys.Count && keys.All(key => key.Contains('#')),
            $"A key was handed out twice or bare: {string.Join(", ", keys)}");
    }

    private static void RequireNoHello(string reply, string what)
    {
        var error = JsonNode.Parse(reply)!["error"];
        Require(error?["reason"]?.GetValue<string>() == "no-hello", $"{what} on Alice's connection was answered {reply}.");
    }

    private static bool Ok(string reply) => JsonNode.Parse(reply)!["ok"]!.GetValue<bool>();

    private static async Task<bool> ThrowsAsync<TException>(Task task) where TException : Exception
    {
        try { await task.WaitScaledAsync(TimeSpan.FromSeconds(5)); }
        catch (TException) { return true; }
        return false;
    }

    private delegate ValueTask<string> Send(string json, string transportKey, string? clientKey, CancellationToken cancellationToken);

    private sealed class ReuseWindow : IDisposable
    {
        private readonly Send _send;
        private readonly IDisposable _definition;
        private readonly IDisposable _route;

        public ReuseWindow(IBridgeTransport transport, Send send)
        {
            _send = send;
            Session = new WindowContentSession(transport, new WindowContentSessionOptions { IncludeFailureDetail = false });
            Dispatcher = BridgeDispatcher.For(transport);
            _definition = Session.Interactions.Register<string, bool>(Route, "confirm", Contract,
                static input => JsonSerializer.Serialize(input), static output => output.GetBoolean());
            _route = Dispatcher.RegisterRoute(Route, new Handler(Session));
        }

        public WindowContentSession Session { get; }
        public BridgeDispatcher Dispatcher { get; }

        // The open connection on the slot.
        public BridgeConnection? Open(string transportKey = Slot) =>
            Dispatcher.ConnectionKeyOf(transportKey) is { } key ? Dispatcher.FindConnection(key) : null;

        public async Task HelloAsync(string session, string transportKey = Slot)
        {
            var reply = await RequestAsync(new JsonObject
            {
                ["kind"] = "hello", ["protocol"] = 4, ["session"] = session,
                ["client"] = new JsonObject { ["name"] = "t", ["version"] = "1" },
                ["features"] = new JsonArray("request-session/1"),
            }, transportKey);
            Require(Ok(reply), $"The hello of {session} failed: {reply}");
        }

        // As the page's open does: the presentation belongs to the connection's own key. Then it registers its handler.
        public async Task MountAsync(string presentation, BridgeConnection connection, string session)
        {
            Session.Interactions.OnPresentationMounted(Route, presentation, connection.ClientKey, connection.Key);
            var reply = JsonNode.Parse(await RequestAsync(new JsonObject
            {
                ["kind"] = "interactions", ["route"] = Route, ["presentation"] = presentation, ["generation"] = 1,
                ["handlers"] = new JsonArray(new JsonObject { ["name"] = "confirm", ["contract"] = Contract }),
                ["session"] = session,
            }, connection.TransportKey))!;
            Require(reply["registration"]?.GetValue<string>() == "ok", $"The page did not register its handler: {reply}");
        }

        // The interaction as the current invocation asks it, or null when it reaches no page.
        public Task<bool>? Ask(string question) =>
            Session.Interactions.TryRequest<string, bool>(Route, "confirm", Contract, question,
                static input => JsonSerializer.Serialize(input), static output => output.GetBoolean());

        public async Task<string> RequestAsync(JsonObject message, string transportKey = Slot) =>
            await _send(message.ToJsonString(), transportKey, "0", CancellationToken.None);

        public void Dispose()
        {
            _route.Dispose();
            _definition.Dispose();
            Session.Dispose();
        }

        private sealed class Handler(WindowContentSession session) : IBridgeRouteHandler
        {
            public ValueTask<ReadOnlyMemory<byte>> HandleAsync(BridgeRouteRequest request, CancellationToken cancellationToken) =>
                new(session.Interactions.Handle(request));
        }
    }

    // A transport without targeted pushes, as CS-WebUI's: pushes to one connection wait in its mailbox for receive.
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
                new BridgeRequest(Encoding.UTF8.GetBytes(json), "0", connection), CancellationToken.None)).Span);

        public void Dispose() { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
