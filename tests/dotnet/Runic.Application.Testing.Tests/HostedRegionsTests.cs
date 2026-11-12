using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// hosted-regions/1 (specs/application/README.md, "Hosted regions"): the producer's wire layer and the window's
// bookkeeping of the regions its pages declare (BridgeHostedRegions).
public sealed class HostedRegionsTests
{
    private const string Hello = """{"kind":"hello","protocol":4,"session":"SESSION","client":{"name":"test-client","version":"1.0.0"},"features":["hosted-regions/1"]}""";
    private const string State = """{"kind":"hosted-region-state","name":"check","width":800,"height":520.5,"scale":1.5,"x":0,"y":-96,"visible":true,"focused":true,"focusSource":"tab"}""";

    [Test]
    public async Task DeclarationsReportsAndRemovalsReachTheObserverInOrderAsync()
    {
        using var window = new Window();
        var observer = new Recorder();
        using var lease = window.Regions.Observe(observer, out var current);
        Require(current.Count == 0, "A new window declares regions.");
        var key = window.Connect("c1");

        Require(Ok(window.Transport.Request("""{"kind":"hosted-region-declare","name":"check","sizing":"content-height"}""", "c1")), "The declaration failed.");
        Require(Ok(window.Transport.Request(State, "c1")), "The report failed.");
        Require(Ok(window.Transport.Request("""{"kind":"hosted-region-declare","name":"other"}""", "c1")), "The second declaration failed.");
        Require(Ok(window.Transport.Request("""{"kind":"hosted-region-remove","name":"check"}""", "c1")), "The removal failed.");
        await window.Regions.WhenRaised();
        Require(observer.Events.SequenceEqual([
            $"declared check ContentHeight {key}",
            $"state check {key} 800x520.5@1.5 0,-96 visible focused Tab",
            $"declared other Fill {key}",
            $"removed check {key} page",
        ]), $"The observer saw [{string.Join(", ", observer.Events)}].");

        // A later observer gets the current declarations with their last report, and nothing twice.
        Require(Ok(window.Transport.Request(State.Replace("check", "other", StringComparison.Ordinal).Replace("\"tab\"", "null", StringComparison.Ordinal)
            .Replace("\"focused\":true", "\"focused\":false", StringComparison.Ordinal), "c1")), "The report failed.");
        await window.Regions.WhenRaised();
        var later = new Recorder();
        using var laterLease = window.Regions.Observe(later, out var declared);
        var other = declared.Single();
        Require(other.Name == "other" && other.Sizing == HostedRegionSizing.Fill && other.ConnectionKey == key
            && other.Report is { Focused: false, FocusSource: null, Width: 800 }, $"The current declarations are [{string.Join(", ", declared)}].");

        // A closed connection removes its regions.
        window.Transport.CloseConnection("c1");
        await window.Regions.WhenRaised();
        Require(later.Events.SequenceEqual([$"removed other {key} closed"]), $"The later observer saw [{string.Join(", ", later.Events)}].");
        Require(window.Regions.Declared.Count == 0, "A closed connection's region stayed declared.");
    }

    [Test]
    public async Task TheLatestDeclarationOfANameWinsAsync()
    {
        using var window = new Window();
        var observer = new Recorder();
        using var lease = window.Regions.Observe(observer, out _);
        var first = window.Connect("c1");
        var second = window.Connect("c2");
        Require(Ok(window.Transport.Request("""{"kind":"hosted-region-declare","name":"check"}""", "c1")), "The first declaration failed.");
        window.Transport.TakeMessages();
        Require(Ok(window.Transport.Request("""{"kind":"hosted-region-declare","name":"check"}""", "c2")), "The second declaration failed.");
        await window.SentAsync();
        Require(window.Transport.TakeMessages().Single() == new RunicBridgeMessage("""{"kind":"hosted-region-empty","name":"check","reason":"detached"}""", "c1"),
            "The earlier declaration's page was not emptied.");
        // The superseded connection's reports and removal are answered ok and change nothing.
        Require(Ok(window.Transport.Request(State, "c1")) && Ok(window.Transport.Request("""{"kind":"hosted-region-remove","name":"check"}""", "c1")),
            "The superseded connection's requests failed.");
        await window.Regions.WhenRaised();
        Require(observer.Events.SequenceEqual([
            $"declared check Fill {first}",
            $"removed check {first} superseded",
            $"declared check Fill {second}",
        ]), $"The observer saw [{string.Join(", ", observer.Events)}].");
        Require(window.Regions.Declared.Single().ConnectionKey == second, "The latest declaration does not own the name.");
    }

    [Test]
    public async Task PushesGoToTheConnectionThatDeclaresTheNameAsync()
    {
        using var window = new Window();
        window.Connect("c1");
        var regions = window.Regions;
        Require(!regions.ShowFrame("check", "http://[::1]:40117/_runic_frame/x", null, "") && !regions.Focus("check"),
            "A push for an undeclared name was sent.");
        Require(Ok(window.Transport.Request("""{"kind":"hosted-region-declare","name":"check"}""", "c1")), "The declaration failed.");
        Require(regions.ShowFrame("check", "http://[::1]:40117/_runic_frame/x", "System check", "camera") && regions.Update("check", HostedRegionTitle.Set(null), 320)
            && regions.Update("check", height: 10) && regions.Empty("check", HostedRegionEmptyReason.Ended) && regions.Focus("check"), "A push was not sent.");
        await window.SentAsync();
        Require(window.Transport.TakeMessages().Select(static message => message.Json).SequenceEqual([
            """{"kind":"hosted-region-content","name":"check","embedding":"frame","src":"http://[::1]:40117/_runic_frame/x","title":"System check","allow":"camera"}""",
            """{"kind":"hosted-region-update","name":"check","title":null,"height":320}""",
            """{"kind":"hosted-region-update","name":"check","height":10}""",
            """{"kind":"hosted-region-empty","name":"check","reason":"ended"}""",
            """{"kind":"hosted-region-focus","name":"check"}""",
        ]), "The pushes were not sent in order.");
        Require(Throws<ArgumentException>(() => regions.ShowFrame("check", "", null, ""))
            && Throws<ArgumentException>(() => regions.Update("check", HostedRegionTitle.Set(new string('t', 1025))))
            && Throws<ArgumentOutOfRangeException>(() => regions.Update("check", height: 32_768)), "An invalid push was accepted.");
    }

    [Test]
    public void InvalidRequestsAreRejected()
    {
        using var window = new Window();
        window.Connect("c1");
        // Without the feature its kinds are not requests of the connection.
        window.Transport.Request(Hello.Replace("SESSION", "s-plain", StringComparison.Ordinal).Replace(""","features":["hosted-regions/1"]""", "", StringComparison.Ordinal), "plain");
        RequireError(window.Transport.Request("""{"kind":"hosted-region-declare","name":"check"}""", "plain"), "invalid-request");

        foreach (var name in new[] { "\"\"", "\"a b\"", "\"" + new string('a', 65) + "\"", "1", "\"é\"" })
            RequireError(window.Transport.Request($$"""{"kind":"hosted-region-declare","name":{{name}}}""", "c1"), "invalid-request");
        RequireError(window.Transport.Request("""{"kind":"hosted-region-declare","name":"check","sizing":"auto"}""", "c1"), "invalid-request");
        RequireError(window.Transport.Request("""{"kind":"hosted-region-remove","name":"check"}""", "c1"), "invalid-request");
        RequireError(window.Transport.Request(State, "c1"), "invalid-request");
        Require(Ok(window.Transport.Request("""{"kind":"hosted-region-declare","name":"check"}""", "c1")), "The declaration failed.");
        RequireError(window.Transport.Request("""{"kind":"hosted-region-declare","name":"check"}""", "c1"), "invalid-request");
        foreach (var (member, value) in new[] { ("width", "-1"), ("height", "32768"), ("scale", "0.2"), ("scale", "9"), ("x", "-32769"), ("y", "32768"),
            ("visible", "1"), ("focused", "null"), ("focusSource", "\"keyboard\""), ("width", "\"800\"") })
            RequireError(window.Transport.Request(Regex.Replace(State, $"\"{member}\":[^,}}]+", $"\"{member}\":{value}"), "c1"), "invalid-request");
        RequireError(window.Transport.Request("""{"kind":"hosted-region-state","name":"check","width":1,"height":1,"scale":1,"x":0,"y":0,"visible":true,"focused":false}""", "c1"),
            "invalid-request");

        // At most 64 regions per connection.
        for (var index = 1; index < BridgeHostedRegions.MaximumPerConnection; index++)
            Require(Ok(window.Transport.Request($$"""{"kind":"hosted-region-declare","name":"r{{index}}"}""", "c1")), $"Declaration {index} failed.");
        RequireError(window.Transport.Request("""{"kind":"hosted-region-declare","name":"one-more"}""", "c1"), "capacity");
        Require(Ok(window.Transport.Request("""{"kind":"hosted-region-remove","name":"r1"}""", "c1"))
            && Ok(window.Transport.Request("""{"kind":"hosted-region-declare","name":"one-more"}""", "c1")), "A removal did not free a place.");
    }

    private static bool Ok(string reply) => reply == """{"ok":true}""";

    private static void RequireError(string json, string reason)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Require(!root.GetProperty("ok").GetBoolean() && root.GetProperty("error").GetProperty("kind").GetString() == "rejected"
            && root.GetProperty("error").GetProperty("reason").GetString() == reason, $"Expected rejected/{reason}, got {json}.");
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

    private sealed class Recorder : IHostedRegionObserver
    {
        public ConcurrentQueue<string> Events { get; } = new();

        public void Declared(HostedRegionDeclaration declaration) =>
            Events.Enqueue($"declared {declaration.Name} {declaration.Sizing} {declaration.ConnectionKey}");

        public void Removed(HostedRegionRemoval removal) =>
            Events.Enqueue($"removed {removal.Name} {removal.ConnectionKey} {(removal.ConnectionClosed ? "closed" : removal.Superseded ? "superseded" : "page")}");

        public void StateChanged(HostedRegionReport report) =>
            Events.Enqueue(FormattableString.Invariant(
                $"state {report.Name} {report.ConnectionKey} {report.Width}x{report.Height}@{report.Scale} {report.X},{report.Y} {(report.Visible ? "visible" : "hidden")} {(report.Focused ? "focused" : "unfocused")} {report.FocusSource}"));
    }

    private sealed class Window : IDisposable
    {
        public Window()
        {
            Transport = new InMemoryBridgeTransport();
            Session = new WindowContentSession(Transport, new WindowContentSessionOptions());
            Dispatcher = BridgeDispatcher.For(Transport);
        }

        public InMemoryBridgeTransport Transport { get; }
        public WindowContentSession Session { get; }
        public BridgeDispatcher Dispatcher { get; }
        public BridgeHostedRegions Regions => Dispatcher.HostedRegions;

        // Completes the hello of a connection that uses the feature and returns its connection key.
        public string Connect(string transportKey)
        {
            var reply = Transport.Request(Hello.Replace("SESSION", "s-" + transportKey, StringComparison.Ordinal), transportKey);
            Require(reply.StartsWith("{\"ok\":true", StringComparison.Ordinal), $"The hello was answered {reply}.");
            return Dispatcher.ConnectionKeyOf(transportKey)!;
        }

        public Task SentAsync() => BridgeTargetedPushes.For(Transport).WhenSent();

        public void Dispose()
        {
            Session.Dispose();
            Transport.Dispose();
        }
    }
}
