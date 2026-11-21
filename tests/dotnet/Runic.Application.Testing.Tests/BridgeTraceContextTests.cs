using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Input;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// trace-context/1 and handler-timing/1 (specs/application/README.md, "Trace context and handler timing"): a request's W3C
// trace context parents its runic.bridge.* span, which parents the ViewModel's own spans; in Development a request that
// asks for it gets its C# handler's name and duration in the reply. Both are optional members that a side without the
// feature ignores, in both directions of version skew.
// Runs alone: it listens to the Views ActivitySource (AssemblyInfo.cs).
[NotInParallel]
internal sealed class BridgeTraceContextTests
{
    private const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string PageSpanId = "00f067aa0ba902b7";
    private const string Traceparent = $"00-{TraceId}-{PageSpanId}-01";
    private const string Connection = "traced";
    private static readonly ActivitySource ViewModelSource = new("Runic.Tests.TracedViewModel");

    [Test]
    public void HelloListsTraceContextAlwaysAndHandlerTimingOnlyInDevelopment()
    {
        foreach (var development in new[] { false, true })
        {
            using var window = new Window(development);
            var features = Features(window.Transport.Request(Hello("trace-context/1", "handler-timing/1"), Connection));
            Require(features.Contains("trace-context/1"), $"trace-context/1 was not listed: {string.Join(", ", features)}");
            Require(features.Contains("handler-timing/1") == development,
                $"handler-timing/1 listed {features.Contains("handler-timing/1")} with development {development}.");
        }
    }

    [Test]
    public void DevelopmentFollowsTheHostEnvironment()
    {
        Require(BridgeFailureDetails.IsDevelopment(name => name == "DOTNET_ENVIRONMENT" ? "Development" : null), "DOTNET_ENVIRONMENT=Development was not Development.");
        Require(BridgeFailureDetails.IsDevelopment(name => name == "ASPNETCORE_ENVIRONMENT" ? "development" : null), "ASPNETCORE_ENVIRONMENT=development was not Development.");
        Require(!BridgeFailureDetails.IsDevelopment(name => name == "DOTNET_ENVIRONMENT" ? "Production" : "Development"), "DOTNET_ENVIRONMENT=Production was Development.");
        Require(!BridgeFailureDetails.IsDevelopment(_ => null), "An unset environment was Development.");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TraceparentParentsTheBridgeSpanAndTheViewModelSpansAsync(bool awaited)
    {
        using var telemetry = new SpanCapture();
        using var window = new Window(development: false);
        window.Transport.Request(Hello("trace-context/1"), Connection);
        var member = awaited ? "saveAsync" : "save";

        var reply = await window.Transport.RequestAsync(
            $$$"""{"kind":"command","route":"traced","member":"{{{member}}}","trace":{"traceparent":"{{{Traceparent}}}","tracestate":"runic=1"}}""", Connection);
        Require(reply.Contains("\"ok\":true", StringComparison.Ordinal), $"The command failed: {reply}");
        Require(!reply.Contains("handler", StringComparison.Ordinal), $"A reply outside Development carried handler timing: {reply}");

        var bridge = telemetry.Single("runic.bridge.command");
        Require(bridge.TraceId.ToHexString() == TraceId && bridge.ParentSpanId.ToHexString() == PageSpanId,
            $"The Bridge span was not the page span's child: {bridge.TraceId}/{bridge.ParentSpanId}.");
        Require(bridge.Kind == ActivityKind.Server && bridge.HasRemoteParent && bridge.TraceStateString == "runic=1",
            $"The Bridge span had kind {bridge.Kind}, remote parent {bridge.HasRemoteParent}, tracestate {bridge.TraceStateString}.");
        var own = telemetry.Single("TracedViewModel.Save");
        Require(own.TraceId == bridge.TraceId && own.ParentSpanId == bridge.SpanId,
            $"The ViewModel's span was not the Bridge span's child: {own.TraceId}/{own.ParentSpanId}, Bridge {bridge.SpanId}.");
    }

    [Test]
    public async Task WithoutAListenerNoParentIsReadAsync()
    {
        using var window = new Window(development: false);
        window.Transport.Request(Hello("trace-context/1"), Connection);
        var reply = await window.Transport.RequestAsync(
            $$$"""{"kind":"command","route":"traced","member":"save","trace":{"traceparent":"{{{Traceparent}}}"}}""", Connection);
        Require(reply.Contains("\"ok\":true", StringComparison.Ordinal), $"The command failed: {reply}");
        // Read only while the ActivitySource has a listener: nothing to parent.
        using var document = JsonDocument.Parse($$$"""{"trace":{"traceparent":"{{{Traceparent}}}"}}""");
        Require(BridgeRequestTrace.TryReadParent(document.RootElement, out var parent) && parent.TraceId.ToHexString() == TraceId,
            "A valid traceparent did not parse.");
    }

    [Test]
    [Arguments("""{"traceparent":"not a traceparent"}""")]
    [Arguments("""{"traceparent":"00-00000000000000000000000000000000-00f067aa0ba902b7-01"}""")]
    [Arguments("""{"traceparent":7}""")]
    [Arguments("\"00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01\"")]
    [Arguments("null")]
    public async Task AnInvalidTraceContextIsIgnoredAsync(string trace)
    {
        using var telemetry = new SpanCapture();
        using var window = new Window(development: false);
        window.Transport.Request(Hello("trace-context/1"), Connection);
        var reply = await window.Transport.RequestAsync($$$"""{"kind":"command","route":"traced","member":"save","trace":{{{trace}}}}""", Connection);
        Require(reply.Contains("\"ok\":true", StringComparison.Ordinal), $"An invalid trace context failed the command: {reply}");
        var bridge = telemetry.Single("runic.bridge.command");
        Require(!bridge.HasRemoteParent && bridge.Kind == ActivityKind.Internal, $"An invalid trace context parented the span: {bridge.ParentId}.");
    }

    [Test]
    public async Task AConnectionWithoutTheFeatureIgnoresTheTraceContextAsync()
    {
        using var telemetry = new SpanCapture();
        using var window = new Window(development: true);
        // An older page lists neither feature; a newer one that sends the members anyway is ignored as well.
        window.Transport.Request(Hello(), Connection);
        var reply = await window.Transport.RequestAsync(
            $$"""{"kind":"command","route":"traced","member":"save","trace":{"traceparent":"{{Traceparent}}"},"timing":true}""", Connection);
        Require(reply.Contains("\"ok\":true", StringComparison.Ordinal) && !reply.Contains("handler", StringComparison.Ordinal),
            $"A connection without the features got {reply}.");
        Require(!telemetry.Single("runic.bridge.command").HasRemoteParent, "A connection without trace-context/1 parented the span.");
    }

    [Test]
    [Arguments("save")]
    [Arguments("saveAsync")]
    public async Task HandlerTimingIsInTheReplyOnlyInDevelopmentAndWhenAskedAsync(string member)
    {
        using (var window = new Window(development: true))
        {
            window.Transport.Request(Hello("handler-timing/1"), Connection);
            var reply = JsonDocument.Parse(await window.Transport.RequestAsync(
                $$"""{"kind":"command","route":"traced","member":"{{member}}","timing":true}""", Connection)).RootElement;
            Require(reply.GetProperty("ok").GetBoolean() && reply.TryGetProperty("revision", out _), $"The command failed: {reply}");
            var handler = reply.GetProperty("handler");
            var expected = member == "save" ? "TracedViewModel.Save" : "TracedViewModel.SaveAsync";
            Require(handler.GetProperty("name").GetString() == expected, $"The handler was named {handler}.");
            // The command sleeps at least 20 ms.
            Require(handler.GetProperty("duration").GetDouble() is >= 15 and < 10_000, $"The handler took {handler}.");

            var unasked = await window.Transport.RequestAsync($$"""{"kind":"command","route":"traced","member":"{{member}}"}""", Connection);
            Require(!unasked.Contains("handler", StringComparison.Ordinal), $"A request that did not ask got {unasked}.");
            var set = JsonDocument.Parse(await window.Transport.RequestAsync(
                """{"kind":"set","route":"traced","member":"count","value":3,"edit":1,"timing":true}""", Connection)).RootElement;
            Require(set.GetProperty("handler").GetProperty("name").GetString() == "TracedViewModel.count",
                $"A setter's reply named {set}.");
            // A request kind without a Bridge call (can) has no handler to name.
            var can = await window.Transport.RequestAsync("""{"kind":"can","route":"traced","member":"save","timing":true}""", Connection);
            Require(can.Contains("\"ok\":true", StringComparison.Ordinal) && !can.Contains("handler", StringComparison.Ordinal), $"can was answered {can}.");
        }
        using (var production = new Window(development: false))
        {
            // Outside Development the producer does not offer the feature, so a client that asks gets nothing.
            production.Transport.Request(Hello("handler-timing/1"), Connection);
            var reply = await production.Transport.RequestAsync($$"""{"kind":"command","route":"traced","member":"{{member}}","timing":true}""", Connection);
            Require(reply.Contains("\"ok\":true", StringComparison.Ordinal) && !reply.Contains("handler", StringComparison.Ordinal),
                $"A production reply carried handler timing: {reply}");
        }
    }

    [Test]
    public void AHandlerIsAddedToAnyReplyObject()
    {
        foreach (var (reply, expected) in new[]
        {
            ("""{"ok":true,"revision":4}""", """{"ok":true,"revision":4,"handler":{"name":"M.\u0022x\u0022","duration":1.235}}"""),
            ("{}", """{"handler":{"name":"M.\u0022x\u0022","duration":1.235}}"""),
        })
        {
            var trace = RequestTrace();
            trace.RecordHandler("M.\"x\"", 1.23456);
            var written = System.Text.Encoding.UTF8.GetString(trace.AddHandler(System.Text.Encoding.UTF8.GetBytes(reply)).Span);
            Require(written == expected, $"{reply} became {written}.");
            using var _ = JsonDocument.Parse(written);
        }
        Require(RequestTrace().AddHandler("""{"ok":true}"""u8.ToArray()).Length == """{"ok":true}""".Length, "A reply without a recorded handler changed.");
    }

    // A context that asks for timing, as BridgeDispatcher creates for a request on a Development connection.
    private static BridgeRequestTrace RequestTrace()
    {
        var connection = new BridgeConnection("c", "c") { UsesHandlerTiming = true };
        using var document = JsonDocument.Parse("""{"timing":true}""");
        return BridgeRequestTrace.Read(connection, document.RootElement)!;
    }

    private static string Hello(params string[] features) =>
        $$"""{"kind":"hello","protocol":4,"session":"traced-session","client":{"name":"test","version":"1"},"features":[{{string.Join(",", features.Select(feature => $"\"{feature}\""))}}]}""";

    private static HashSet<string> Features(string reply)
    {
        using var document = JsonDocument.Parse(reply);
        return [.. document.RootElement.GetProperty("features").EnumerateArray().Select(feature => feature.GetString()!)];
    }

    private sealed class Window : IDisposable
    {
        public Window(bool development)
        {
            Model = new TracedViewModel();
            Transport = new InMemoryBridgeTransport();
            Session = new WindowContentSession(Transport, new WindowContentSessionOptions { RootModel = Model });
            BridgeDispatcher.For(Transport).HandlerTiming = development;
            Bridge = new TracedBridge(Transport, Model, Session);
        }

        public TracedViewModel Model { get; }
        public InMemoryBridgeTransport Transport { get; }
        public WindowContentSession Session { get; }
        public TracedBridge Bridge { get; }

        public void Dispose()
        {
            Bridge.Dispose();
            Session.Dispose();
            Transport.Dispose();
        }
    }

    internal sealed class TracedViewModel : INotifyPropertyChanged
    {
        private int _count;

        public TracedViewModel() => Save = new DelegateCommand(() =>
        {
            using var span = ViewModelSource.StartActivity("TracedViewModel.Save");
            Thread.Sleep(20);
        });

        public ICommand Save { get; }

        public int Count
        {
            get => _count;
            set { _count = value; PropertyChanged?.Invoke(this, new(nameof(Count))); }
        }

        // An awaited command: its span starts after an await that leaves the model turn.
        public async Task SaveAsync()
        {
            await Task.Delay(20).ConfigureAwait(false);
            using var span = ViewModelSource.StartActivity("TracedViewModel.Save");
            Saved++;
        }

        public int Saved { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    internal sealed class TracedBridge(IBridgeTransport transport, TracedViewModel model, WindowContentSession content)
        : ViewModelBridge(transport, model, typeof(TracedViewModel), "traced",
            [new BridgeStateMember("count", static (writer, vm) => writer.WriteNumberValue(((TracedViewModel)vm).Count))],
            [new("count", "count", vm => ((TracedViewModel)vm).Count, (vm, value) => ((TracedViewModel)vm).Count = BridgeWire.ReadInt32(value))], [],
            [
                new("Save", vm => ((TracedViewModel)vm).Save),
                new("SaveAsync", vm => ((TracedViewModel)vm).Save) { ExecuteAsync = static (vm, _, _) => ((TracedViewModel)vm).SaveAsync() },
            ],
            contractFingerprint: "traced", content: content);

    private sealed class DelegateCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }

    // Records the spans of the Views ActivitySource and of the test ViewModel's.
    private sealed class SpanCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly ConcurrentQueue<Activity> _stopped = new();

        public SpanCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name is RunicViewsTelemetry.ActivitySourceName or "Runic.Tests.TracedViewModel",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = _stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public Activity Single(string operationName)
        {
            // Other suites' Bridges run in parallel: only this suite's route and ViewModel count.
            var matches = _stopped.Where(activity => activity.OperationName == operationName
                && (activity.Source == ViewModelSource || activity.GetTagItem("runic.bridge.route") as string == "traced")).ToArray();
            return matches.Length == 1 ? matches[0]
                : throw new InvalidOperationException($"{matches.Length} {operationName} spans were recorded: {string.Join(", ", _stopped.Select(activity => activity.OperationName))}.");
        }

        public void Dispose() => _listener.Dispose();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
