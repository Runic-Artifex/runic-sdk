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

        var cancellationWait = transport.CallAsync(BridgeInteractionRouter.WaitRoute, new(StringValue: WaitJson(route,
            "session-a:present-a", "confirmDelete", "testing.confirmDelete.v1"), ClientKey: "client-a",
            ConnectionKey: "connection-a")).AsTask();
        await Task.Yield();
        Task<bool> cancelled;
        using (RunicInteractionInvocation.Enter(session, route, "client-a", "connection-a"))
            cancelled = model.ConfirmDelete.Handle("cancel");
        _ = await cancellationWait;
        Require(transport.Call($"{route}Unmount", new(StringValue: "session-a:present-a", ClientKey: "client-a",
            ConnectionKey: "connection-a")) == "ok", "The root interaction presentation did not unmount.");
        Require(await ThrowsAsync<RunicInteractionCancelledException>(async () => _ = await cancelled),
            "Unmounting a selected browser presentation did not cancel its pending question.");
    }

    private static string WaitJson(string route, string presentationId, string name, string contract) =>
        JsonSerializer.Serialize(new { route, presentationId, handlers = new[] { new { name, contract } } });

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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class InteractionModel
    {
        public Interaction<string, bool> ConfirmDelete { get; } = new();
    }
}
