using System.Text.Json;
using ReactiveUI.Binding;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;

namespace Runic.Application.Testing.Tests;

internal static class InteractionFixture
{
    public static async Task VerifyAsync()
    {
        using var transport = new InMemoryViewTransport();
        using var session = new WindowContentSession(transport);
        var model = new InteractionModel();
        var route = "interaction";
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
                alreadyCancelled.Token))
                cancelledBeforeAdmission = model.ConfirmDelete.Handle("cancelled-before-admission");
            Require(await ThrowsAsync<OperationCanceledException>(async () => _ = await cancelledBeforeAdmission),
                "An already-cancelled command was delivered to an interaction handler.");
        }

        Require(transport.Call($"{route}Mount", new(StringValue: "session-a:present-a", ClientKey: "client-a",
            ConnectionKey: "connection-a")) == "ok", "The root interaction presentation did not mount.");
        var wait = transport.CallAsync(BridgeInteractionRouter.WaitRoute, new(StringValue: WaitJson(route,
            "session-a:present-a", "confirmDelete", "testing.confirmDelete.v1"), ClientKey: "client-a",
            ConnectionKey: "connection-a")).AsTask();
        await Task.Yield();

        Task<bool> answer;
        using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-a"))
            answer = model.ConfirmDelete.Handle("browser");
        using var request = JsonDocument.Parse(await wait);
        var root = request.RootElement;
        Require(root.GetProperty("kind").GetString() == "request"
            && root.GetProperty("input").GetString() == "browser",
            "The interaction wait route did not deliver the typed request to its selected mount.");

        var reply = JsonSerializer.Serialize(new
        {
            kind = "answered",
            requestId = root.GetProperty("requestId").GetString(),
            route,
            presentationId = "session-a:present-a",
            ownerEpoch = root.GetProperty("ownerEpoch").GetInt64(),
            name = "confirmDelete",
            contract = "testing.confirmDelete.v1",
            output = false,
        });
        Require(Kind(transport.Call(BridgeInteractionRouter.ReplyRoute,
            new(StringValue: reply, ClientKey: "client-a", ConnectionKey: "connection-a"))) == "ok",
            "The selected browser interaction response was rejected.");
        Require(!await answer, "The typed browser response did not complete ReactiveUI Interaction.Handle.");

        // Replaying a response is harmless, while the same response from a
        // different connection cannot complete or inspect the request.
        Require(Kind(transport.Call(BridgeInteractionRouter.ReplyRoute,
            new(StringValue: reply, ClientKey: "client-a", ConnectionKey: "connection-a"))) == "already-completed",
            "A matching duplicate interaction response was not recognized.");
        var conflictingReply = reply.Replace("\"output\":false", "\"output\":true", StringComparison.Ordinal);
        Require(Kind(transport.Call(BridgeInteractionRouter.ReplyRoute,
            new(StringValue: conflictingReply, ClientKey: "client-a", ConnectionKey: "connection-a"))) == "stale",
            "A conflicting duplicate interaction response was accepted as a retry.");
        Require(Kind(transport.Call(BridgeInteractionRouter.ReplyRoute,
            new(StringValue: reply, ClientKey: "client-b", ConnectionKey: "connection-b"))) == "stale",
            "A response from another connection was accepted.");

        Require(Kind(await transport.CallAsync(BridgeInteractionRouter.WaitRoute,
            new(StringValue: "{}", ClientKey: "client-a", ConnectionKey: "connection-a"))) == "invalid-request",
            "A malformed interaction wait request escaped the route boundary.");
        Require(Kind(transport.Call(BridgeInteractionRouter.ReplyRoute,
            new(StringValue: "{}", ClientKey: "client-a", ConnectionKey: "connection-a"))) == "invalid-request",
            "A malformed interaction reply escaped the route boundary.");

        // Capabilities survive the interval between prompt polls. Two browser
        // questions therefore remain bound to this presentation and are
        // delivered one at a time as it polls again.
        Require(Kind(transport.Call(BridgeInteractionRouter.ControlRoute, new(StringValue: ControlJson(route,
            "session-a:present-a", 1, "confirmDelete", "testing.confirmDelete.v1"), ClientKey: "client-a",
            ConnectionKey: "connection-a"))) == "ok", "The interaction capability registration was rejected.");
        Task<bool> firstQueued;
        Task<bool> secondQueued;
        using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-a"))
        {
            firstQueued = session.Interactions.TryRequest(route, "confirmDelete", "testing.confirmDelete.v1",
                "first", static value => JsonSerializer.Serialize(value), static value => value.GetBoolean())!;
            secondQueued = session.Interactions.TryRequest(route, "confirmDelete", "testing.confirmDelete.v1",
                "second", static value => JsonSerializer.Serialize(value), static value => value.GetBoolean())!;
        }
        var firstWire = await transport.CallAsync(BridgeInteractionRouter.WaitRoute, new(StringValue: WaitJson(route,
            "session-a:present-a", "confirmDelete", "testing.confirmDelete.v1", 1), ClientKey: "client-a",
            ConnectionKey: "connection-a"));
        Require(Reply(transport, route, "session-a:present-a", firstWire, true) == "ok"
            && await firstQueued, "The first queued browser interaction was not completed.");
        var secondWire = await transport.CallAsync(BridgeInteractionRouter.WaitRoute, new(StringValue: WaitJson(route,
            "session-a:present-a", "confirmDelete", "testing.confirmDelete.v1", 1), ClientKey: "client-a",
            ConnectionKey: "connection-a"));
        Require(Reply(transport, route, "session-a:present-a", secondWire, false) == "ok"
            && !await secondQueued, "A concurrent interaction fell through instead of waiting for the next poll.");

        // The separate control stream reaches a handler already processing a
        // prompt when the command's cancellation token fires.
        var controlWait = transport.CallAsync(BridgeInteractionRouter.ControlWaitRoute, new(StringValue:
            JsonSerializer.Serialize(new { route, presentationId = "session-a:present-a" }), ClientKey: "client-a",
            ConnectionKey: "connection-a")).AsTask();
        var cancellationWait = transport.CallAsync(BridgeInteractionRouter.WaitRoute, new(StringValue: WaitJson(route,
            "session-a:present-a", "confirmDelete", "testing.confirmDelete.v1", 1), ClientKey: "client-a",
            ConnectionKey: "connection-a")).AsTask();
        using var commandCancellation = new CancellationTokenSource();
        Task<bool> cancelledByCommand;
        using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-a", commandCancellation.Token))
            cancelledByCommand = session.Interactions.TryRequest(route, "confirmDelete", "testing.confirmDelete.v1",
                "cancel-through-control", static value => JsonSerializer.Serialize(value), static value => value.GetBoolean())!;
        _ = await cancellationWait;
        commandCancellation.Cancel();
        using (var control = JsonDocument.Parse(await controlWait))
            Require(control.RootElement.GetProperty("kind").GetString() == "cancelled"
                && control.RootElement.TryGetProperty("requestId", out _),
                "Command cancellation was not published to the selected interaction control stream.");
        Require(await ThrowsAsync<RunicInteractionCancelledException>(async () => _ = await cancelledByCommand),
            "Command cancellation did not cancel the selected browser interaction.");

        var unmountWait = transport.CallAsync(BridgeInteractionRouter.WaitRoute, new(StringValue: WaitJson(route,
            "session-a:present-a", "confirmDelete", "testing.confirmDelete.v1"), ClientKey: "client-a",
            ConnectionKey: "connection-a")).AsTask();
        await Task.Yield();
        Task<bool> cancelled;
        using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-a"))
            cancelled = model.ConfirmDelete.Handle("cancel");
        _ = await unmountWait;
        Require(transport.Call($"{route}Unmount", new(StringValue: "session-a:present-a", ClientKey: "client-a",
            ConnectionKey: "connection-a")) == "ok", "The root interaction presentation did not unmount.");
        Require(await ThrowsAsync<RunicInteractionCancelledException>(async () => _ = await cancelled),
            "Unmounting a selected browser presentation did not cancel its pending question.");

        // A lost connection terminates only its presentation. A replacement
        // can register the same capability and immediately receive queued work.
        Require(transport.Call($"{route}Mount", new(StringValue: "session-b:present-b", ClientKey: "client-a",
            ConnectionKey: "connection-b")) == "ok", "The replacement interaction presentation did not mount.");
        Require(Kind(transport.Call(BridgeInteractionRouter.ControlRoute, new(StringValue: ControlJson(route,
            "session-b:present-b", 1, "confirmDelete", "testing.confirmDelete.v1"), ClientKey: "client-a",
            ConnectionKey: "connection-b"))) == "ok", "The replacement capability registration was rejected.");
        var disconnectControl = transport.CallAsync(BridgeInteractionRouter.ControlWaitRoute, new(StringValue:
            JsonSerializer.Serialize(new { route, presentationId = "session-b:present-b" }), ClientKey: "client-a",
            ConnectionKey: "connection-b")).AsTask();
        var disconnectWait = transport.CallAsync(BridgeInteractionRouter.WaitRoute, new(StringValue: WaitJson(route,
            "session-b:present-b", "confirmDelete", "testing.confirmDelete.v1", 1), ClientKey: "client-a",
            ConnectionKey: "connection-b")).AsTask();
        Task<bool> disconnected;
        using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-b"))
            disconnected = model.ConfirmDelete.Handle("disconnect");
        _ = await disconnectWait;
        session.ReleaseConnection("connection-b");
        Require(Kind(await disconnectControl) == "disconnected"
            && await ThrowsAsync<RunicInteractionCancelledException>(async () => _ = await disconnected),
            "Connection loss did not terminate its selected interaction and control poll.");

        Require(transport.Call($"{route}Mount", new(StringValue: "session-c:present-c", ClientKey: "client-a",
            ConnectionKey: "connection-c")) == "ok", "The restarted interaction presentation did not mount.");
        Require(Kind(transport.Call(BridgeInteractionRouter.ControlRoute, new(StringValue: ControlJson(route,
            "session-c:present-c", 1, "confirmDelete", "testing.confirmDelete.v1"), ClientKey: "client-a",
            ConnectionKey: "connection-c"))) == "ok", "The restarted capability registration was rejected.");
        Task<bool> restarted;
        using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-c"))
            restarted = session.Interactions.TryRequest(route, "confirmDelete", "testing.confirmDelete.v1", "restart",
                static value => JsonSerializer.Serialize(value), static value => value.GetBoolean())!;
        var restartWire = await transport.CallAsync(BridgeInteractionRouter.WaitRoute, new(StringValue: WaitJson(route,
            "session-c:present-c", "confirmDelete", "testing.confirmDelete.v1", 1), ClientKey: "client-a",
            ConnectionKey: "connection-c"));
        Require(Reply(transport, route, "session-c:present-c", restartWire, true, "connection-c") == "ok" && await restarted,
            "The restarted presentation did not receive queued interaction work.");
    }

    private static string WaitJson(string route, string presentationId, string name, string contract, long generation = 0) =>
        JsonSerializer.Serialize(new { route, presentationId, generation, handlers = new[] { new { name, contract } } });

    private static string ControlJson(string route, string presentationId, long generation, string name, string contract) =>
        JsonSerializer.Serialize(new { route, presentationId, generation, handlers = new[] { new { name, contract } } });

    private static string Reply(InMemoryViewTransport transport, string route, string presentationId, string request, bool output,
        string connection = "connection-a")
    {
        using var wire = JsonDocument.Parse(request);
        var root = wire.RootElement;
        return Kind(transport.Call(BridgeInteractionRouter.ReplyRoute, new(StringValue: JsonSerializer.Serialize(new
        {
            kind = "answered", requestId = root.GetProperty("requestId").GetString(), route, presentationId,
            ownerEpoch = root.GetProperty("ownerEpoch").GetInt64(), name = "confirmDelete",
            contract = "testing.confirmDelete.v1", output,
        }), ClientKey: "client-a", ConnectionKey: connection)));
    }

    private static string Kind(string json)
    {
        using var reply = JsonDocument.Parse(json);
        return reply.RootElement.GetProperty("kind").GetString()!;
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

    private sealed class InteractionModel
    {
        public Interaction<string, bool> ConfirmDelete { get; } = new();
    }
}
