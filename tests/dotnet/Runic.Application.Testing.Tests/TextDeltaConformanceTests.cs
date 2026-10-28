using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Text changes of append-only text fields (specs/application/state-changes.md, "text"). The shared cases of the former
// text delta fixtures are scenarios of the conformance corpus (specs/application/conformance/scenarios/state-changes,
// run by Conformance/ProducerScenarioTests); these tests cover the generated Bridges and BridgeTextDelta.
internal sealed class TextDeltaConformanceTests
{
    private const string Route = "log";

    // After a state the client read, the next frames continue the value that state wrote.
    [Test]
    public async Task AFrameContinuesTheLastFullStateAsync()
    {
        var model = new TextDeltaViewModel();
        model.SetLog("a");
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new TextDeltaBridge(transport, model, Route);
        var revision = Revision(transport);
        model.Title = "build";
        var title = await WaitForAsync(transport, 1);
        model.Append("b");
        var frames = await WaitForAsync(transport, 1);
        var titleRevision = revision + 1;
        Require(title.Count == 1 && frames.Count == 1
            && title[0] == $$"""{"kind":"frame","route":"log","baseRevision":{{revision}},"revision":{{titleRevision}},"changes":[{"field":"title","kind":"set","value":"build"}]}"""
            && frames[0] == $$"""{"kind":"frame","route":"log","baseRevision":{{titleRevision}},"revision":{{titleRevision + 1}},"changes":[{"field":"log","kind":"text","length":1,"trim":0,"text":"b"}]}""",
            $"The text did not continue the read state:\n{string.Join("\n", title.Concat(frames))}");
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

    // Validation stays atomic: a View with validation publishes its text as a set in one frame with the members it
    // validates, as for collections.
    [Test]
    public async Task AViewModelWithValidationPublishesSetsAsync()
    {
        var model = new ValidatedTextViewModel();
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new ValidatedTextBridge(transport, model, Route);
        _ = Revision(transport);
        model.Append("a");
        var frames = await WaitForAsync(transport, 1);
        Require(frames.Count == 1 && frames[0].Contains("""{"field":"log","kind":"set","value":"a"}""", StringComparison.Ordinal),
            $"A ViewModel with validation published a text change: {string.Join("\n", frames)}");
    }


    // Measures the point of the attribute on a 128 KB log (#300).
    [Test]
    public async Task ALongLogSendsOnlyWhatItAppendsAsync()
    {
        const int window = 128_000;
        var model = new TextDeltaViewModel();
        model.SetLog(new string('x', window));
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new TextDeltaBridge(transport, model, Route);
        _ = Revision(transport);
        var line = "building /nix/store/...-package.drv\n";
        // Each append is its own publication unit here. (Units that see more than 8 appends of a repeated line exceed
        // BridgeTextDelta's candidate bound and send the whole text.)
        List<string> frames = [];
        for (var index = 0; index < 20; index++)
        {
            var next = model.Log + line;
            model.SetLog(next[^window..]);
            frames.AddRange(await WaitForAsync(transport, 1));
        }
        Require(frames.Count == 20 && frames.All(frame => frame.Length < 250),
            $"A log that keeps its last {window} characters resent them: {string.Join(", ", frames.Select(frame => frame.Length))}");
    }

    // Reads the route's state, which frames continue, and returns its revision.
    private static long Revision(InMemoryBridgeTransport transport)
    {
        using var reply = transport.SnapshotState(Route);
        return reply.RootElement.GetProperty("revision").GetInt64();
    }

    // The pushes of the route, once count arrived or after 5 seconds.
    private static async Task<IReadOnlyList<string>> WaitForAsync(InMemoryBridgeTransport transport, int count)
    {
        List<string> pushes = [];
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && pushes.Count < count)
        {
            pushes.AddRange(Take(transport));
            if (pushes.Count < count) await Task.Delay(5);
        }
        // Give an unexpected trailing push a chance to show up.
        await Task.Delay(50);
        pushes.AddRange(Take(transport));
        return pushes;
    }

    private static string[] Take(InMemoryBridgeTransport transport) =>
        transport.TakePushes(Route).Select(push =>
        {
            using (push) return push.RootElement.GetRawText();
        }).ToArray();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
