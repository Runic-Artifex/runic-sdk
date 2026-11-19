using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Views protocol 4 interactions of a window (specs/application/README.md, "Interactions"): registration with open and
// interactions, the interaction and interaction-cancelled pushes, and interaction-reply.
public sealed class InteractionProtocolTests
{
    private const string Route = "notes";
    private const string Contract = "testing.confirm.v1";

    [Test]
    public void RegistrationAnswersUnsupportedOkStaleAndIgnored()
    {
        using var window = new InteractionWindow();
        window.Open("p-1");
        Require(window.Register("p-1", 1, ("unknown", "none")) == "unsupported", "An unknown handler was not unsupported.");
        Require(window.Register("p-1", 1, ("confirm", "other.v1")) == "unsupported", "Another contract was not unsupported.");
        Require(window.Register("p-1", 2, ("confirm", Contract)) == "ok", "A registration was not ok.");
        Require(window.Register("p-1", 2, ("confirm", Contract)) == "ok", "An exact repeat was not ok.");
        Require(window.Register("p-1", 2) == "stale", "The same generation with other handlers was not stale.");
        Require(window.Register("p-1", 1) == "stale", "A lower generation was not stale.");
        Require(window.Register("p-2", 3, ("confirm", Contract)) == "ignored", "A presentation that is not open was not ignored.");
        Require(window.Register("p-1", 3, connection: "connection-2") == "ignored",
            "Another connection's registration of the presentation was not ignored.");
        Require(window.Register("p-1", 3) == "ok", "A higher generation did not replace the registration.");

        // open's handlers are generation 0.
        window.Open("p-3");
        Require(window.Session.Interactions.RegisterOpenHandlers(Route, "p-3", window.Connection(),
            JsonDocument.Parse($$"""[{"name":"confirm","contract":"{{Contract}}"}]""").RootElement) == "ok",
            "The handlers of an open were not registered.");
        Require(window.Register("p-3", 0) == "stale", "Generation 0 with other handlers after the open was not stale.");
    }

    [Test]
    public async Task AnInteractionIsPushedAndAnsweredOnceAsync()
    {
        using var window = new InteractionWindow();
        window.Open("p-1");
        window.Register("p-1", 1, ("confirm", Contract));
        var answer = window.Ask("Clear all rows?");
        var pushes = await window.PushesAsync();
        Require(pushes.Count == 1, $"The interaction was not pushed once: {Show(pushes)}");
        var push = pushes[0];
        Require(push["kind"]!.GetValue<string>() == "interaction" && push["route"]!.GetValue<string>() == Route
            && push["presentation"]!.GetValue<string>() == "p-1" && push["name"]!.GetValue<string>() == "confirm"
            && push["contract"]!.GetValue<string>() == Contract && push["ownerEpoch"]!.GetValue<long>() >= 1
            && push["expiresAt"]!.GetValue<string>() == "2026-10-28T12:02:00Z"
            && push["input"]!.GetValue<string>() == "Clear all rows?",
            $"The interaction push is not the request: {push}");

        Require(window.Reply(push, """{"outcome":"answered","output":{"accepted":true}}""", "connection-2") == "stale",
            "Another connection answered the interaction.");
        Require(window.Reply(push, """{"outcome":"answered","output":{"accepted":true}}""") == "ok", "The answer was not ok.");
        Require(await answer.WaitScaledAsync(TimeSpan.FromSeconds(5)), "The waiting interaction did not receive the answer.");
        Require(window.Reply(push, """{"outcome":"answered","output":{ "accepted" : true }}""") == "already-completed",
            "A canonically equal repeat was not already-completed.");
        Require(window.Reply(push, """{"outcome":"answered","output":{"accepted":false}}""") == "stale",
            "A different repeat was not stale.");
    }

    [Test]
    public async Task AnInteractionThatTimesOutIsCancelledWithAMessageAsync()
    {
        using var window = new InteractionWindow();
        window.Open("p-1");
        window.Register("p-1", 1, ("confirm", Contract));
        var answer = window.Ask("Clear?");
        var push = (await window.PushesAsync())[0];
        window.Clock.Advance(TimeSpan.FromMinutes(2));
        var cancelled = await window.PushesAsync();
        Require(cancelled.Count == 1 && cancelled[0]["kind"]!.GetValue<string>() == "interaction-cancelled"
            && cancelled[0]["requestId"]!.GetValue<string>() == push["requestId"]!.GetValue<string>()
            && cancelled[0]["ownerEpoch"]!.GetValue<long>() == push["ownerEpoch"]!.GetValue<long>()
            && cancelled[0]["presentation"]!.GetValue<string>() == "p-1"
            && cancelled[0]["message"]!.GetValue<string>().Length > 0 && cancelled[0]["reason"] is null,
            $"The timed-out interaction was not cancelled with a message: {Show(cancelled)}");
        Require(await ThrowsAsync<RunicInteractionTimeoutException>(answer), "The waiting interaction did not time out.");
        Require(window.Reply(push, """{"outcome":"answered","output":{"accepted":true}}""") == "stale",
            "An answer after the cancellation was not stale.");
    }

    // sdk#1146: a longer timeout than the default, up to a day.
    [Test]
    public async Task ALongerTimeoutEndsTheInteractionAtItsDeadlineAsync()
    {
        using var window = new InteractionWindow(openEnded: true);
        window.Open("p-1");
        window.Register("p-1", 1, ("confirm", Contract));
        var answer = window.Ask("Clear?", TimeSpan.FromMinutes(15));
        var push = (await window.PushesAsync())[0];
        Require(push["expiresAt"]!.GetValue<string>() == "2026-10-28T12:15:00Z", $"The deadline is not fifteen minutes away: {push}");
        window.Clock.Advance(TimeSpan.FromMinutes(10));
        Require(!answer.IsCompleted && window.Transport.TakeMessages().Count == 0, "The interaction ended before its deadline.");
        window.Clock.Advance(TimeSpan.FromMinutes(5));
        Require(await ThrowsAsync<RunicInteractionTimeoutException>(answer), "The interaction did not time out at its deadline.");
    }

    // sdk#1146: Timeout.InfiniteTimeSpan arms no timer, and a page that uses open-ended-interactions/1 receives no expiresAt.
    [Test]
    public async Task AnInteractionWithoutADeadlineWaitsForItsAnswerAsync()
    {
        using var window = new InteractionWindow(openEnded: true);
        window.Open("p-1");
        window.Register("p-1", 1, ("confirm", Contract));
        var answer = window.Ask("Clear?", Timeout.InfiniteTimeSpan);
        var push = (await window.PushesAsync())[0];
        Require(push["kind"]!.GetValue<string>() == "interaction" && !push.ContainsKey("expiresAt"),
            $"The interaction without a deadline named one: {push}");
        window.Clock.Advance(TimeSpan.FromDays(60));
        Require(!answer.IsCompleted && window.Transport.TakeMessages().Count == 0, "The interaction without a deadline ended.");
        Require(window.Reply(push, """{"outcome":"answered","output":{"accepted":true}}""") == "ok", "The late answer was not ok.");
        Require(await answer.WaitScaledAsync(TimeSpan.FromSeconds(5)), "The late answer did not reach the waiting interaction.");
    }

    // A page without open-ended-interactions/1, such as @runic-artifex/views before 0.23, needs an instant: it receives
    // the latest one a finite timeout reaches, a day later, and the producer still arms no timer.
    [Test]
    public async Task APageWithoutOpenEndedInteractionsReceivesADayAsTheDeadlineAsync()
    {
        using var window = new InteractionWindow();
        window.Open("p-1");
        window.Register("p-1", 1, ("confirm", Contract));
        var answer = window.Ask("Clear?", Timeout.InfiniteTimeSpan);
        var push = (await window.PushesAsync())[0];
        Require(push["expiresAt"]?.GetValue<string>() == "2026-10-29T12:00:00Z", $"The interaction did not name a day as its deadline: {push}");
        window.Clock.Advance(TimeSpan.FromDays(2));
        Require(!answer.IsCompleted, "The producer timed out an interaction without a deadline.");
        // The page ends it: an older page aborts its handler at its own limit and replies cancelled.
        Require(window.Reply(push, """{"outcome":"cancelled"}""") == "ok", "The page's cancellation was not ok.");
        Require(await ThrowsAsync<RunicInteractionCancelledException>(answer), "The page's cancellation did not end the interaction.");
    }

    [Test]
    public void TimeoutsArePositiveAndAtMostADayOrInfinite()
    {
        using var window = new InteractionWindow();
        foreach (var timeout in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(-2), TimeSpan.FromHours(24) + TimeSpan.FromTicks(1) })
        {
            Require(Rejects(() => window.Session.Interactions.Register<string, bool>(Route, "other", Contract, static input => input,
                static _ => true, timeout: timeout)), $"Register accepted {timeout}.");
            Require(Rejects(() => new ViewQuestion<string, bool>(timeout: timeout)), $"ViewQuestion accepted {timeout}.");
            Require(Rejects(() => Runic.Application.Views.ReactiveUI.Generated.ReactiveInteractionDescriptor.Create<string, bool>("other",
                Contract, static _ => new global::ReactiveUI.Binding.Interaction<string, bool>(), static input => input, static _ => true, timeout)),
                $"ReactiveInteractionDescriptor accepted {timeout}.");
        }
        foreach (var timeout in new[] { Timeout.InfiniteTimeSpan, TimeSpan.FromHours(24), TimeSpan.FromTicks(1) })
        {
            window.Session.Interactions.Register<string, bool>(Route, "other", Contract, static input => input, static _ => true, timeout: timeout).Dispose();
            Require(new ViewQuestion<string, bool>(timeout: timeout).Timeout == timeout, $"ViewQuestion did not keep {timeout}.");
            Runic.Application.Views.ReactiveUI.Generated.ReactiveInteractionDescriptor.Create<string, bool>("other", Contract,
                static _ => new global::ReactiveUI.Binding.Interaction<string, bool>(), static input => input, static _ => true, timeout);
        }
        Require(new RunicInteractionTimeoutAttribute(Timeout.Infinite).Timeout == Timeout.InfiniteTimeSpan
            && new RunicInteractionTimeoutAttribute(900_000).Timeout == TimeSpan.FromMinutes(15)
            && TimeSpan.FromMilliseconds(RunicInteractionTimeoutAttribute.MaximumMilliseconds) == TimeSpan.FromHours(24),
            "RunicInteractionTimeoutAttribute does not convert its milliseconds.");

        static bool Rejects(Action action)
        {
            try { action(); }
            catch (ArgumentOutOfRangeException) { return true; }
            return false;
        }
    }

    [Test]
    public async Task AnOutputThatDoesNotDecodeFailsTheInteractionAsync()
    {
        using var window = new InteractionWindow();
        window.Open("p-1");
        window.Register("p-1", 1, ("confirm", Contract));
        var answer = window.Ask("Clear?");
        var push = (await window.PushesAsync())[0];
        Require(window.Reply(push, """{"outcome":"answered","output":"yes"}""") == "invalid-output",
            "An output that does not decode was not invalid-output.");
        Require(await ThrowsAsync<RunicInteractionOutputException>(answer), "The waiting interaction did not fail with an output error.");
    }

    [Test]
    public async Task AClosedConnectionEndsItsInteractionsAsync()
    {
        using var window = new InteractionWindow();
        window.Open("p-1");
        window.Register("p-1", 1, ("confirm", Contract));
        var answer = window.Ask("Clear?");
        await window.PushesAsync();
        window.Transport.CloseConnection();
        Require(await ThrowsAsync<RunicInteractionCancelledException>(answer), "A closed connection did not end its interaction.");
    }

    private static async Task<bool> ThrowsAsync<TException>(Task task) where TException : Exception
    {
        try { await task.WaitScaledAsync(TimeSpan.FromSeconds(5)); }
        catch (TException) { return true; }
        return false;
    }

    private static string Show(IEnumerable<JsonObject> messages) => string.Join("\n", messages.Select(message => message.ToJsonString()));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // A window with the interaction `confirm` (input string, output {accepted: boolean}) on the route `notes`, served on
    // an in-memory transport with a virtual clock; connection-1 and connection-2 completed their hello.
    private sealed class InteractionWindow : IDisposable
    {
        private readonly IDisposable _definition;
        private readonly IDisposable _route;

        // openEnded: connection-1 lists open-ended-interactions/1 in its hello.
        public InteractionWindow(bool openEnded = false)
        {
            Session = new WindowContentSession(Transport, new WindowContentSessionOptions { TimeProvider = Clock, IncludeFailureDetail = false });
            _definition = Session.Interactions.Register<string, bool>(Route, "confirm", Contract,
                static input => JsonSerializer.Serialize(input), static output => output.GetProperty("accepted").GetBoolean());
            _route = BridgeDispatcher.For(Transport).RegisterRoute(Route, new Handler(Session));
            Hello("connection-1", "client-1", openEnded);
            Hello("connection-2", "client-2");
        }

        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
        public InMemoryBridgeTransport Transport { get; } = new();
        public WindowContentSession Session { get; }

        public BridgeConnection Connection(string key = "connection-1") => BridgeDispatcher.For(Transport).FindConnection(Key(key))!;

        // The window's key of the connection the transport reports with this key.
        public string Key(string transportKey) => BridgeDispatcher.For(Transport).ConnectionKeyOf(transportKey)!;

        // As an open of the presentation on connection-1 does.
        public void Open(string presentation) =>
            Session.Interactions.OnPresentationMounted(Route, presentation, "client-1", Key("connection-1"),
                Connection().UsesOpenEndedInteractions);

        public string Register(string presentation, long generation, params (string Name, string Contract)[] handlers) =>
            Register(presentation, generation, "connection-1", handlers);

        public string Register(string presentation, long generation, string connection, params (string Name, string Contract)[] handlers)
        {
            var message = new JsonObject
            {
                ["kind"] = "interactions",
                ["route"] = Route,
                ["presentation"] = presentation,
                ["generation"] = generation,
                ["handlers"] = new JsonArray([.. handlers.Select(handler =>
                    (JsonNode)new JsonObject { ["name"] = handler.Name, ["contract"] = handler.Contract })]),
            };
            return Request(message, connection)["registration"]!.GetValue<string>();
        }

        // Asks the interaction as a command invoked on connection-1 does.
        public Task<bool> Ask(string question, TimeSpan? timeout = null)
        {
            using (RunicInteractionInvocation.Enter(Session, Route, "client-1", Key("connection-1")))
                return Session.Interactions.TryRequest<string, bool>(Route, "confirm", Contract, question,
                    static input => JsonSerializer.Serialize(input), static output => output.GetProperty("accepted").GetBoolean(), timeout)
                    ?? throw new InvalidOperationException("The interaction did not reach the presentation.");
        }

        // An interaction-reply to a pushed request, with the outcome and output members of `answer`.
        public string Reply(JsonObject push, string answer, string connection = "connection-1")
        {
            var message = new JsonObject
            {
                ["kind"] = "interaction-reply",
                ["route"] = Route,
                ["presentation"] = push["presentation"]!.GetValue<string>(),
                ["requestId"] = push["requestId"]!.GetValue<string>(),
                ["ownerEpoch"] = push["ownerEpoch"]!.GetValue<long>(),
                ["name"] = push["name"]!.GetValue<string>(),
                ["contract"] = push["contract"]!.GetValue<string>(),
            };
            foreach (var (name, value) in JsonNode.Parse(answer)!.AsObject()) message[name] = value?.DeepClone();
            return Request(message, connection)["result"]!.GetValue<string>();
        }

        public async Task<List<JsonObject>> PushesAsync(string connection = "connection-1")
        {
            await BridgeTargetedPushes.For(Transport).WhenSent().WaitScaledAsync(TimeSpan.FromSeconds(5));
            return [.. Transport.TakeMessages().Where(message => message.ConnectionKey == connection)
                .Select(message => JsonNode.Parse(message.Json)!.AsObject())];
        }

        private void Hello(string connection, string client, bool openEnded = false)
        {
            var hello = new JsonObject
            {
                ["kind"] = "hello",
                ["protocol"] = 4,
                ["session"] = $"s-{connection}",
                ["client"] = new JsonObject { ["name"] = "t", ["version"] = "1" },
            };
            if (openEnded) hello["features"] = new JsonArray("open-ended-interactions/1");
            Request(hello, connection, client);
        }

        private JsonObject Request(JsonObject message, string connection, string? client = null) =>
            JsonNode.Parse(Transport.Request(message.ToJsonString(), connection,
                client ?? (connection == "connection-1" ? "client-1" : "client-2")))!.AsObject();

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
}
