using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Windows.Input;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Views protocol 4 operations of a window (specs/application/README.md, "Operations"): start, status, watch with
// throttled progress, the terminal status with its revision, cancel, stream, identity and window close. The
// conformance scenarios cover the same rules with the test Views; these cases also cover what the scenarios do not.
public sealed class OperationProtocolTests
{
    private const string Route = "importer";

    [Test]
    public async Task WatchPushesThrottledProgressAndTheTerminalStatusAsync()
    {
        using var window = new OperationWindow();
        var start = window.Start("op-1", "\"a\"");
        Require(start["ok"]!.GetValue<bool>() && start["admission"]!.GetValue<string>() == "accepted"
            && start["status"]!["kind"]!.GetValue<string>() == "running" && start["revision"] is not null,
            $"The start was not accepted with a running status and a revision: {start}");
        var watch = window.Request(OperationWindow.Operation("watch", "op-1"));
        Require(watch["status"]!["kind"]!.GetValue<string>() == "running" && watch["status"]!["progress"] is null,
            $"The watch did not answer the running status without progress: {watch}");

        await window.Model.StepAsync(0.25);
        var first = await window.PushesAsync();
        Require(first.Count == 1 && first[0]["kind"]!.GetValue<string>() == "operation"
            && first[0]["route"]!.GetValue<string>() == Route && first[0]["member"]!.GetValue<string>() == "import"
            && first[0]["operation"]!.GetValue<string>() == "op-1" && first[0]["contract"]!.GetValue<string>() == OperationWindow.Contract
            && first[0]["status"]!["progress"]!.GetValue<double>() == 0.25,
            $"The first progress was not pushed at once: {Show(first)}");

        await window.Model.StepAsync(0.5);
        Require((await window.PushesAsync()).Count == 0, "A progress within 100 ms of the previous push was pushed at once.");
        window.Clock.Advance(TimeSpan.FromMilliseconds(99));
        Require((await window.PushesAsync()).Count == 0, "The pending progress was pushed before 100 ms had passed.");
        window.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var second = await window.PushesAsync();
        Require(second.Count == 1 && second[0]["status"]!["progress"]!.GetValue<double>() == 0.5,
            $"The latest progress was not pushed 100 ms after the previous push: {Show(second)}");

        // A terminal status replaces a pending progress push.
        await window.Model.StepAsync(0.75);
        await window.Model.StepAsync(1.5);
        Require((await window.PushesAsync()).Count == 0, "A clamped progress within 100 ms was pushed at once.");
        await window.Model.FinishAsync(2);
        var terminal = await window.PushesUntilTerminalAsync();
        Require(terminal.Count == 1 && terminal[0]["status"]!["kind"]!.GetValue<string>() == "succeeded"
            && terminal[0]["status"]!["result"]!.GetValue<int>() == 2 && terminal[0]["status"]!["revision"] is not null
            && terminal[0]["status"]!["progress"] is null,
            $"The terminal status was not the watch's last push, with its result and revision: {Show(terminal)}");
        window.Clock.Advance(TimeSpan.FromSeconds(1));
        Require((await window.PushesAsync()).Count == 0, "A watch pushed after the terminal status.");

        var status = window.Request(OperationWindow.Operation("status", "op-1"));
        Require(status["status"]!["kind"]!.GetValue<string>() == "succeeded"
            && status["status"]!["revision"]!.GetValue<long>() == terminal[0]["status"]!["revision"]!.GetValue<long>(),
            $"The status did not repeat the terminal status: {status}");
    }

    [Test]
    public async Task StartIsKeyedByContractRouteAndOperationAsync()
    {
        using var window = new OperationWindow();
        Require(window.Start("op-1", """{"a":1.0,"b":"x"}""")["admission"]!.GetValue<string>() == "accepted",
            "The first start was not accepted.");
        var duplicate = window.Start("op-1", """{"b":"x","a":1}""");
        Require(duplicate["admission"]!.GetValue<string>() == "duplicate" && duplicate["status"]!["kind"]!.GetValue<string>() == "running"
            && duplicate["reason"] is null && duplicate["revision"] is not null,
            $"A canonically equal start was not a duplicate: {duplicate}");
        var conflict = window.Start("op-1", """{"b":"y","a":1}""");
        Require(conflict["admission"]!.GetValue<string>() == "rejected" && conflict["reason"]!.GetValue<string>() == "identity-conflict"
            && conflict["status"] is null,
            $"Another input under a known key was not an identity conflict: {conflict}");
        var otherMember = window.Request(OperationWindow.Operation("start", "op-1", member: "export", input: "\"a\""));
        Require(otherMember["reason"]?.GetValue<string>() == "identity-conflict",
            $"Another member under a known key was not an identity conflict: {otherMember}");
        Require(window.Request(OperationWindow.Operation("status", "op-1", member: "export"))["status"]!["kind"]!.GetValue<string>() == "unknown",
            "Another member observed the operation.");
        // The second start of a command that is not available while the first runs.
        var second = window.Start("op-2", "\"b\"");
        Require(second["admission"]!.GetValue<string>() == "rejected" && second["reason"]!.GetValue<string>() == "unavailable"
            && second["revision"] is not null,
            $"A start of an unavailable command was not rejected as unavailable: {second}");
        await window.Model.FinishAsync(1);
    }

    [Test]
    public void StartChecksItsInputAndContract()
    {
        using var window = new OperationWindow();
        Require(Error(window.Request(OperationWindow.Operation("start", "op-1"))) == ("rejected", "invalid-request"),
            "A start without the command's input was not invalid-request.");
        Require(Error(window.Request(OperationWindow.Operation("start", "op-1", input: "\"a\"", contract: "Other.View:abc"))) == ("rejected", "invalid-request"),
            "A start with another build's contract was not refused.");
        var invalid = window.Start("op-1", "42");
        Require(invalid["admission"]!.GetValue<string>() == "rejected" && invalid["reason"]!.GetValue<string>() == "invalid-input",
            $"An input that does not decode was not rejected with invalid-input: {invalid}");
        Require(Error(window.Request(OperationWindow.Operation("status", "op-1", member: "title"))) == ("rejected", "invalid-request"),
            "A status for a member that is not an operation was not invalid-request.");
        var unknown = window.Request(OperationWindow.Operation("status", "op-9"));
        Require(unknown["status"]!.ToJsonString() == """{"kind":"unknown"}""", $"An unknown operation was not unknown: {unknown}");
        Require(window.Request(OperationWindow.Operation("cancel", "op-9"))["cancellation"]!.GetValue<string>() == "unknown",
            "Cancelling an unknown operation did not answer unknown.");
        var page = window.Request(OperationWindow.Operation("stream", "op-9", cursor: 0));
        Require(page["page"]!["kind"]!.GetValue<string>() == "unknown" && page["page"]!["items"]!.AsArray().Count == 0,
            $"The stream page of an unknown operation was not empty: {page}");
    }

    [Test]
    public async Task CancelRequestsCancellationAndTheOperationEndsCancelledAsync()
    {
        using var window = new OperationWindow();
        window.Start("op-1", "\"a\"");
        window.Request(OperationWindow.Operation("watch", "op-1"));
        Require(window.Request(OperationWindow.Operation("cancel", "op-1"))["cancellation"]!.GetValue<string>() == "cancellation-requested",
            "Cancel did not request cancellation.");
        var pushes = await window.PushesUntilTerminalAsync();
        Require(pushes[^1]["status"]!["kind"]!.GetValue<string>() == "cancelled" && pushes[^1]["status"]!["revision"] is not null,
            $"The cancelled operation's terminal status was not pushed with its revision: {Show(pushes)}");
        Require(window.Request(OperationWindow.Operation("cancel", "op-1"))["cancellation"]!.GetValue<string>() == "not-running",
            "Cancelling a terminal operation did not answer not-running.");
    }

    [Test]
    public async Task UnwatchAndAClosedConnectionEndTheWatchAsync()
    {
        using var window = new OperationWindow();
        window.Start("op-1", "\"a\"");
        window.Request(OperationWindow.Operation("watch", "op-1"));
        window.Request(OperationWindow.Operation("watch", "op-1"), "connection-2", "client-2");
        window.Request(OperationWindow.Operation("watch", "op-1"), "connection-3", "client-3");
        Require(window.Request(OperationWindow.Operation("unwatch", "op-1"))["status"]!["kind"]!.GetValue<string>() == "running",
            "Unwatch did not answer the status.");
        window.Transport.CloseConnection("connection-3");
        await window.Model.FinishAsync(3);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (window.Transport.Messages.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await BridgeTargetedPushes.For(window.Transport).WhenSent();
        Require(window.Transport.Messages.Count == 1 && window.Transport.Messages[0].ConnectionKey == "connection-2",
            $"Only the connection that still watched should receive the terminal status: {string.Join("\n", window.Transport.Messages)}");
    }

    [Test]
    public void AConnectionHoldsAtMost64Watches()
    {
        using var transport = new InMemoryBridgeTransport();
        using var watches = new BridgeOperationWatches(transport, new FakeTimeProvider());
        var running = new BridgeOperationStatus("x", BridgeOperationStatusKind.Running, null);
        for (var index = 0; index < BridgeOperationWatches.MaximumWatchesPerConnection; index++)
            Require(watches.Add(BridgeOperationIdentity.Create("V:fp", Route, $"op-{index}"), "import", "c", () => running) is not null,
                $"Watch {index + 1} was refused.");
        Require(watches.Add(BridgeOperationIdentity.Create("V:fp", Route, "op-0"), "import", "c", () => running) is not null,
            "Watching an operation the connection already watches was refused at capacity.");
        Require(watches.Add(BridgeOperationIdentity.Create("V:fp", Route, "op-64"), "import", "c", () => running) is null,
            "The 65th watch of a connection was added.");
        Require(watches.Add(BridgeOperationIdentity.Create("V:fp", Route, "op-64"), "import", "other", () => running) is not null,
            "Another connection's watch counted against the first connection.");
        watches.ReleaseConnection("c");
        Require(watches.Add(BridgeOperationIdentity.Create("V:fp", Route, "op-65"), "import", "c", () => running) is not null,
            "A closed connection's watches still counted.");
    }

    [Test]
    public async Task ProgressOutsideAnOperationDoesNothingAsync()
    {
        RunicOperationProgress.Report(0.5);
        RunicOperationProgress.Report(double.NaN);
        using var window = new OperationWindow();
        window.Start("op-1", "\"a\"");
        window.Request(OperationWindow.Operation("watch", "op-1"));
        await window.Model.StepAsync(double.NaN);
        Require((await window.PushesAsync()).Count == 0, "A NaN progress was pushed.");
        await window.Model.StepAsync(-1);
        var pushes = await window.PushesAsync();
        Require(pushes.Count == 1 && pushes[0]["status"]!["progress"]!.GetValue<double>() == 0,
            $"A negative progress was not clamped to 0: {Show(pushes)}");
        await window.Model.FinishAsync(0);
    }

    // Window close (specs/application/README.md, "Limits and window close"): a closing window stops admission, waits
    // for running operations and then cancels what remains; a watching page receives their terminal statuses.
    [Test]
    public async Task ClosingWindowRefusesStartsAndDrainsOperationsAsync()
    {
        using var window = new OperationWindow();
        window.Start("op-1", "\"a\"");
        window.Request(OperationWindow.Operation("watch", "op-1"));
        var close = window.Session.DrainAsync(TimeSpan.FromSeconds(10)).AsTask();
        await Task.Delay(50);
        Require(!close.IsCompleted, "Close did not wait for the running operation.");
        var refused = window.Start("op-2", "\"b\"");
        Require(refused["admission"]!.GetValue<string>() == "rejected" && refused["reason"]!.GetValue<string>() == "owner-closing",
            $"A closing window admitted a start: {refused}");
        var duplicate = window.Start("op-1", "\"a\"");
        Require(duplicate["admission"]!.GetValue<string>() == "rejected" && duplicate["reason"]!.GetValue<string>() == "owner-closing",
            $"A closing window answered a known start other than owner-closing: {duplicate}");
        await window.Model.FinishAsync(1);
        var result = await close.WaitAsync(TimeSpan.FromSeconds(5));
        Require(result.Drained && result.RemainingOperations == 0, "Close did not report the drained operation.");
        var pushes = await window.PushesUntilTerminalAsync();
        Require(pushes[^1]["status"]!["kind"]!.GetValue<string>() == "succeeded", "The drained operation's terminal was not pushed.");
    }

    [Test]
    public async Task CloseTimeoutCancelsTheRemainingOperationsAsync()
    {
        using var window = new OperationWindow();
        window.Start("op-1", "\"a\"");
        window.Request(OperationWindow.Operation("watch", "op-1"));
        var close = window.Session.DrainAsync(TimeSpan.FromSeconds(10)).AsTask();
        await Task.Delay(50);
        window.Clock.Advance(TimeSpan.FromSeconds(10));
        var result = await close.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!result.Drained && result.RemainingOperations == 1, "Close did not count the running operation at its timeout.");
        await result.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var pushes = await window.PushesUntilTerminalAsync();
        Require(pushes[^1]["status"]!["kind"]!.GetValue<string>() == "cancelled",
            $"The close timeout did not cancel the operation: {Show(pushes)}");
    }

    private static (string? Kind, string? Reason) Error(JsonObject reply) =>
        reply["ok"]!.GetValue<bool>() ? (null, null)
            : (reply["error"]!["kind"]?.GetValue<string>(), reply["error"]!["reason"]?.GetValue<string>());

    private static string Show(IEnumerable<JsonObject> messages) => string.Join("\n", messages.Select(message => message.ToJsonString()));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // A window whose root View `importer` has the asynchronous commands `import` (input string, result int32) and
    // `export` (input string), served on an in-memory transport with a virtual clock, and one connection after hello.
    private sealed class OperationWindow : IDisposable
    {
        public OperationWindow()
        {
            Session = new WindowContentSession(Transport, new WindowContentSessionOptions
            {
                RootModel = Model,
                TimeProvider = Clock,
                IncludeFailureDetail = false,
            });
            Bridge = new ImportBridge(Transport, Model, Session);
            Lease = OperationRoutes.Serve(Transport, Route, Bridge);
            Hello();
        }

        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
        public InMemoryBridgeTransport Transport { get; } = new();
        public ImportModel Model { get; } = new();
        public WindowContentSession Session { get; }
        private ImportBridge Bridge { get; }
        private IDisposable? Lease { get; }
        public static string Contract => $"{typeof(ImportModel).FullName}:{ImportBridge.Fingerprint}";

        public void Hello(string connection = InMemoryBridgeTransport.DefaultConnectionKey, string? client = null) =>
            Require(JsonNode.Parse(Transport.Request(new JsonObject
            {
                ["kind"] = "hello",
                ["protocol"] = 4,
                ["session"] = $"s-{connection}",
                ["client"] = new JsonObject { ["name"] = "t", ["version"] = "1" },
            }.ToJsonString(), connection, client))!["ok"]!.GetValue<bool>(), "The hello failed.");

        public JsonObject Request(string json, string connection = InMemoryBridgeTransport.DefaultConnectionKey, string? client = null)
        {
            if (connection != InMemoryBridgeTransport.DefaultConnectionKey && !_hello.Contains(connection))
            {
                _hello.Add(connection);
                Hello(connection, client);
            }
            return JsonNode.Parse(Transport.Request(json, connection, client))!.AsObject();
        }

        private readonly HashSet<string> _hello = [];

        public JsonObject Start(string operation, string input) => Request(Operation("start", operation, input: input));

        public static string Operation(string kind, string operation, string member = "import", string? input = null,
            string? contract = null, long? cursor = null)
        {
            var message = new JsonObject
            {
                ["kind"] = kind,
                ["route"] = Route,
                ["member"] = member,
                ["operation"] = operation,
                ["contract"] = contract ?? Contract,
            };
            if (input is not null) message["input"] = JsonNode.Parse(input);
            if (cursor is not null) message["cursor"] = cursor;
            return message.ToJsonString();
        }

        // The targeted pushes sent to a connection since the last call, once every queued push was sent.
        public async Task<List<JsonObject>> PushesAsync(string connection = InMemoryBridgeTransport.DefaultConnectionKey)
        {
            await BridgeTargetedPushes.For(Transport).WhenSent().WaitAsync(TimeSpan.FromSeconds(5));
            return [.. Transport.TakeMessages().Where(message => message.ConnectionKey == connection)
                .Select(message => JsonNode.Parse(message.Json)!.AsObject())];
        }

        // The targeted pushes until the operation's terminal status, which ends the list.
        public async Task<List<JsonObject>> PushesUntilTerminalAsync(string connection = InMemoryBridgeTransport.DefaultConnectionKey)
        {
            List<JsonObject> pushes = [];
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                pushes.AddRange(await PushesAsync(connection));
                if (pushes.Count > 0 && pushes[^1]["status"]!["kind"]!.GetValue<string>() != "running") return pushes;
                await Task.Delay(10);
            }
            throw new InvalidOperationException($"No terminal status was pushed: {Show(pushes)}");
        }

        public void Dispose()
        {
            Lease?.Dispose();
            Bridge.Dispose();
            Session.Dispose();
            Transport.Dispose();
        }
    }

    internal sealed class ImportModel : INotifyPropertyChanged
    {
        private readonly Channel<double?> _steps = Channel.CreateUnbounded<double?>();
        private readonly Channel<bool> _processed = Channel.CreateUnbounded<bool>();
        private int _result;

        public ICommand Import { get; } = new NoopCommand();
        public ICommand Export { get; } = new NoopCommand();
        public bool Running { get; private set; }

        // Reports a progress (or ends the operation with null) from inside the running command, and waits until it did.
        public async Task StepAsync(double? progress)
        {
            await _steps.Writer.WriteAsync(progress);
            await _processed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }

        public Task FinishAsync(int result)
        {
            _result = result;
            return StepAsync(null);
        }

        public async Task<BridgeOperationResult> ImportAsync(string source, CancellationToken cancellationToken)
        {
            Running = true;
            try
            {
                while (true)
                {
                    var step = await _steps.Reader.ReadAsync(cancellationToken);
                    if (step is not { } progress)
                    {
                        _processed.Writer.TryWrite(true);
                        return BridgeOperationResult.Succeeded(_result.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    RunicOperationProgress.Report(progress);
                    _processed.Writer.TryWrite(true);
                }
            }
            finally { Running = false; }
        }

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    private sealed class ImportBridge(IBridgeTransport transport, ImportModel model, WindowContentSession content)
        : ViewModelBridge<ImportModel>(transport, model, Route, static (writer, _, revision, _) =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("revision", revision);
            writer.WriteEndObject();
        }, [], [],
        [
            new("Import", vm => vm.Import)
            {
                WireName = "import",
                ReadInput = static input => input.ValueKind switch
                {
                    JsonValueKind.String => input.GetString(),
                    JsonValueKind.Object => input.GetRawText(),
                    _ => throw new FormatException("A string or an object."),
                },
                ExecuteResultAsync = static (vm, cancellation, input) => vm.ImportAsync((string)input!, cancellation),
                CanExecute = static (vm, _) => !vm.Running,
            },
            new("Export", vm => vm.Export)
            {
                WireName = "export",
                ReadInput = static input => input.GetString(),
                ExecuteAsync = static (_, cancellation, _) => Task.Delay(Timeout.InfiniteTimeSpan, cancellation),
                CanExecute = static (_, _) => true,
            },
        ],
        contractFingerprint: Fingerprint, content: content)
    {
        public const string Fingerprint = "fp-1";
    }

    private sealed class NoopCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}

// Serves a Bridge's operation and interaction requests on its route, as the Bridge's route handler does. Until the
// route handler of the producer (P4) serves View routes, the tests register this one; once it does, the route is
// already served and Serve returns null.
internal static class OperationRoutes
{
    internal static IDisposable? Serve<TModel>(IBridgeTransport transport, string route, ViewModelBridge<TModel> bridge)
        where TModel : INotifyPropertyChanged
    {
        var handle = typeof(ViewModelBridge<TModel>).GetMethod("HandleOperationAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("ViewModelBridge.HandleOperationAsync");
        try { return BridgeDispatcher.For(transport).RegisterRoute(route, new Handler(request => (ValueTask<ReadOnlyMemory<byte>>)handle.Invoke(bridge, [request, CancellationToken.None])!)); }
        catch (InvalidOperationException) { return null; }
    }

    private sealed class Handler(Func<BridgeRouteRequest, ValueTask<ReadOnlyMemory<byte>>> handle) : IBridgeRouteHandler
    {
        public ValueTask<ReadOnlyMemory<byte>> HandleAsync(BridgeRouteRequest request, CancellationToken cancellationToken) => handle(request);
    }
}
