using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// A transport may reuse a connection key once its connection closed: CS-WebUI's key is "clientId:connectionId", WebUI
// reuses connection slots, and without its cookie every client id is 0. The dispatcher gives each connection a key that
// is unique for its lifetime, so a command that awaits across the close of its connection never reaches the page that
// got the transport key next: not with an interaction, and not with a targeted push.
internal sealed class ConnectionKeyReuseTests
{
    private const string Route = "reuse";
    private const string Contract = "testing.reuse.confirm";
    // A CS-WebUI key without the cookie: client 0, connection slot 1.
    private const string Slot = "0:1";

    [Test]
    public async Task ACommandResumedAfterItsConnectionClosedNeverReachesTheConnectionThatReusedItsKeyAsync()
    {
        using var window = new ReuseWindow();
        window.Hello(Slot, "s-alice");
        var alice = window.Dispatcher.FindConnection(Slot)!;
        Require(alice.Key == Slot && alice.TransportKey == Slot, $"The first connection of a key did not keep it: {alice.Key}");
        window.Mount("alice-page", alice);

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

        window.Transport.CloseConnection(Slot);
        // Another browser gets the same WebUI slot and client id 0.
        window.Hello(Slot, "s-bob");
        Require(window.Dispatcher.FindConnection(Slot) is null, "The closed connection's key found the connection that reused its slot.");
        var bob = window.Dispatcher.FindConnection(Slot + "#2")!;
        Require(bob is { TransportKey: Slot } && bob.Key != alice.Key, $"The reused slot did not get a new connection key: {bob?.Key}");
        window.Mount("bob-page", bob);

        gate.SetResult();
        Require(await resumed is null, "Alice's resumed command asked Bob's page.");
        Require(!await window.Dispatcher.SendAsync(alice.Key, "{\"kind\":\"probe\"}"u8.ToArray()),
            "A push for the closed connection was accepted.");
        await BridgeTargetedPushes.For(window.Transport).WhenSent().WaitScaledAsync(TimeSpan.FromSeconds(5));
        Require(window.Transport.TakeMessages().All(message => message.ConnectionKey != Slot),
            "Bob's page received a push meant for Alice's connection.");

        // Bob's own command still reaches Bob's page, through the transport's key.
        using (RunicInteractionInvocation.Enter(window.Session, Route, bob.ClientKey, bob.Key))
            Require(window.Ask("Bob's question") is not null, "Bob's own command did not reach his page.");
        await BridgeTargetedPushes.For(window.Transport).WhenSent().WaitScaledAsync(TimeSpan.FromSeconds(5));
        Require(window.Transport.TakeMessages().Any(message => message.ConnectionKey == Slot && message.Json.Contains("\"interaction\"", StringComparison.Ordinal)),
            "Bob's interaction was not sent with the transport's key.");
    }

    [Test]
    public void AGeneratedKeyNeverNamesAnotherConnectionAsync()
    {
        using var window = new ReuseWindow();
        window.Hello("a", "s-1");
        // A transport whose own keys look like generated ones.
        window.Hello("a#2", "s-2");
        window.Transport.CloseConnection("a");
        window.Hello("a", "s-3");
        var reused = window.Dispatcher.FindConnection("a#3");
        Require(reused is { TransportKey: "a" } && window.Dispatcher.FindConnection("a#2") is { TransportKey: "a#2" },
            "A generated key collided with a transport's own key.");
        window.Transport.CloseConnection("a");
        window.Hello("a", "s-4");
        Require(window.Dispatcher.FindConnection("a#4") is { TransportKey: "a" }, "A third use of a key did not get a new key.");
    }

    private sealed class ReuseWindow : IDisposable
    {
        private readonly IDisposable _definition;
        private readonly IDisposable _route;

        public ReuseWindow()
        {
            Session = new WindowContentSession(Transport, new WindowContentSessionOptions { IncludeFailureDetail = false });
            _definition = Session.Interactions.Register<string, bool>(Route, "confirm", Contract,
                static input => JsonSerializer.Serialize(input), static output => output.GetBoolean());
            _route = Dispatcher.RegisterRoute(Route, new Handler(Session));
        }

        public InMemoryBridgeTransport Transport { get; } = new();
        public WindowContentSession Session { get; }
        public BridgeDispatcher Dispatcher => BridgeDispatcher.For(Transport);

        public void Hello(string transportKey, string session) =>
            Request(new JsonObject
            {
                ["kind"] = "hello", ["protocol"] = 4, ["session"] = session,
                ["client"] = new JsonObject { ["name"] = "t", ["version"] = "1" },
            }, transportKey);

        // As the page's open does: the presentation belongs to the dispatcher's connection key. Then it registers its handler.
        public void Mount(string presentation, BridgeConnection connection)
        {
            Session.Interactions.OnPresentationMounted(Route, presentation, connection.ClientKey, connection.Key);
            var reply = Request(new JsonObject
            {
                ["kind"] = "interactions", ["route"] = Route, ["presentation"] = presentation, ["generation"] = 1,
                ["handlers"] = new JsonArray(new JsonObject { ["name"] = "confirm", ["contract"] = Contract }),
            }, connection.TransportKey);
            Require(reply["registration"]?.GetValue<string>() == "ok", $"The page did not register its handler: {reply}");
        }

        // The interaction as the current invocation asks it, or null when it reaches no page.
        public Task<bool>? Ask(string question) =>
            Session.Interactions.TryRequest<string, bool>(Route, "confirm", Contract, question,
                static input => JsonSerializer.Serialize(input), static output => output.GetBoolean());

        private JsonObject Request(JsonObject message, string transportKey) =>
            JsonNode.Parse(Transport.Request(message.ToJsonString(), transportKey, "0"))!.AsObject();

        public void Dispose()
        {
            _route.Dispose();
            _definition.Dispose();
            Session.Dispose();
            Transport.Dispose();
        }

        private sealed class Handler(WindowContentSession session) : IBridgeRouteHandler
        {
            public ValueTask<ReadOnlyMemory<byte>> HandleAsync(BridgeRouteRequest request, CancellationToken cancellationToken) =>
                new(session.Interactions.Handle(request));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
