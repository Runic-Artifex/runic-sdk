using System.Text;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// text-splice/1 (specs/application/README.md, "Features" and "Edits"; state-changes.md, "Text splices"): frames and set
// requests change text members with splices on connections that use the feature, and sets everywhere else.
public sealed class TextSpliceTests
{
    // 100,000 code units, as the analysis measured.
    private static readonly string Large = string.Concat(Enumerable.Repeat("0123456789", 10_000));

    [Test]
    public async Task AKeystrokeInALargeTextMemberIsOneSpliceBothWaysAsync()
    {
        var model = new TextSpliceViewModel { Body = Large };
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new TextSpliceBridge(transport, model, "splice");
        var hello = Parse(transport.Request(Hello("s-1")));
        Require(hello.GetProperty("features").EnumerateArray().Any(feature => feature.GetString() == "text-splice/1"),
            $"The hello reply does not list text-splice/1: {hello}");
        var revision = Parse(transport.Request(Open)).GetProperty("revision").GetInt64();
        transport.TakeMessages();

        // A keystroke: the request and the frame carry the splice, not the 100,000 code units.
        var set = $$$"""{"kind":"set","route":"splice","member":"body","edit":1,"splice":{"baseRevision":{{{revision}}},"length":100000,"index":5000,"delete":0,"text":"x"}}""";
        Require(Encoding.UTF8.GetByteCount(set) < 200, "The request is not small.");
        var reply = Parse(transport.Request(set));
        Require(reply.GetProperty("ok").GetBoolean(), $"The splice was refused: {reply}");
        Require(model.Body == Large.Insert(5000, "x"), "The setter did not receive the full value.");
        var frame = Frames(transport).Single();
        Require(frame.Length < 200, $"The frame is not small: {frame.Length} bytes.");
        RequireSplice(frame, "body", length: 100_000, index: 5000, delete: 0, text: "x");

        // The next keystroke, typed before the frame arrived, is a splice of the previous edit's value.
        reply = Parse(transport.Request("""{"kind":"set","route":"splice","member":"body","edit":2,"splice":{"baseEdit":1,"length":100001,"index":5001,"delete":0,"text":"y"}}"""));
        Require(reply.GetProperty("ok").GetBoolean(), $"A splice of the previous edit was refused: {reply}");
        Require(model.Body == Large.Insert(5000, "xy"), "The chained splice was not applied to the previous edit's value.");
        RequireSplice(Frames(transport).Single(), "body", length: 100_001, index: 5001, delete: 0, text: "y");

        // A change of .NET: a deletion is a splice too.
        model.Body = Large.Insert(5000, "xy").Remove(10, 3);
        RequireSplice(await NextFrameAsync(transport), "body", length: 100_002, index: 10, delete: 3, text: "");
        var full = Parse(transport.Request("""{"kind":"snapshot","route":"splice"}"""));
        Require(full.GetProperty("state").GetProperty("body").GetString() == model.Body, "The state differs from the model.");
    }

    [Test]
    public void ASpliceThatTheProducerCannotApplyAdmitsNothing()
    {
        var model = new TextSpliceViewModel { Body = Large };
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new TextSpliceBridge(transport, model, "splice");
        transport.Request(Hello("s-1"));
        var revision = Parse(transport.Request(Open)).GetProperty("revision").GetInt64();
        foreach (var splice in new[]
        {
            // An edit this connection never sent.
            """{"baseEdit":3,"length":100000,"index":0,"delete":0,"text":"x"}""",
            // A length that is not the base's.
            $$"""{"baseRevision":{{revision}},"length":99999,"index":0,"delete":0,"text":"x"}""",
            // A range outside the base.
            $$"""{"baseRevision":{{revision}},"length":100000,"index":99999,"delete":2,"text":"x"}""",
            // A revision after the route's last publication.
            $$"""{"baseRevision":{{revision + 1}},"length":100000,"index":0,"delete":0,"text":"x"}""",
        })
            RequireError(transport.Request(SetSplice("body", 5, splice)), "rejected", "splice-base");
        Require(model.Body == Large, "A refused splice changed the model.");
        // Nothing was admitted: edit 1 still applies.
        var reply = Parse(transport.Request($$$"""{"kind":"set","route":"splice","member":"body","edit":1,"splice":{"baseRevision":{{{revision}}},"length":100000,"index":0,"delete":1,"text":""}}"""));
        Require(reply.GetProperty("ok").GetBoolean() && !reply.TryGetProperty("superseded", out _), $"Edit 1 was not applied: {reply}");
        Require(model.Body == Large[1..], "Edit 1 was not applied.");
        // The base revision is now older than the member's last change.
        RequireError(transport.Request($$$"""{"kind":"set","route":"splice","member":"body","edit":2,"splice":{"baseRevision":{{{revision}}},"length":100000,"index":0,"delete":0,"text":"x"}}"""), "rejected", "splice-base");
        // A surrogate pair is never split.
        model.Body = Large + "\U0001F600";
        var paired = Parse(transport.Request("""{"kind":"snapshot","route":"splice"}""")).GetProperty("revision").GetInt64();
        RequireError(transport.Request($$$"""{"kind":"set","route":"splice","member":"body","edit":3,"splice":{"baseRevision":{{{paired}}},"length":100002,"index":100001,"delete":0,"text":"x"}}"""), "rejected", "splice-base");
    }

    [Test]
    public void SplicesNeedTheFeatureAndATextMember()
    {
        var model = new TextSpliceViewModel { Body = Large };
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new TextSpliceBridge(transport, model, "splice");
        transport.Request(Hello("s-1", features: false));
        var revision = Parse(transport.Request(Open)).GetProperty("revision").GetInt64();
        var splice = $$"""{"baseRevision":{{revision}},"length":100000,"index":0,"delete":0,"text":"x"}""";
        RequireError(transport.Request(SetSplice("body", 1, splice)), "rejected", "invalid-request");

        transport.Request(Hello("s-2"), "c2");
        transport.Request(Open.Replace("p-1", "p-2", StringComparison.Ordinal), "c2");
        RequireError(transport.Request(SetSplice("id", 1, splice), "c2"), "rejected", "invalid-request");
        RequireError(transport.Request(SetSplice("body", 1, splice).Replace("\"edit\"", "\"value\":\"x\",\"edit\"", StringComparison.Ordinal), "c2"), "rejected", "invalid-request");
        RequireError(transport.Request("""{"kind":"set","route":"splice","member":"body","edit":1,"splice":{"length":1,"index":0,"delete":0,"text":"x"}}""", "c2"), "rejected", "invalid-request");
        Require(model.Body == Large, "An invalid splice changed the model.");
    }

    [Test]
    public async Task AWindowWithAConnectionWithoutTheFeaturePublishesSetsAsync()
    {
        var model = new TextSpliceViewModel { Body = Large };
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new TextSpliceBridge(transport, model, "splice");
        transport.Request(Hello("s-1"));
        transport.Request(Hello("s-2", features: false), "c2");
        transport.Request(Open);
        transport.TakeMessages();
        model.Body = Large + "!";
        var frame = await NextFrameAsync(transport);
        Require(Change(frame, "body") is { } set && set.GetProperty("kind").GetString() == "set"
            && set.GetProperty("value").GetString() == Large + "!",
            $"A frame for a connection without text-splice/1 has a splice: {Truncate(frame)}");
        transport.CloseConnection("c2");
        model.Body = Large + "!?";
        RequireSplice(await NextFrameAsync(transport), "body", length: 100_001, index: 100_001, delete: 0, text: "?");
    }

    [Test]
    public async Task AFrameOnItsWayWhenAConnectionWithoutTheFeatureJoinsCarriesSetsAsync()
    {
        var model = new TextSpliceViewModel { Body = Large };
        using var transport = new HeldTransport();
        using var bridge = new TextSpliceBridge(transport, model, "splice");
        await transport.RequestAsync(Hello("s-1"), "c1");
        await transport.RequestAsync(Open, "c1");
        // The transport holds the first frame, so the second waits in the delivery queue with its splices.
        transport.Hold();
        model.Body = Large + "!";
        await transport.WaitForHeldAsync();
        model.Body = Large + "!?";
        // A read publishes what is pending first: the frame is queued with its splice before the connection joins.
        await transport.RequestAsync("""{"kind":"snapshot","route":"splice"}""", "c1");
        await transport.RequestAsync(Hello("s-2", features: false), "c2");
        transport.Release();
        var frames = await transport.FramesAsync(2);
        RequireSplice(frames[0], "body", length: 100_000, index: 100_000, delete: 0, text: "!");
        Require(Change(frames[1], "body") is { } set && set.GetProperty("kind").GetString() == "set"
            && set.GetProperty("value").GetString() == Large + "!?",
            $"The queued frame kept its splice for a connection without text-splice/1: {Truncate(frames[1])}");
    }

    [Test]
    public async Task ShortValuesNullAndOtherTypesAreSetsAsync()
    {
        var model = new TextSpliceViewModel { Body = "short" };
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new TextSpliceBridge(transport, model, "splice");
        transport.Request(Hello("s-1"));
        transport.Request(Open);
        transport.TakeMessages();
        model.Body = "shorter";
        Require(Change(await NextFrameAsync(transport), "body")!.Value.GetProperty("kind").GetString() == "set", "A short value was spliced.");
        model.Report(Large);
        Require(Change(await NextFrameAsync(transport), "status")!.Value.GetProperty("kind").GetString() == "set", "A change from null was spliced.");
        model.Report(Large + ".");
        RequireSplice(await NextFrameAsync(transport), "status", length: 100_000, index: 100_000, delete: 0, text: ".");
        model.Report(null);
        Require(Change(await NextFrameAsync(transport), "status")!.Value.GetProperty("value").ValueKind == JsonValueKind.Null, "A change to null was not a set.");
        model.Id = Guid.NewGuid();
        Require(Change(await NextFrameAsync(transport), "id")!.Value.GetProperty("kind").GetString() == "set", "A uuid member was spliced.");
    }

    [Test]
    public void TheSpliceRuleKeepsTheLongerShape()
    {
        // One splice from the common prefix and suffix.
        Require(BridgeTextSplice.Between(Large, Large.Insert(7, "abc"), out var first, out _) == 1
            && first == new BridgeTextSplice(7, 0, "abc"), $"An insertion is {first}.");
        // A rolling value, a log that drops its oldest lines: drop the start, append.
        var log = string.Concat(Enumerable.Range(0, 5000).Select(line => $"line {line:D5}\n"));
        var rolled = log[110..] + "line 05000\n";
        Require(BridgeTextSplice.Between(log, rolled, out first, out var second) == 2
            && first == new BridgeTextSplice(0, 110, "") && second == new BridgeTextSplice(log.Length - 110, 0, "line 05000\n"),
            $"A rolling value is {first} and {second}.");
        // Fewer than 64 kept code units: a set.
        Require(BridgeTextSplice.Between(new string('a', 64), new string('a', 64) + "b", out _, out _) == 1, "64 code units were not enough.");
        Require(BridgeTextSplice.Between(new string('a', 63), new string('a', 63) + "b", out _, out _) == 0, "63 kept code units were spliced.");
        Require(BridgeTextSplice.Between(Large, new string('z', 100_000), out _, out _) == 0, "A rewrite was spliced.");
        // The prefix stops before a surrogate pair whose high surrogate both values share.
        Require(BridgeTextSplice.Between(Large + "\U0001F600", Large + "\U0001F601", out first, out _) == 1
            && first == new BridgeTextSplice(100_000, 2, "\U0001F601"), $"A surrogate pair was split: {first}.");
        // The suffix starts after one whose low surrogate both share.
        Require(BridgeTextSplice.Between("\U0001F600" + Large, "\U0001F700" + Large, out first, out _) == 1
            && first == new BridgeTextSplice(0, 2, "\U0001F700"), $"A surrogate pair was split: {first}.");
        Require(!BridgeTextSplice.TryApply("a\U0001F600b", 4, 2, 0, "x", out _), "A splice inside a pair applied.");
        Require(BridgeTextSplice.TryApply("a\U0001F600b", 4, 1, 2, "x", out var applied) && applied == "axb", "A valid splice failed.");
    }

    private const string Open = """{"kind":"open","route":"splice","presentation":"p-1"}""";

    private static string Hello(string session, bool features = true) =>
        """{"kind":"hello","protocol":4,"session":""" + "\"" + session + "\"" + ""","client":{"name":"test","version":"1.0.0"}"""
        + (features ? ""","features":["text-splice/1"]""" : "") + "}";

    private static string SetSplice(string member, int edit, string splice) =>
        """{"kind":"set","route":"splice","member":""" + "\"" + member + "\",\"edit\":" + edit + ",\"splice\":" + splice + "}";

    private static IEnumerable<string> Frames(InMemoryBridgeTransport transport) =>
        transport.TakeMessages().Select(message => message.Json).Where(json => Parse(json).GetProperty("kind").GetString() == "frame");

    private static async Task<string> NextFrameAsync(InMemoryBridgeTransport transport)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < timeout)
        {
            if (Frames(transport).ToArray() is [var frame, ..]) return frame;
            await Task.Delay(5);
        }
        throw new InvalidOperationException("The bridge published no frame.");
    }

    private static JsonElement? Change(string frame, string field) =>
        Parse(frame).GetProperty("changes").EnumerateArray().Cast<JsonElement?>()
            .FirstOrDefault(change => change!.Value.GetProperty("field").GetString() == field);

    private static void RequireSplice(string frame, string field, int length, int index, int delete, string text)
    {
        var change = Change(frame, field);
        Require(change is { } splice && splice.GetProperty("kind").GetString() == "splice"
            && splice.GetProperty("length").GetInt32() == length && splice.GetProperty("index").GetInt32() == index
            && splice.GetProperty("delete").GetInt32() == delete && splice.GetProperty("text").GetString() == text,
            $"Expected a splice of {field} ({length}, {index}, {delete}, '{text}'): {Truncate(frame)}");
    }

    private static string Truncate(string json) => json.Length > 300 ? json[..300] + "…" : json;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void RequireError(string json, string kind, string reason)
    {
        var reply = Parse(json);
        Require(!reply.GetProperty("ok").GetBoolean() && reply.GetProperty("error").GetProperty("kind").GetString() == kind
            && reply.GetProperty("error").GetProperty("reason").GetString() == reason, $"Expected {kind}/{reason}, got {json}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // A transport whose broadcasts can be held, so a frame waits in the delivery queue while a connection joins.
    private sealed class HeldTransport : IBridgeTransport, IDisposable
    {
        private readonly object _gate = new();
        private readonly List<string> _frames = [];
        private IBridgeRequestHandler? _handler;
        private TaskCompletionSource? _release;
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable Serve(IBridgeRequestHandler handler)
        {
            _handler = handler;
            return this;
        }

        public void Hold()
        {
            lock (_gate) _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Release()
        {
            lock (_gate) _release?.TrySetResult();
        }

        public Task WaitForHeldAsync() => _held.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public async ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
        {
            var json = Encoding.UTF8.GetString(message.Span);
            Task? wait;
            lock (_gate)
            {
                if (Parse(json).GetProperty("kind").GetString() == "frame") _frames.Add(json);
                wait = _release?.Task;
            }
            _held.TrySetResult();
            if (wait is not null) await wait.ConfigureAwait(false);
        }

        public async Task<string[]> FramesAsync(int count)
        {
            var timeout = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < timeout)
            {
                lock (_gate)
                    if (_frames.Count >= count) return [.. _frames];
                await Task.Delay(5);
            }
            throw new InvalidOperationException("The bridge did not publish the frames.");
        }

        public async Task<string> RequestAsync(string json, string connection) =>
            Encoding.UTF8.GetString((await _handler!.HandleAsync(
                new BridgeRequest(Encoding.UTF8.GetBytes(json), "client", connection), CancellationToken.None)).Span);

        public void Dispose() { }
    }
}
