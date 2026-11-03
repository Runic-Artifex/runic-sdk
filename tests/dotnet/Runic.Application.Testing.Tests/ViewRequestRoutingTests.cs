using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReactiveUI.Binding;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI.Generated;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// view-requests/1 (specs/application/README.md, "View requests") and the interaction scopes of A2 ("Interactions",
// Scope): delivery by scope and rule, kept requests, registration outcomes, the feature gate, the window scope and
// commands that ask another route of their window.
public sealed class ViewRequestRoutingTests
{
    private const string Route = "requests";
    private const string FocusContract = "testing.request.focus";
    private const string RevealContract = "testing.request.reveal";
    private const string ToastContract = "testing.request.toast";

    [Test]
    public async Task ACommandReachesOnlyTheInvokingPresentationAfterItsChangesAsync()
    {
        using var window = new RequestWindow();
        window.OpenAndRegister("p-1", "connection-1", "focus");
        window.OpenAndRegister("p-2", "connection-2", "focus");
        var opened = window.Revision();
        using (RunicInteractionInvocation.Enter(window.Session, Route, "client-1", "connection-1"))
        {
            await window.InTurnAsync(() => window.Model.Title = "changed");
            window.Model.Focus.Raise(new FocusTarget("title"));
        }
        var pushes = await window.RequestsAsync();
        Require(pushes.Count == 1, $"The request did not reach one presentation: {Show(pushes)}");
        var push = pushes[0];
        Require(push.Connection == "connection-1" && push.Json["presentation"]!.GetValue<string>() == "p-1"
            && push.Json["name"]!.GetValue<string>() == "focus" && push.Json["contract"]!.GetValue<string>() == FocusContract
            && push.Json["input"]!["member"]!.GetValue<string>() == "title",
            $"The request did not reach the invoking presentation: {push.Json}");
        Require(push.Json["revision"]!.GetValue<long>() > opened && push.Json["revision"]!.GetValue<long>() == window.Revision(),
            $"The request's revision does not include the change before it: {push.Json} (opened at {opened}).");
    }

    [Test]
    public async Task OutsideAScopeTheSinglePresentationReceivesItAndSeveralDropItAsync()
    {
        using var window = new RequestWindow();
        window.OpenAndRegister("p-1", "connection-1", "focus");
        window.Model.Focus.Raise(new FocusTarget("a"));
        Require((await window.RequestsAsync()).Single().Json["input"]!["member"]!.GetValue<string>() == "a",
            "The single presentation did not receive a request raised outside a scope.");

        window.OpenAndRegister("p-2", "connection-2", "focus");
        window.Model.Focus.Raise(new FocusTarget("b"));
        Require((await window.RequestsAsync()).Count == 0, "A request reached one of several presentations.");

        // The window scope selects the same way.
        window.Close("p-2", "connection-2");
        using (RunicInteractionInvocation.EnterWindow(window.Model))
            window.Model.Focus.Raise(new FocusTarget("c"));
        Require((await window.RequestsAsync()).Single().Connection == "connection-1",
            "The window scope did not reach its single presentation.");

        // A presentation without a registration, and one that unregistered, receives nothing.
        window.Register("p-1", "connection-1", 2);
        window.Model.Focus.Raise(new FocusTarget("d"));
        Require((await window.RequestsAsync()).Count == 0, "A presentation that unregistered received the request.");
    }

    [Test]
    public async Task NextMountedKeepsTheLatestRequestForTheNextRegistrationAsync()
    {
        using var window = new RequestWindow();
        window.Open("p-1", "connection-1");
        window.Model.Reveal.Raise(1);
        window.Model.Reveal.Raise(2);
        Require((await window.RequestsAsync()).Count == 0, "A kept request was pushed before a presentation registered.");
        window.Register("p-1", "connection-1", 1, "reveal");
        var pushes = await window.RequestsAsync();
        Require(pushes.Count == 1 && pushes[0].Json["input"]!.GetValue<int>() == 2,
            $"The latest kept request did not reach the next registration once: {Show(pushes)}");
        window.Register("p-1", "connection-1", 2, "reveal", "focus");
        Require((await window.RequestsAsync()).Count == 0, "A delivered request was delivered again.");

        // A request raised in a command waits for a presentation of the invoking client and connection.
        window.Register("p-1", "connection-1", 3);
        using (RunicInteractionInvocation.Enter(window.Session, Route, "client-3", "connection-3"))
            window.Model.Reveal.Raise(3);
        window.Register("p-1", "connection-1", 4, "reveal");
        Require((await window.RequestsAsync()).Count == 0, "A request kept in a command reached another connection.");
        window.OpenAndRegister("p-3", "connection-3", "reveal");
        Require((await window.RequestsAsync()).Single().Connection == "connection-3",
            "A request kept in a command did not reach the invoking connection's next registration.");
    }

    [Test]
    public async Task ARequestRaisedBeforeTheViewModelIsPresentedWaitsForItsFirstWindowAsync()
    {
        var model = new RequestModel();
        model.Reveal.Raise(7);
        using var window = new RequestWindow(model);
        window.OpenAndRegister("p-1", "connection-1", "reveal");
        Require((await window.RequestsAsync()).Single().Json["input"]!.GetValue<int>() == 7,
            "A request raised before the ViewModel was presented did not reach its first presentation.");
    }

    [Test]
    public async Task BroadcastReachesEveryRegisteredPresentationAsync()
    {
        using var window = new RequestWindow();
        window.OpenAndRegister("p-1", "connection-1", "toast");
        window.OpenAndRegister("p-2", "connection-2", "toast");
        window.Open("p-3", "connection-2");
        using (RunicInteractionInvocation.Enter(window.Session, Route, "client-1", "connection-1"))
            window.Model.Toast.Raise("Saved");
        var pushes = await window.RequestsAsync();
        Require(pushes.Select(push => push.Json["presentation"]!.GetValue<string>()).Order().SequenceEqual(["p-1", "p-2"]),
            $"The broadcast did not reach every registered presentation: {Show(pushes)}");
        window.Model.Toast.Raise("Again");
        Require((await window.RequestsAsync()).Count == 2, "A broadcast was kept or replayed.");
    }

    [Test]
    public void RegistrationAnswersOkUnsupportedStaleIgnoredAndNeedsTheFeature()
    {
        using var window = new RequestWindow();
        window.Open("p-1", "connection-1");
        Require(window.Register("p-1", "connection-1", 1, "unknown") == "unsupported", "An unknown request was not unsupported.");
        Require(window.Register("p-1", "connection-1", 2, "focus") == "ok", "A registration was not ok.");
        Require(window.Register("p-1", "connection-1", 2, "focus") == "ok", "An exact repeat was not ok.");
        Require(window.Register("p-1", "connection-1", 2, "toast") == "stale", "The same generation with other requests was not stale.");
        Require(window.Register("p-1", "connection-1", 1) == "stale", "A lower generation was not stale.");
        Require(window.Register("p-9", "connection-1", 3, "focus") == "ignored", "A presentation that is not open was not ignored.");
        Require(window.Register("p-1", "connection-2", 3, "focus") == "ignored", "Another connection's registration was not ignored.");

        // A connection without view-requests/1 cannot register; generation 0 is invalid.
        window.Hello("connection-old", "client-old", features: false);
        window.Open("p-old", "connection-old", "client-old");
        Require(window.Rejection(RequestWindow.RegisterJson("p-old", 1, "focus"), "connection-old", "client-old") == "invalid-request",
            "A connection without the feature registered view requests.");
        Require(window.Rejection(RequestWindow.RegisterJson("p-1", 0, "focus"), "connection-1") == "invalid-request",
            "Generation 0 was accepted.");
        var hello = JsonNode.Parse(window.Transport.Request(
            """{"kind":"hello","protocol":4,"session":"s-features","client":{"name":"t","version":"1"},"features":["view-requests/1"]}""",
            "connection-features", "client-features"))!;
        Require(hello["features"]!.AsArray().Any(feature => feature!.GetValue<string>() == "view-requests/1"),
            "The producer does not list view-requests/1.");
    }

    [Test]
    public async Task AnInputOverTheLimitThrowsAndNothingIsSentAsync()
    {
        using var window = new RequestWindow(maximumInputBytes: 1024);
        window.OpenAndRegister("p-1", "connection-1", "focus");
        Require(Throws<ArgumentException>(() => window.Model.Focus.Raise(new FocusTarget(new string('x', 2000)))),
            "An input over the window's limit did not throw.");
        Require((await window.RequestsAsync()).Count == 0, "An input over the limit was sent.");
    }

    [Test]
    public async Task ACommandAsksAnInteractionOfAnotherRouteOfItsConnectionAsync()
    {
        using var window = new RequestWindow();
        // The application's handler, registered first, answers when no page can.
        using var fallback = window.Model.Confirm.RegisterHandler(context => context.SetOutput(false));
        using var interaction = ReactiveInteractionDescriptor.Create<RequestModel, string, bool>("confirm", "testing.confirm",
            model => model.Confirm, static input => JsonSerializer.Serialize(input), static output => output.GetBoolean())
            .Attach(window.Session, window.Model, Route);
        window.Open("p-1", "connection-1", handlers: """[{"name":"confirm","contract":"testing.confirm"}]""");

        // The shell's command on another route of the window, on the same connection.
        Task<bool> asked;
        using (RunicInteractionInvocation.Enter(window.Session, "shell", "client-1", "connection-1"))
            asked = window.Model.Confirm.Handle("Close the note?");
        var push = await window.NextPushAsync("interaction");
        Require(push.Connection == "connection-1" && push.Json["route"]!.GetValue<string>() == Route,
            $"The interaction of another route was not asked: {push.Json}");
        window.Answer(push.Json, "true");
        Require(await asked.WaitScaledAsync(TimeSpan.FromSeconds(5)), "The answer did not complete the interaction.");

        // Another connection's command never reaches it, and falls back.
        using (RunicInteractionInvocation.Enter(window.Session, "shell", "client-2", "connection-2"))
            Require(!await window.Model.Confirm.Handle("Other?").WaitScaledAsync(TimeSpan.FromSeconds(5)),
                "Another connection's command did not fall back.");

        // The window scope asks the window's one presentation, from any connection.
        Task<bool> scoped;
        using (RunicInteractionInvocation.EnterWindow(window.Model))
            scoped = window.Model.Confirm.Handle("Reload?");
        var windowPush = await window.NextPushAsync("interaction");
        window.Answer(windowPush.Json, "true");
        Require(await scoped.WaitScaledAsync(TimeSpan.FromSeconds(5)), "The window scope did not ask the window's presentation.");
    }

    [Test]
    public async Task EnterWindowEntersNoScopeWithoutOneWindowAsync()
    {
        var model = new RequestModel();
        using (RunicInteractionInvocation.EnterWindow(model))
            Require(RunicInteractionInvocation.Current is null, "EnterWindow entered a scope for a ViewModel no window presents.");
        using var window = new RequestWindow(model);
        using (RunicInteractionInvocation.EnterWindow(model))
            Require(RunicInteractionInvocation.Current is { ClientKey: null, ConnectionKey: null } current
                && ReferenceEquals(current.Session, window.Session), "EnterWindow did not enter the window scope.");
        Require(RunicInteractionInvocation.Current is null, "The window scope outlived its using block.");
        await Task.CompletedTask;
    }

    [Test]
    public async Task AViewQuestionAsksThePageAndOtherwiseFallsBackAsync()
    {
        using var window = new RequestWindow();
        var question = new ViewQuestion<string, bool>((_, _) => ValueTask.FromResult(false));
        var unanswered = new ViewQuestion<string, bool>();
        using var attached = Views.Generated.ViewQuestionDescriptor.Create<RequestModel, string, bool>("ask", "testing.ask",
            _ => question, static input => JsonSerializer.Serialize(input), static output => output.GetBoolean())
            .Attach(window.Session, window.Model, Route);
        Require(!await question.AskAsync("No page?"), "A question without a page did not use its fallback.");
        Require(await ThrowsAsync<RunicInteractionUnhandledException>(() => unanswered.AskAsync("None?")),
            "A question without a page or a fallback did not fail as unhandled.");

        window.Open("p-1", "connection-1", handlers: """[{"name":"ask","contract":"testing.ask"}]""");
        Task<bool> asked;
        using (RunicInteractionInvocation.Enter(window.Session, Route, "client-1", "connection-1"))
            asked = question.AskAsync("Page?");
        window.Answer((await window.NextPushAsync("interaction")).Json, "true");
        Require(await asked.WaitScaledAsync(TimeSpan.FromSeconds(5)), "The page's answer did not complete the question.");

        using var cancellation = new CancellationTokenSource();
        Task<bool> cancelled;
        using (RunicInteractionInvocation.Enter(window.Session, Route, "client-1", "connection-1"))
            cancelled = question.AskAsync("Cancel?", cancellation.Token);
        _ = await window.NextPushAsync("interaction");
        cancellation.Cancel();
        Require(await ThrowsAsync<RunicInteractionCancelledException>(() => cancelled),
            "Cancelling the question's token did not cancel it.");
    }

    // A window with one route that presents a RequestModel through a Bridge written against the generated-code API.
    private sealed class RequestWindow : IDisposable
    {
        private readonly RequestBridge _bridge;
        private readonly List<(string Connection, JsonObject Json)> _pending = [];

        public RequestWindow(RequestModel? model = null, int? maximumInputBytes = null)
        {
            Model = model ?? new RequestModel();
            Session = new WindowContentSession(Transport, new WindowContentSessionOptions
            {
                IncludeFailureDetail = false,
                MaximumInputBytes = maximumInputBytes,
            });
            _bridge = new RequestBridge(Transport, Model, Session);
            _rootPresentations = Session.AttachRootInteractionPresentation(Route);
            Hello("connection-1", "client-1");
            Hello("connection-2", "client-2");
            Hello("connection-3", "client-3");
        }

        private readonly IDisposable _rootPresentations;

        public InMemoryBridgeTransport Transport { get; } = new();
        public WindowContentSession Session { get; }
        public RequestModel Model { get; }

        public void Hello(string connection, string client, bool features = true) =>
            Request(new JsonObject
            {
                ["kind"] = "hello",
                ["protocol"] = 4,
                ["session"] = $"s-{connection}",
                ["client"] = new JsonObject { ["name"] = "t", ["version"] = "1" },
                ["features"] = features ? new JsonArray("view-requests/1") : new JsonArray(),
            }, connection, client);

        public void Open(string presentation, string connection, string? client = null, string? handlers = null)
        {
            var message = new JsonObject { ["kind"] = "open", ["route"] = Route, ["presentation"] = presentation };
            if (handlers is not null) message["handlers"] = JsonNode.Parse(handlers);
            var reply = Request(message, connection, client);
            Require(reply["presentation"]?.GetValue<string>() == "ok"
                && (handlers is null || reply["registration"]?.GetValue<string>() == "ok"), $"The open was not ok: {reply}");
        }

        public void Close(string presentation, string connection) =>
            Request(new JsonObject { ["kind"] = "close", ["route"] = Route, ["presentation"] = presentation }, connection);

        public void OpenAndRegister(string presentation, string connection, params string[] requests)
        {
            Open(presentation, connection);
            Require(Register(presentation, connection, 1, requests) == "ok", "The view requests were not registered.");
        }

        public static JsonObject RegisterJson(string presentation, long generation, params string[] requests) => new()
        {
            ["kind"] = "view-requests",
            ["route"] = Route,
            ["presentation"] = presentation,
            ["generation"] = generation,
            ["requests"] = new JsonArray([.. requests.Select(name => (JsonNode)new JsonObject
            {
                ["name"] = name,
                ["contract"] = name switch { "focus" => FocusContract, "reveal" => RevealContract, "toast" => ToastContract, _ => "none" },
            })]),
        };

        public string Register(string presentation, string connection, long generation, params string[] requests) =>
            Request(RegisterJson(presentation, generation, requests), connection)["registration"]!.GetValue<string>();

        public string? Rejection(JsonObject message, string connection, string? client = null) =>
            Request(message, connection, client)["error"]?["reason"]?.GetValue<string>();

        public long Revision() => Request(new JsonObject { ["kind"] = "snapshot", ["route"] = Route }, "connection-1")["revision"]!.GetValue<long>();

        public Task InTurnAsync(Action action) => Session.ModelContext!.InvokeAsync(action).AsTask();

        // The view-request pushes since the last call, after the window handed its pushes to the transport.
        public async Task<List<(string Connection, JsonObject Json)>> RequestsAsync()
        {
            await BridgeTargetedPushes.For(Transport).WhenSent().WaitScaledAsync(TimeSpan.FromSeconds(5));
            Take();
            var requests = _pending.Where(push => push.Json["kind"]!.GetValue<string>() == "view-request").ToList();
            _pending.RemoveAll(push => push.Json["kind"]!.GetValue<string>() == "view-request");
            return requests;
        }

        public async Task<(string Connection, JsonObject Json)> NextPushAsync(string kind)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                Take();
                var index = _pending.FindIndex(push => push.Json["kind"]!.GetValue<string>() == kind);
                if (index >= 0)
                {
                    var push = _pending[index];
                    _pending.RemoveAt(index);
                    return push;
                }
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"No {kind} push.");
                await Task.Delay(10);
            }
        }

        public void Answer(JsonObject push, string output) =>
            Request(new JsonObject
            {
                ["kind"] = "interaction-reply",
                ["route"] = push["route"]!.GetValue<string>(),
                ["presentation"] = push["presentation"]!.GetValue<string>(),
                ["requestId"] = push["requestId"]!.GetValue<string>(),
                ["ownerEpoch"] = push["ownerEpoch"]!.GetValue<long>(),
                ["name"] = push["name"]!.GetValue<string>(),
                ["contract"] = push["contract"]!.GetValue<string>(),
                ["outcome"] = "answered",
                ["output"] = JsonNode.Parse(output),
            }, "connection-1");

        private void Take() => _pending.AddRange(Transport.TakeMessages().Where(message => message.ConnectionKey is not null)
            .Select(message => (message.ConnectionKey!, JsonNode.Parse(message.Json)!.AsObject())));

        private JsonObject Request(JsonObject message, string connection, string? client = null) =>
            JsonNode.Parse(Transport.Request(message.ToJsonString(), connection,
                client ?? "client-" + connection["connection-".Length..]))!.AsObject();

        public void Dispose()
        {
            _rootPresentations.Dispose();
            _bridge.Dispose();
            Session.Dispose();
            Transport.Dispose();
        }
    }

    internal sealed record FocusTarget(string Member);

    internal sealed class RequestModel : INotifyPropertyChanged
    {
        private string _title = "";

        public string Title
        {
            get => _title;
            set { _title = value; Raise(); }
        }

        public ViewRequest<FocusTarget> Focus { get; } = new();
        public ViewRequest<int> Reveal { get; } = new(ViewRequestDelivery.NextMounted);
        public ViewRequest<string> Toast { get; } = new(ViewRequestDelivery.Broadcast);
        public Interaction<string, bool> Confirm { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    }

    private sealed class RequestBridge(IBridgeTransport transport, RequestModel model, WindowContentSession session)
        : ViewModelBridge<RequestModel>(transport, model, Route,
            [new("title", (writer, value) => writer.WriteStringValue(value.Title))],
            [new("Title", "title", value => value.Title, null)], [], [], content: session,
            interactions:
            [
                Views.Generated.ViewRequestDescriptor.Create<RequestModel, FocusTarget>("focus", FocusContract, value => value.Focus,
                    static input => JsonSerializer.Serialize(new { member = input.Member })),
                Views.Generated.ViewRequestDescriptor.Create<RequestModel, int>("reveal", RevealContract, value => value.Reveal,
                    static input => JsonSerializer.Serialize(input)),
                Views.Generated.ViewRequestDescriptor.Create<RequestModel, string>("toast", ToastContract, value => value.Toast,
                    static input => JsonSerializer.Serialize(input)),
            ]);

    private static string Show(IEnumerable<(string Connection, JsonObject Json)> pushes) =>
        string.Join(", ", pushes.Select(push => $"{push.Connection}: {push.Json}"));

    private static async Task<bool> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; }
        catch (T) { return true; }
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
}
