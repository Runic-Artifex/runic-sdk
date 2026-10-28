using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Drives the C# producer through the shared text delta fixtures in
// specs/application/fixtures/text-deltas and requires byte-identical frames.
// The views package applies the same fixtures as a consumer.
internal sealed class TextDeltaConformanceTests
{
    private const string Route = "log";

    public static IEnumerable<Func<(string Name, JsonObject TestCase)>> Cases()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "text-deltas");
        var files = Directory.GetFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Require(files.Length >= 1, $"The text delta fixtures were not copied to {directory}.");
        foreach (var file in files)
            foreach (var testCase in JsonNode.Parse(File.ReadAllText(file))!["cases"]!.AsArray())
            {
                var name = $"{Path.GetFileName(file)}: {testCase!["name"]}";
                var json = testCase.ToJsonString();
                yield return () => (name, JsonNode.Parse(json)!.AsObject());
            }
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    [DisplayName("$name")]
    public async Task ProducerMatchesTheFixtureAsync(string name, JsonObject testCase)
    {
        var model = new TextDeltaViewModel();
        var initial = testCase["initial"]!;
        model.SetLog(initial["log"]?.GetValue<string>());
        model.Title = initial["title"]!.GetValue<string>();
        using var transport = new FrameTransport();
        using var bridge = new TextDeltaBridge(transport, model, Route);
        RequireJson(name, "initial snapshot", initial, State(transport));

        foreach (var step in testCase["steps"]!.AsArray())
        {
            if (step!["batch"] is JsonArray batch)
            {
                using (BridgeSnapshotBatch.Begin(model)) foreach (var mutation in batch) Apply(model, mutation!);
            }
            else foreach (var mutation in step["each"]!.AsArray()) Apply(model, mutation!);
        }

        var expectedFrames = testCase["frames"]!.AsArray();
        var frames = await transport.WaitForAsync(expectedFrames.Count);
        Require(frames.Count == expectedFrames.Count,
            $"{name}: expected {expectedFrames.Count} frames, observed {frames.Count}:\n{string.Join("\n", frames)}");
        for (var index = 0; index < frames.Count; index++)
            RequireJson(name, $"frame {index}", expectedFrames[index]!, frames[index]);
        RequireJson(name, "final snapshot", testCase["expected"]!, State(transport));
    }

    // After a full state, the next frame continues the value that state wrote.
    [Test]
    public async Task AFrameContinuesTheLastFullStateAsync()
    {
        var model = new TextDeltaViewModel();
        model.SetLog("a");
        using var transport = new FrameTransport();
        using var bridge = new TextDeltaBridge(transport, model, Route);
        model.Title = "build";
        _ = await transport.WaitForAsync(1);
        model.Append("b");
        var frames = await transport.WaitForAsync(2);
        Require(frames.Count == 2
            && frames[0] == """{"revision":1,"log":"a","title":"build"}"""
            && frames[1] == """{"__runicDelta":1,"baseRevision":1,"revision":2,"changes":[{"field":"log","kind":"text","length":1,"trim":0,"text":"b"}]}""",
            $"The text did not continue the full state:\n{string.Join("\n", frames)}");
    }

    // The kept text is the longest suffix of the page's value that the new value starts with.
    [Test]
    [Arguments("abc", "abcd", 0, "d")]
    [Arguments("abcdef", "cdefgh", 2, "gh")]
    [Arguments("abab", "babab", 1, "ab")]
    [Arguments("abc", "", 3, "")]
    [Arguments("abc", "xyz", 3, "xyz")]
    [Arguments("", "a", 0, "a")]
    [DisplayName("BridgeTextDelta.Between($baseline, $value)")]
    public void TheDeltaKeepsTheLongestSharedText(string baseline, string value, int trim, string text)
    {
        var delta = BridgeTextDelta.Between(baseline, value);
        Require(delta == new BridgeTextDelta(baseline.Length, trim, text), $"Between({baseline}, {value}) was {delta}.");
    }

    // Repeated text (a progress line, padding) must not make the search give up and resend everything.
    [Test]
    public void ARepeatedLogKeepsItsText()
    {
        var baseline = new string('.', 4000) + string.Concat(Enumerable.Repeat("line\n", 200));
        var value = (baseline + "line\nline\n")[12..];
        var delta = BridgeTextDelta.Between(baseline, value);
        Require(delta is { Trim: 12, Text: "line\nline\n" }, $"The repeated log was {delta}.");
        var pair = BridgeTextDelta.Between("x\uD83D", "x\uD83D\uDE00");
        Require(pair is null, $"An append inside a surrogate pair was {pair}.");
        foreach (var (from, to) in new[] { (baseline, value), (value, baseline), ("a😀b", "😀bc"), (new string('x', 300), new string('x', 299) + "y") })
            if (BridgeTextDelta.Between(from, to) is { } result)
                Require(from[result.Trim..] + result.Text == to && result.Length == from.Length, $"Between did not rebuild the value: {result}.");
    }

    // Validation stays atomic with full states, as for collections.
    [Test]
    public async Task AViewModelWithValidationPublishesFullStatesAsync()
    {
        var model = new ValidatedTextViewModel();
        using var transport = new FrameTransport();
        using var bridge = new ValidatedTextBridge(transport, model, Route);
        model.Append("a");
        var frames = await transport.WaitForAsync(1);
        Require(frames.Count == 1 && !frames[0].Contains("__runicDelta", StringComparison.Ordinal),
            $"A ViewModel with validation published a frame: {string.Join("\n", frames)}");
    }

    // Measures the point of the attribute on a 128 KB log (#300).
    [Test]
    public async Task ALongLogSendsOnlyWhatItAppendsAsync()
    {
        const int window = 128_000;
        var model = new TextDeltaViewModel();
        model.SetLog(new string('x', window));
        using var transport = new FrameTransport();
        using var bridge = new TextDeltaBridge(transport, model, Route);
        var line = "building /nix/store/...-package.drv\n";
        for (var index = 0; index < 20; index++)
        {
            var next = model.Log + line;
            model.SetLog(next[^window..]);
        }
        var frames = await transport.WaitForAsync(20);
        Require(frames.Count == 20 && frames.All(frame => frame.Length < 200),
            $"A log that keeps its last {window} characters resent them: {string.Join(", ", frames.Select(frame => frame.Length))}");
    }

    private static void Apply(TextDeltaViewModel model, JsonNode mutation)
    {
        switch (mutation["op"]!.GetValue<string>())
        {
            case "append": model.Append(mutation["text"]!.GetValue<string>()); break;
            case "set": model.SetLog(mutation["value"]?.GetValue<string>()); break;
            case "title": model.Title = mutation["value"]!.GetValue<string>(); break;
            default: throw new InvalidOperationException($"Unknown fixture mutation {mutation.ToJsonString()}.");
        }
    }

    private static string State(FrameTransport transport)
    {
        using var reply = JsonDocument.Parse(transport.Call($"{Route}Snapshot"));
        return reply.RootElement.GetProperty("state").GetRawText();
    }

    private static void RequireJson(string name, string what, JsonNode expected, string actual)
    {
        var encoded = expected.ToJsonString();
        Require(string.Equals(encoded, actual, StringComparison.Ordinal),
            $"{name}: {what} differs from the fixture.\nexpected: {encoded}\nactual:   {actual}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FrameTransport : LegacyTestTransport
    {
        private readonly object _gate = new();
        private readonly List<string> _frames = [];
        protected override void Publish(string name, string stateJson)
        {
            lock (_gate) _frames.Add(stateJson);
        }

        public async Task<IReadOnlyList<string>> WaitForAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate) if (_frames.Count >= count) break;
                await Task.Delay(5);
            }
            // Give an unexpected trailing frame a chance to show up.
            await Task.Delay(50);
            lock (_gate) return _frames.ToArray();
        }

    }
}
