using System.Text;
using System.Text.Json.Nodes;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests.Conformance;

// The runner's own rules (specs/application/conformance/README.md, "Comparing messages" and "Steps"), which the P3
// scenarios do not reach yet: revisions by order, frame changes per field, ids, generators and held delivery.
public sealed class ScenarioRunnerTests
{
    private static readonly Dictionary<string, ScenarioId> Ids = new(StringComparer.Ordinal)
    {
        ["session"] = new("session", "wire", "s-1"),
        ["contract"] = new("contract", "provided", null),
    };

    [Test]
    public void RevisionsAreComparedByOrder()
    {
        var messages = new ScenarioMessages(Ids);
        messages.Match(Parse("""{"baseRevision":0,"revision":1}"""), Parse("""{"baseRevision":10,"revision":12}"""));
        messages.Match(Parse("""{"revision":1}"""), Parse("""{"revision":12}"""));
        messages.Match(Parse("""{"revision":5}"""), Parse("""{"revision":13}"""));
        Require(Fails(() => messages.Match(Parse("""{"revision":1}"""), Parse("""{"revision":13}"""))), "A revision mapped twice.");
        Require(Fails(() => messages.Match(Parse("""{"revision":3}"""), Parse("""{"revision":14}"""))), "A revision out of order matched.");
        Require(Fails(() => messages.Match(Parse("""{"count":1}"""), Parse("""{"count":12}"""))), "A number that is not a revision was mapped.");
    }

    [Test]
    public void FrameChangesAreComparedPerField()
    {
        var messages = new ScenarioMessages(Ids);
        var expected = Parse("""{"kind":"frame","changes":[{"field":"a","n":1},{"field":"b","n":1},{"field":"a","n":2}]}""");
        messages.Match(expected, Parse("""{"kind":"frame","changes":[{"field":"b","n":1},{"field":"a","n":1},{"field":"a","n":2}]}"""));
        Require(Fails(() => messages.Match(expected, Parse("""{"kind":"frame","changes":[{"field":"a","n":2},{"field":"b","n":1},{"field":"a","n":1}]}"""))),
            "Changes of one field matched out of order.");
        Require(Fails(() => messages.Match(expected, Parse("""{"kind":"frame","changes":[{"field":"a","n":1},{"field":"a","n":2}]}"""))),
            "A missing field matched.");
        // Outside a frame, an array named changes keeps its order.
        Require(Fails(() => messages.Match(Parse("""{"changes":[1,2]}"""), Parse("""{"changes":[2,1]}"""))), "Another array matched out of order.");
    }

    [Test]
    public void MatchersAndIdsWriteAndMatch()
    {
        var messages = new ScenarioMessages(Ids);
        var written = Encoding.UTF8.GetString(messages.Write(Parse(
            """{"session":{"$id":"session"},"name":{"$any":"string","default":"c"},"input":{"$json":"{\"limit\":2.0}"}}""")));
        Require(written == """{"session":"s-1","name":"c","input":{"limit":2.0}}""", $"The runner wrote {written}.");
        Require(Fails(() => messages.Write(Parse("""{"contract":{"$id":"contract"}}"""))), "An unprovided id was written.");
        Require(Fails(() => messages.Write(Parse("""{"x":{"$id":"undeclared"}}"""))), "An undeclared id was written.");

        messages.Match(Parse("""{"name":{"$any":"string"},"n":1}"""), Parse("""{"name":"producer","n":1.0}"""));
        Require(Fails(() => messages.Match(Parse("""{"name":{"$any":"string"}}"""), Parse("""{"name":1}"""))), "$any matched another type.");
        Require(Fails(() => messages.Match(Parse("""{"a":1}"""), Parse("""{"a":1,"b":2}"""))), "An extra member matched.");
        Require(Fails(() => messages.Match(Parse("""{"s":{"$id":"session"}}"""), Parse("""{"s":"s-2"}"""))), "Another wire id matched.");
        // A provided id binds the first time and must match after that; then the runner writes it.
        messages.Match(Parse("""{"c":{"$id":"contract"}}"""), Parse("""{"c":"Tests.Notes:fp-1"}"""));
        Require(Fails(() => messages.Match(Parse("""{"c":{"$id":"contract"}}"""), Parse("""{"c":"other"}"""))), "A provided id changed.");
        Require(Encoding.UTF8.GetString(messages.Write(Parse("""{"c":{"$id":"contract"}}"""))) == """{"c":"Tests.Notes:fp-1"}""",
            "The bound provided id was not written.");
    }

    [Test]
    public void GeneratorsExpandToRowsAndAdds()
    {
        var expanded = ConformanceCorpus.Expand(Parse("""{"rows":[{"$rows":[1,2,8]}],"changes":[{"$adds":[3,1]}]}"""))!.ToJsonString();
        const string Expected = """
            {"rows":[{"id":1,"label":"row 1..."},{"id":2,"label":"row 2..."}],"changes":[{"field":"rows","kind":"add","index":3,"oldIndex":-1,"keys":["3"],"items":[{"id":3,"label":"row 3"}]}]}
            """;
        Require(expanded == Expected, $"The generators expanded to {expanded}.");
    }

    [Test]
    public async Task HeldPushesWaitForReleaseInOrderAsync()
    {
        using var transport = new ScenarioTransport();
        using var serving = transport.Serve(new EchoHandler());
        var first = transport.Connect("c1");
        var second = transport.Connect("c2");
        transport.Send(first, "{}"u8.ToArray(), "r1");
        transport.Hold();
        var broadcast = transport.BroadcastAsync("""{"n":1}"""u8.ToArray()).AsTask();
        var targeted = transport.SendAsync(second.Key, """{"n":2}"""u8.ToArray()).AsTask();
        // Replies are not held.
        transport.Send(first, "{}"u8.ToArray(), "r2");
        Require(!broadcast.IsCompleted && !targeted.IsCompleted, "A held push completed.");
        transport.Release();
        Require(await targeted.WaitAsync(TimeSpan.FromSeconds(5)), "The held targeted push was not delivered.");
        await broadcast.WaitAsync(TimeSpan.FromSeconds(5));
        var received = Drain(first);
        Require(received.SequenceEqual(["r1 {}", "r2 {}", "push {\"n\":1}"]), $"The first connection received [{string.Join(", ", received)}].");
        received = Drain(second);
        Require(received.SequenceEqual(["push {\"n\":1}", "push {\"n\":2}"]), $"The second connection received [{string.Join(", ", received)}].");
        transport.Disconnect(second);
        Require(!await transport.SendAsync(second.Key, "{}"u8.ToArray()), "A closed connection took a push.");
    }

    private static List<string> Drain(ScenarioConnection connection)
    {
        var messages = new List<string>();
        while (connection.Messages.Reader.TryRead(out var message)) messages.Add($"{message.ReplyTo ?? "push"} {message.Json}");
        return messages;
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    private static bool Fails(Action action)
    {
        try { action(); return false; }
        catch (ScenarioFailure) { return true; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Answers every request with the request itself.
    private sealed class EchoHandler : IBridgeRequestHandler
    {
        public ValueTask<ReadOnlyMemory<byte>> HandleAsync(BridgeRequest request, CancellationToken cancellationToken) =>
            new(request.Message.ToArray());

        public void ConnectionClosed(string connectionKey) { }
    }
}
