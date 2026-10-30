using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Testing;
using Runic.Application.Testing.Tests.Conformance;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// presentation-epoch/1 and presented/1 (specs/application/README.md, "Presentation epochs", "Painted states" and
// "Window close"): epochs in replies and states, renewals, scoped commands and their order of checks, present and
// presented, and the closing push of a window's close.
public sealed class PresentationFeatureTests
{
    private const string Epoch = "presentation-epoch/1";
    private const string Presented = "presented/1";

    [Test]
    public void OpenSnapshotAndStatesCarryTheEpochOnlyWhereTheFeatureIsInUse()
    {
        using var window = new Wizard();
        window.Hello("old");
        var old = Parse(window.Transport.Request(Open("p-old"), "old"));
        Require(!old.TryGetProperty("epoch", out _), $"An open reply without the feature carries an epoch: {old}");
        window.Renew();
        var state = window.NextState();
        Require(!state.TryGetProperty("epoch", out _), $"A state push carries an epoch while no connection uses the feature: {state}");

        // E7: while one connection uses the feature, the broadcast state carries epoch; a reply only on that connection.
        window.Hello("new", Epoch);
        var open = Parse(window.Transport.Request(Open("p-new"), "new"));
        Require(open.GetProperty("epoch").GetInt64() == state.GetProperty("revision").GetInt64(),
            $"The open reply's epoch is not the revision of the renewal that began it: {open}");
        Require(!Parse(window.Transport.Request(Snapshot, "old")).TryGetProperty("epoch", out _),
            "A snapshot reply on a connection without the feature carries an epoch.");
        window.Renew();
        state = window.NextState();
        Require(state.GetProperty("epoch").GetInt64() == state.GetProperty("revision").GetInt64(),
            $"A renewal's state does not begin its epoch at its revision: {state}");
        var snapshot = Parse(window.Transport.Request(Snapshot, "new"));
        Require(snapshot.GetProperty("epoch").GetInt64() == state.GetProperty("epoch").GetInt64(),
            $"The snapshot reply does not carry the current epoch: {snapshot}");

        window.Transport.CloseConnection("new");
        window.Renew();
        Require(!window.NextState().TryGetProperty("epoch", out _), "A state carries epoch after the last such connection closed.");
    }

    [Test]
    public void ARenewalIsAFullStateAtANewRevisionEvenWithoutAChange()
    {
        using var window = new Wizard();
        window.Hello("c", Epoch);
        var open = Parse(window.Transport.Request(Open("p-1"), "c"));
        var revision = open.GetProperty("revision").GetInt64();
        Require(open.GetProperty("epoch").GetInt64() == revision, $"The first state does not begin the first epoch: {open}");
        window.Renew();
        var state = window.NextState();
        Require(state.GetProperty("revision").GetInt64() > revision && state.GetProperty("epoch").GetInt64() > revision
            && state.GetProperty("state").GetProperty("page").GetInt32() == 1,
            $"The renewal was not a full state at a new revision and epoch: {state}");
    }

    [Test]
    public void AScopedCommandRunsInItsEpochAndASecondOneFromTheSameStateIsSuperseded()
    {
        using var window = new Wizard();
        window.Hello("c", Epoch);
        var epoch = Parse(window.Transport.Request(Open("p-1"), "c")).GetProperty("epoch").GetInt64();
        var first = Parse(window.Transport.Request(Command("next", epoch), "c"));
        var second = Parse(window.Transport.Request(Command("next", epoch), "c"));
        Require(first.GetProperty("ok").GetBoolean() && !first.TryGetProperty("superseded", out _),
            $"The scoped command with the current epoch did not run: {first}");
        Require(second.GetProperty("ok").GetBoolean() && second.GetProperty("superseded").GetBoolean()
            && second.GetProperty("revision").GetInt64() == first.GetProperty("revision").GetInt64(),
            $"The second scoped command was not superseded at the route's revision: {second}");
        Require(window.Page == 2, $"The superseded command ran: page {window.Page}.");
        // The unscoped command of the same state runs.
        Require(!Parse(window.Transport.Request("""{"kind":"command","route":"wizard","member":"ping"}""", "c")).TryGetProperty("superseded", out _),
            "A command without an epoch was superseded.");
    }

    [Test]
    public void ARenewalThatIsNotPublishedYetSupersedesACommandAndTheReplyPublishesIt()
    {
        using var window = new Wizard();
        window.Hello("c", Epoch);
        var epoch = Parse(window.Transport.Request(Open("p-1"), "c")).GetProperty("epoch").GetInt64();
        window.Transport.TakeMessages();
        // A renewal and the command cross in one model turn, before the route's next unit could run.
        var reply = RunicModelTurns.Run(window.Session.ModelContext!, () =>
        {
            RunicPresentation.Renew(window.Model);
            return Parse(window.Transport.Request(Command("next", epoch), "c"));
        });
        Require(reply.GetProperty("superseded").GetBoolean() && window.Page == 1, $"The command crossed the pending renewal: {reply}");
        var state = window.NextState();
        Require(state.GetProperty("revision").GetInt64() == reply.GetProperty("revision").GetInt64()
            && state.GetProperty("epoch").GetInt64() == state.GetProperty("revision").GetInt64(),
            $"The superseded reply's revision is not the published renewal's: reply {reply}, state {state}");
    }

    [Test]
    public void AStaleEpochIsCheckedBeforeInputKeyAndAvailability()
    {
        using var window = new Checks();
        window.Hello("c", Epoch);
        window.Hello("old");
        var epoch = Parse(window.Transport.Request("""{"kind":"open","route":"checks","presentation":"p-1"}""", "c")).GetProperty("epoch").GetInt64();
        window.Renew();
        var stale = epoch;
        var current = window.Epoch("c");
        Require(current != stale, "The renewal did not change the epoch.");
        foreach (var (member, input) in new[] { ("pick", "\"not a number\""), ("pick", "7"), ("locked", null), ("slow", null) })
        {
            var reply = Parse(window.Transport.Request(ChecksCommand(member, input, stale), "c"));
            Require(reply.GetProperty("ok").GetBoolean() && reply.GetProperty("superseded").GetBoolean(),
                $"{member} with a stale epoch was not superseded first: {reply}");
        }
        Require(window.Model.Runs == 0, "A superseded command ran.");
        // With the current epoch the usual checks answer.
        RequireError(window.Transport.Request(ChecksCommand("pick", "\"not a number\"", current), "c"), "rejected", "invalid-input");
        RequireError(window.Transport.Request(ChecksCommand("pick", "7", current), "c"), "rejected", "unresolved-key");
        RequireError(window.Transport.Request(ChecksCommand("locked", null, current), "c"), "rejected", "unavailable");
        Require(Parse(window.Transport.Request(ChecksCommand("pick", "1", current), "c")).GetProperty("ok").GetBoolean()
            && Parse(window.Transport.Request(ChecksCommand("slow", null, current), "c")).GetProperty("ok").GetBoolean()
            && window.Model.Runs == 2, "A command with the current epoch did not run.");
        // The envelope comes first: an unknown command and an epoch that is not a safe integer are invalid requests.
        RequireError(window.Transport.Request(ChecksCommand("missing", null, stale), "c"), "rejected", "invalid-request");
        RequireError(window.Transport.Request("""{"kind":"command","route":"checks","member":"slow","epoch":1.5}""", "c"), "rejected", "invalid-request");
        RequireError(window.Transport.Request("""{"kind":"command","route":"checks","member":"slow","epoch":-1}""", "c"), "rejected", "invalid-request");
        // Without the feature the member is ignored, also when it is not an epoch.
        Require(!Parse(window.Transport.Request(ChecksCommand("slow", null, stale), "old")).TryGetProperty("superseded", out _)
            && Parse(window.Transport.Request("""{"kind":"command","route":"checks","member":"slow","epoch":"x"}""", "old")).GetProperty("ok").GetBoolean()
            && window.Model.Runs == 4, "A command with an epoch on a connection without the feature did not run.");
        // A route nothing serves answers released before the epoch.
        window.DisposeBridge();
        RequireError(window.Transport.Request(ChecksCommand("slow", null, stale), "c"), "released", null);
    }

    [Test]
    public async Task APresentedReportCompletesTheWaitAndAConnectionThatOpensDuringItIsAskedAsync()
    {
        using var window = new Wizard();
        window.Hello("a", Presented);
        window.Hello("b", Presented);
        window.Hello("old");
        window.Transport.Request(Open("p-a"), "a");
        window.Transport.Request(Open("p-old"), "old");
        window.Renew();
        var revision = window.NextState().GetProperty("revision").GetInt64();
        window.Transport.TakeMessages();
        var wait = RunicPresentation.WaitForPresentedAsync(window.Model, TimeSpan.FromSeconds(30)).AsTask();
        await window.Transport.WhenPushesSentAsync();
        var presents = window.Transport.TakeMessages().Where(message => message.Kind == "present").ToArray();
        Require(presents is [{ ConnectionKey: "a" } present] && Parse(present.Json).GetProperty("route").GetString() == "wizard"
            && Parse(present.Json).GetProperty("revision").GetInt64() == revision,
            $"present did not reach exactly the connection with the feature and the route open: {string.Join(", ", presents.Select(message => $"{message.ConnectionKey} {message.Json}"))}");
        // A connection that opens the route during the wait is asked after its open reply; nobody is asked twice.
        window.Transport.Request(Open("p-b"), "b");
        window.Transport.Request(Open("p-a2"), "a");
        await window.Transport.WhenPushesSentAsync();
        presents = window.Transport.TakeMessages().Where(message => message.Kind == "present").ToArray();
        Require(presents is [{ ConnectionKey: "b" }], $"The connection that opened during the wait was not asked once: {presents.Length}.");
        Require(!wait.IsCompleted, "The wait completed before a page reported.");
        Require(Parse(window.Transport.Request($$"""{"kind":"presented","route":"wizard","revision":{{revision - 1}}}""", "b")).GetProperty("ok").GetBoolean()
            && !wait.IsCompleted, "A report of an earlier revision completed the wait.");
        Require(Parse(window.Transport.Request($$"""{"kind":"presented","route":"wizard","revision":{{revision}}}""", "b")).GetProperty("ok").GetBoolean(),
            "The presented report was refused.");
        Require(await wait.WaitAsync(TimeSpan.FromSeconds(5)), "The report did not complete the wait with true.");
        // presented needs the feature and a route that is served.
        RequireError(window.Transport.Request("""{"kind":"presented","route":"wizard","revision":0}""", "old"), "rejected", "invalid-request");
        RequireError(window.Transport.Request("""{"kind":"presented","route":"elsewhere","revision":0}""", "a"), "released", null);
    }

    [Test]
    public async Task AWaitWithoutAReportIsFalseAtItsTimeoutAndACancelledWaitThrowsAsync()
    {
        var clock = new FakeTimeProvider();
        using var window = new Wizard(clock);
        window.Hello("c", Presented);
        window.Transport.Request(Open("p-1"), "c");
        var wait = RunicPresentation.WaitForPresentedAsync(window.Model, TimeSpan.FromSeconds(1)).AsTask();
        clock.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(20);
        Require(!wait.IsCompleted, "The wait ended before its timeout.");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Require(!await wait.WaitAsync(TimeSpan.FromSeconds(5)), "The wait without a report was not false at its timeout.");

        using var cancellation = new CancellationTokenSource();
        var cancelled = RunicPresentation.WaitForPresentedAsync(window.Model, Timeout.InfiniteTimeSpan, cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        Require(await Throws<OperationCanceledException>(cancelled), "A cancelled wait did not throw OperationCanceledException.");
        Require(await Throws<OperationCanceledException>(RunicPresentation.WaitForPresentedAsync(window.Model, TimeSpan.FromSeconds(1),
            new CancellationToken(canceled: true)).AsTask()), "A wait with a cancelled token did not throw.");
    }

    [Test]
    public async Task AWaitForAModelThatNoRoutePresentsCountsWithTheFirstStateOfItsFirstRouteAsync()
    {
        var model = WizardViewModel.Create(new JsonObject());
        var wait = RunicPresentation.WaitForPresentedAsync(model, TimeSpan.FromSeconds(30)).AsTask();
        using var window = new Wizard(model: model);
        window.Hello("c", Presented);
        var revision = Parse(window.Transport.Request(Open("p-1"), "c")).GetProperty("revision").GetInt64();
        using (await window.Transport.TakePushAsync("c", "present", TimeSpan.FromSeconds(5))) { }
        window.Transport.Request($$"""{"kind":"presented","route":"wizard","revision":{{revision}}}""", "c");
        Require(await wait.WaitAsync(TimeSpan.FromSeconds(5)), "The first state's report did not complete the wait.");
    }

    [Test]
    public async Task ArgumentsAreValidated()
    {
        Require(Throws<ArgumentNullException>(() => RunicPresentation.Renew(null!)), "Renew accepted null.");
        Require(await Throws<ArgumentNullException>(RunicPresentation.WaitForPresentedAsync(null!, TimeSpan.Zero).AsTask()),
            "WaitForPresentedAsync accepted null.");
        Require(await Throws<ArgumentOutOfRangeException>(RunicPresentation.WaitForPresentedAsync(new object(), TimeSpan.FromSeconds(-1)).AsTask()),
            "WaitForPresentedAsync accepted a negative timeout.");
        // A ViewModel that nothing presents: Renew does nothing, a wait ends at its timeout.
        RunicPresentation.Renew(new object());
        Require(!await RunicPresentation.WaitForPresentedAsync(new object(), TimeSpan.Zero), "A wait for nothing was true.");
    }

    [Test]
    public async Task ACloseWaitsForTheClosingReplyOfEachConnectionOrItsCloseAsync()
    {
        using var window = new Wizard();
        window.Hello("a", Presented);
        window.Hello("b", Presented);
        window.Hello("old");
        var drain = window.Session.DrainAsync(TimeSpan.FromSeconds(30)).AsTask();
        await window.Transport.WhenPushesSentAsync();
        var closings = window.Transport.TakeMessages().Where(message => message.Kind == "closing").ToArray();
        Require(closings.Select(message => message.ConnectionKey).Order().SequenceEqual(["a", "b"])
            && closings.All(message => message.Json == """{"kind":"closing"}"""),
            "closing did not reach exactly the connections that use presented/1.");
        Require(Parse(window.Transport.Request("""{"kind":"closing-reply"}""", "a")).GetProperty("ok").GetBoolean(), "closing-reply was refused.");
        Require(!drain.IsCompleted, "The close did not wait for the second page.");
        window.Transport.CloseConnection("b");
        var result = await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Require(result.PagesAcknowledged && result.Drained, "A close whose pages replied or closed was not acknowledged.");
        // Later drains reuse the result; a closing-reply without a pending closing answers ok.
        Require((await window.Session.DrainAsync(Timeout.InfiniteTimeSpan)).PagesAcknowledged, "A later drain did not reuse the result.");
        Require(Parse(window.Transport.Request("""{"kind":"closing-reply"}""", "a")).GetProperty("ok").GetBoolean(), "A second closing-reply was refused.");
        RequireError(window.Transport.Request("""{"kind":"closing-reply"}""", "old"), "rejected", "invalid-request");
    }

    [Test]
    public async Task ACloseWithoutAReplyIsUnacknowledgedAtItsTimeoutAndAZeroTimeoutDoesNotWaitAsync()
    {
        var clock = new FakeTimeProvider();
        using (var window = new Wizard(clock))
        {
            window.Hello("c", Presented);
            var drain = window.Session.DrainAsync(TimeSpan.FromSeconds(30)).AsTask();
            using (await window.Transport.TakePushAsync("c", "closing", TimeSpan.FromSeconds(5))) { }
            // The wait is capped at 5 seconds.
            clock.Advance(TimeSpan.FromMilliseconds(4999));
            await Task.Delay(20);
            Require(!drain.IsCompleted, "The close ended before its acknowledgement limit.");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Require(!(await drain.WaitAsync(TimeSpan.FromSeconds(5))).PagesAcknowledged, "An unanswered close was acknowledged.");
        }
        using (var window = new Wizard(clock))
        {
            window.Hello("c", Presented);
            var result = await window.Session.DrainAsync(TimeSpan.Zero).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Require(!result.PagesAcknowledged, "A close without a timeout waited for its page.");
            using (await window.Transport.TakePushAsync("c", "closing", TimeSpan.FromSeconds(5))) { }
        }
        using (var window = new Wizard(clock))
        {
            window.Hello("c");
            Require((await window.Session.DrainAsync(TimeSpan.Zero)).PagesAcknowledged, "A close without pages that use presented/1 was unacknowledged.");
        }
    }

    private static string Open(string presentation) => $$"""{"kind":"open","route":"wizard","presentation":"{{presentation}}"}""";

    private const string Snapshot = """{"kind":"snapshot","route":"wizard"}""";

    private static string Command(string member, long epoch) => $$"""{"kind":"command","route":"wizard","member":"{{member}}","epoch":{{epoch}}}""";

    private static string ChecksCommand(string member, string? input, long epoch) =>
        $$"""{"kind":"command","route":"checks","member":"{{member}}"{{(input is null ? "" : $",\"input\":{input}")}},"epoch":{{epoch}}}""";

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void RequireError(string json, string kind, string? reason)
    {
        var error = Parse(json).GetProperty("error");
        Require(error.GetProperty("kind").GetString() == kind
            && (reason is null || error.GetProperty("reason").GetString() == reason), $"Expected {kind}/{reason}, got {json}.");
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static async Task<bool> Throws<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); return false; }
        catch (T) { return true; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // A window whose root View is the conformance corpus's wizard.
    private sealed class Wizard : IDisposable
    {
        private readonly IDisposable _bridge;

        public Wizard(TimeProvider? clock = null, WizardViewModel? model = null)
        {
            Transport = new InMemoryBridgeTransport();
            Session = new WindowContentSession(Transport, new WindowContentSessionOptions { TimeProvider = clock, IncludeFailureDetail = false });
            BridgeDispatcher.For(Transport).MinimumUnitInterval = TimeSpan.Zero;
            Model = model ?? WizardViewModel.Create(new JsonObject());
            _bridge = RunicModelTurns.Run(Session.ModelContext!, () => new WizardBridge(Transport, Model, Session));
        }

        public InMemoryBridgeTransport Transport { get; }
        public WindowContentSession Session { get; }
        public WizardViewModel Model { get; }
        public int Page => RunicModelTurns.Run(Session.ModelContext!, () => Model.Page);

        public void Hello(string connection, params string[] features) => Transport.HelloWith(connection, features);

        public void Renew() => RunicPresentation.Renew(Model);

        public JsonElement NextState() => Transport.NextState("wizard");

        public void Dispose()
        {
            _bridge.Dispose();
            Session.Dispose();
            Transport.Dispose();
        }
    }

    // A window whose root View has scoped commands with an input, a key, an availability and an awaited body.
    private sealed class Checks : IDisposable
    {
        private IDisposable? _bridge;

        public Checks()
        {
            Transport = new InMemoryBridgeTransport();
            Session = new WindowContentSession(Transport, new WindowContentSessionOptions { IncludeFailureDetail = false });
            BridgeDispatcher.For(Transport).MinimumUnitInterval = TimeSpan.Zero;
            _bridge = RunicModelTurns.Run(Session.ModelContext!, () => new ChecksBridge(Transport, Model, Session));
        }

        public InMemoryBridgeTransport Transport { get; }
        public WindowContentSession Session { get; }
        public ChecksViewModel Model { get; } = new();

        public void Hello(string connection, params string[] features) => Transport.HelloWith(connection, features);

        public void Renew()
        {
            RunicPresentation.Renew(Model);
            Transport.NextState("checks");
        }

        public long Epoch(string connection) =>
            Parse(Transport.Request("""{"kind":"snapshot","route":"checks"}""", connection)).GetProperty("epoch").GetInt64();

        public void DisposeBridge() => Interlocked.Exchange(ref _bridge, null)?.Dispose();

        public void Dispose()
        {
            DisposeBridge();
            Session.Dispose();
            Transport.Dispose();
        }
    }
}

internal sealed class ChecksViewModel : INotifyPropertyChanged
{
    private int _runs;

    public int Runs => Volatile.Read(ref _runs);

    public ConformanceCommand PickCommand => new(_ => true, _ => Interlocked.Increment(ref _runs));
    public ConformanceCommand LockedCommand => new(_ => false, _ => Interlocked.Increment(ref _runs));
    public ConformanceCommand SlowCommand => new(_ => true, _ => { });

    internal async Task SlowAsync()
    {
        await Task.Yield();
        Interlocked.Increment(ref _runs);
    }

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

internal sealed class ChecksBridge(IBridgeTransport transport, ChecksViewModel vm, WindowContentSession content)
    : ViewModelBridge<ChecksViewModel>(transport, vm, "checks",
        [new("runs", (writer, model) => writer.WriteNumberValue(model.Runs))],
        [],
        [],
        [
            new("Pick", model => model.PickCommand)
            {
                ReadInput = value => value.GetInt32(),
                ResolveArgument = (_, key) => (int)key! == 1 ? "one" : null,
            },
            new("Locked", model => model.LockedCommand),
            new("Slow", model => model.SlowCommand) { ExecuteAsync = (model, _, _) => model.SlowAsync() },
        ],
        "presentation-checks", content);

internal static class PresentationTransportExtensions
{
    // A hello on the connection that lists the features.
    internal static void HelloWith(this InMemoryBridgeTransport transport, string connection, string[] features)
    {
        var reply = transport.Request(BridgeRequests.Json(writer =>
        {
            writer.WriteString("kind", "hello");
            writer.WriteNumber("protocol", 4);
            writer.WriteString("session", $"session-{connection}");
            writer.WriteStartObject("client");
            writer.WriteString("name", "presentation-tests");
            writer.WriteString("version", "1.0.0");
            writer.WriteEndObject();
            writer.WriteStartArray("features");
            foreach (var feature in features) writer.WriteStringValue(feature);
            writer.WriteEndArray();
        }), connection, $"client-{connection}");
        if (!reply.StartsWith("{\"ok\":true", StringComparison.Ordinal)) throw new InvalidOperationException($"The hello was refused: {reply}");
    }

    // The next state push of the route, waiting for it; the frames before it are dropped.
    internal static JsonElement NextState(this InMemoryBridgeTransport transport, string route)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var push in transport.TakePushes(route))
                using (push)
                    if (push.RootElement.GetProperty("kind").GetString() == "state") return push.RootElement.Clone();
            Thread.Sleep(5);
        }
        throw new TimeoutException($"No state push of {route} arrived.");
    }

    // Waits until the targeted pushes queued so far reached the transport.
    internal static Task WhenPushesSentAsync(this InMemoryBridgeTransport transport) =>
        BridgeTargetedPushes.For(transport).WhenSent().WaitAsync(TimeSpan.FromSeconds(5));
}
