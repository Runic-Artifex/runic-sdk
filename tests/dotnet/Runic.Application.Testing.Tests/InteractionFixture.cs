using System.Text.Json;
using ReactiveUI.Binding;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Application.Views.ReactiveUI.Generated;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class InteractionFixture
{
    [Test]
    public async Task InteractionsReachMountedBrowsersAndFallBackAsync()
    {
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport);
        var model = new InteractionModel();
        var route = "interaction";
        // The route is served, so that open answers; the test attaches the interaction itself.
        using var bridge = new InteractionBridge(transport, model, route, session);
        // Register an ordinary application handler first. The adapter handler
        // is later and returns without output when no browser is eligible.
        using var fallback = model.ConfirmDelete.RegisterHandler(context => context.SetOutput(true));
        using var descriptor = ReactiveInteractionDescriptor.Create<InteractionModel, string, bool>(
            "confirmDelete", "testing.confirmDelete.v1", value => value.ConfirmDelete,
            static value => JsonSerializer.Serialize(value), static value => value.GetBoolean()).Attach(session, model, route);
        Require(Throws<InvalidOperationException>(() =>
            ReactiveInteractionDescriptor.Create<InteractionModel, string, bool>("confirmDeleteAgain",
                "testing.confirmDeleteAgain.v1", value => value.ConfirmDelete,
                static value => JsonSerializer.Serialize(value), static value => value.GetBoolean()).Attach(session, model, route)),
            "Exposing one Interaction instance through two generated members did not fail clearly.");
        using var rootMount = session.AttachRootInteractionPresentation(route);

        // A normal application handler remains the fallback when no mounted
        // browser is actively waiting.
        using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-a"))
            Require(await model.ConfirmDelete.Handle("headless"),
                "The browser adapter did not fall through to the .NET interaction handler.");

        using (var alreadyCancelled = new CancellationTokenSource())
        {
            alreadyCancelled.Cancel();
            Task<bool> cancelledBeforeAdmission;
            using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-a",
                cancellationToken: alreadyCancelled.Token))
                cancelledBeforeAdmission = model.ConfirmDelete.Handle("cancelled-before-admission");
            Require(await ThrowsAsync<OperationCanceledException>(async () => _ = await cancelledBeforeAdmission),
                "An already-cancelled command was delivered to an interaction handler.");
        }

        // Protocol 4: the presentation registers its handlers with open (generation 0) or interactions; the producer
        // pushes interaction requests to the presentation's connection, and the page answers with interaction-reply.
        var pushes = new Pushes(transport);
        RequireOpen(transport.Open(route, "present-a", "connection-a", "client-a", Handlers()),
            "The root interaction presentation did not open with its handlers.");

        Task<bool> answer;
        using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-a"))
            answer = model.ConfirmDelete.Handle("browser");
        using var request = await pushes.NextAsync("connection-a", "interaction");
        var root = request.RootElement;
        Require(root.GetProperty("presentation").GetString() == "present-a"
            && root.GetProperty("input").GetString() == "browser",
            "The interaction push did not deliver the typed request to its selected presentation.");

        var reply = ReplyJson(route, "present-a", root, "false");
        Require(Result(transport.Send(reply, "connection-a", "client-a")) == "ok",
            "The selected browser interaction response was rejected.");
        Require(!await answer, "The typed browser response did not complete ReactiveUI Interaction.Handle.");

        // Replaying a response is harmless, while the same response from a
        // different connection cannot complete or inspect the request.
        Require(Result(transport.Send(reply, "connection-a", "client-a")) == "already-completed",
            "A matching duplicate interaction response was not recognized.");
        Require(Result(transport.Send(ReplyJson(route, "present-a", root, "true"), "connection-a", "client-a")) == "stale",
            "A conflicting duplicate interaction response was accepted as a retry.");
        Require(Result(transport.Send(reply, "connection-x", "client-x")) == "stale",
            "A response from another connection was accepted.");

        Require(Reason(transport.Send("""{"kind":"interaction-reply"}""", "connection-a", "client-a")) == "invalid-request",
            "A malformed interaction reply escaped the request boundary.");
        Require(Reason(transport.Send("""{"kind":"interactions","route":"interaction"}""", "connection-a", "client-a")) == "invalid-request",
            "A malformed interaction registration escaped the request boundary.");

        // A later generation replaces the registration; two questions of one invocation both reach the presentation.
        Require(Registration(transport.Send(InteractionsJson(route, "present-a", 1), "connection-a", "client-a")) == "ok",
            "The interaction registration was rejected.");
        Task<bool> firstQueued;
        Task<bool> secondQueued;
        using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-a"))
        {
            firstQueued = session.Interactions.TryRequest(route, "confirmDelete", "testing.confirmDelete.v1",
                "first", static value => JsonSerializer.Serialize(value), static value => value.GetBoolean())!;
            secondQueued = session.Interactions.TryRequest(route, "confirmDelete", "testing.confirmDelete.v1",
                "second", static value => JsonSerializer.Serialize(value), static value => value.GetBoolean())!;
        }
        using (var first = await pushes.NextAsync("connection-a", "interaction"))
            Require(Result(transport.Send(ReplyJson(route, "present-a", first.RootElement, "true"), "connection-a", "client-a")) == "ok"
                && await firstQueued, "The first queued browser interaction was not completed.");
        using (var second = await pushes.NextAsync("connection-a", "interaction"))
            Require(Result(transport.Send(ReplyJson(route, "present-a", second.RootElement, "false"), "connection-a", "client-a")) == "ok"
                && !await secondQueued, "A concurrent interaction fell through instead of reaching the presentation.");

        // An answer the generated decoder rejects settles the request with a
        // typed error immediately, whichever exception the decoder throws.
        Task<bool> undecodable;
        using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-a"))
            undecodable = session.Interactions.TryRequest(route, "confirmDelete", "testing.confirmDelete.v1",
                "undecodable", static value => JsonSerializer.Serialize(value),
                static value => value.GetProperty("confirmed").GetBoolean())!;
        using (var undecodableWire = await pushes.NextAsync("connection-a", "interaction"))
            Require(Result(transport.Send(ReplyJson(route, "present-a", undecodableWire.RootElement, "false"), "connection-a", "client-a")) == "invalid-output",
                "An undecodable interaction answer was not reported as invalid output.");
        Require(await ThrowsAsync<RunicInteractionOutputException>(
            () => undecodable.WaitScaledAsync(TimeSpan.FromSeconds(5))),
            "An undecodable interaction answer left the request pending instead of failing it.");

        // The command's cancellation reaches a handler already processing a prompt as interaction-cancelled.
        using var commandCancellation = new CancellationTokenSource();
        Task<bool> cancelledByCommand;
        using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-a", cancellationToken: commandCancellation.Token))
            cancelledByCommand = session.Interactions.TryRequest(route, "confirmDelete", "testing.confirmDelete.v1",
                "cancel-through-control", static value => JsonSerializer.Serialize(value), static value => value.GetBoolean())!;
        using (var pending = await pushes.NextAsync("connection-a", "interaction"))
        {
            commandCancellation.Cancel();
            using var control = await pushes.NextAsync("connection-a", "interaction-cancelled");
            Require(control.RootElement.GetProperty("requestId").GetString() == pending.RootElement.GetProperty("requestId").GetString(),
                "Command cancellation was not pushed to the selected presentation.");
        }
        Require(await ThrowsAsync<RunicInteractionCancelledException>(async () => _ = await cancelledByCommand),
            "Command cancellation did not cancel the selected browser interaction.");

        Task<bool> cancelled;
        using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-a"))
            cancelled = model.ConfirmDelete.Handle("cancel");
        (await pushes.NextAsync("connection-a", "interaction")).Dispose();
        using (var closed = JsonDocument.Parse(transport.Close(route, "present-a", "connection-a", "client-a")))
            Require(closed.RootElement.GetProperty("presentation").GetString() == "ok", "The root interaction presentation did not close.");
        Require(await ThrowsAsync<RunicInteractionCancelledException>(async () => _ = await cancelled),
            "Closing a selected browser presentation did not cancel its pending question.");

        // A lost connection terminates only its presentation. A replacement
        // can register the same capability and immediately receive new work.
        RequireOpen(transport.Open(route, "present-b", "connection-b", "client-a", Handlers()),
            "The replacement interaction presentation did not open.");
        Task<bool> disconnected;
        using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-b"))
            disconnected = model.ConfirmDelete.Handle("disconnect");
        (await pushes.NextAsync("connection-b", "interaction")).Dispose();
        transport.CloseConnection("connection-b");
        Require(await ThrowsAsync<RunicInteractionCancelledException>(async () => _ = await disconnected),
            "Connection loss did not terminate its selected interaction.");

        RequireOpen(transport.Open(route, "present-c", "connection-c", "client-a", Handlers()),
            "The restarted interaction presentation did not open.");
        Task<bool> restarted;
        using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-c"))
            restarted = session.Interactions.TryRequest(route, "confirmDelete", "testing.confirmDelete.v1", "restart",
                static value => JsonSerializer.Serialize(value), static value => value.GetBoolean())!;
        using (var restartWire = await pushes.NextAsync("connection-c", "interaction"))
            Require(Result(transport.Send(ReplyJson(route, "present-c", restartWire.RootElement, "true"), "connection-c", "client-a")) == "ok"
                && await restarted, "The restarted presentation did not receive interaction work.");
    }

    private static string Handlers() => """[{"name":"confirmDelete","contract":"testing.confirmDelete.v1"}]""";

    private static string InteractionsJson(string route, string presentation, long generation) => BridgeRequests.Json(writer =>
    {
        writer.WriteString("kind", "interactions");
        writer.WriteString("route", route);
        writer.WriteString("presentation", presentation);
        writer.WriteNumber("generation", generation);
        writer.WritePropertyName("handlers");
        writer.WriteRawValue(Handlers());
    });

    // {"kind":"interaction-reply"} answering the interaction push request.
    private static string ReplyJson(string route, string presentation, JsonElement request, string outputJson) => BridgeRequests.Json(writer =>
    {
        writer.WriteString("kind", "interaction-reply");
        writer.WriteString("route", route);
        writer.WriteString("presentation", presentation);
        writer.WriteString("requestId", request.GetProperty("requestId").GetString());
        writer.WriteNumber("ownerEpoch", request.GetProperty("ownerEpoch").GetInt64());
        writer.WriteString("name", "confirmDelete");
        writer.WriteString("contract", "testing.confirmDelete.v1");
        writer.WriteString("outcome", "answered");
        writer.WritePropertyName("output");
        writer.WriteRawValue(outputJson);
    });

    private static void RequireOpen(string reply, string message)
    {
        using var document = JsonDocument.Parse(reply);
        var root = document.RootElement;
        Require(root.GetProperty("ok").GetBoolean() && root.GetProperty("presentation").GetString() == "ok"
            && root.GetProperty("registration").GetString() == "ok", $"{message} {reply}");
    }

    private static string? Result(string json) => Member(json, "result");

    private static string? Registration(string json) => Member(json, "registration");

    private static string? Member(string json, string name)
    {
        using var reply = JsonDocument.Parse(json);
        return reply.RootElement.TryGetProperty(name, out var value) ? value.GetString() : json;
    }

    private static string? Reason(string json)
    {
        using var reply = JsonDocument.Parse(json);
        return reply.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("reason", out var reason)
            ? reason.GetString() : json;
    }

    // The targeted pushes of the transport, by connection and kind, in order.
    private sealed class Pushes(InMemoryBridgeTransport transport)
    {
        private readonly List<RunicBridgeMessage> _pending = [];

        internal async Task<JsonDocument> NextAsync(string connectionKey, string kind)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                _pending.AddRange(transport.TakeMessages());
                for (var index = 0; index < _pending.Count; index++)
                {
                    if (_pending[index].ConnectionKey != connectionKey) continue;
                    var document = JsonDocument.Parse(_pending[index].Json);
                    if (document.RootElement.GetProperty("kind").GetString() == kind)
                    {
                        _pending.RemoveAt(index);
                        return document;
                    }
                    document.Dispose();
                }
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"No {kind} push reached {connectionKey}.");
                await Task.Delay(10);
            }
        }
    }

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

    private sealed class InteractionModel : System.ComponentModel.INotifyPropertyChanged
    {
        public Interaction<string, bool> ConfirmDelete { get; } = new();
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    private sealed class InteractionBridge(IBridgeTransport transport, InteractionModel model, string route, WindowContentSession session)
        : ViewModelBridge<InteractionModel>(transport, model, route, Array.Empty<BridgeStateMember<InteractionModel>>(), [], [], [],
            content: session);
}
