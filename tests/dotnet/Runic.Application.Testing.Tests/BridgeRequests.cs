using System.Runtime.CompilerServices;
using System.Text.Json;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// Views protocol 4 requests by route and member on an InMemoryBridgeTransport, for the tests that drive a Bridge
// without a RunicViewDriver (specs/application/README.md, "Requests and replies"). Each request is sent after its
// connection's hello; a connection that was closed sends a new hello with a new session. Values and inputs are wire
// JSON.
internal static class BridgeRequests
{
    private static readonly ConditionalWeakTable<InMemoryBridgeTransport, State> States = new();

    // Sends one request and returns its reply.
    internal static string Send(this InMemoryBridgeTransport transport, string json,
        string connectionKey = InMemoryBridgeTransport.DefaultConnectionKey, string? clientKey = null) =>
        transport.SendAsync(json, connectionKey, clientKey).AsTask().GetAwaiter().GetResult();

    internal static async ValueTask<string> SendAsync(this InMemoryBridgeTransport transport, string json,
        string connectionKey = InMemoryBridgeTransport.DefaultConnectionKey, string? clientKey = null,
        CancellationToken cancellationToken = default)
    {
        var state = States.GetOrCreateValue(transport);
        if (state.Greet(connectionKey) is { } session) await HelloAsync(transport, connectionKey, clientKey, session).ConfigureAwait(false);
        var reply = await transport.RequestAsync(json, connectionKey, clientKey, cancellationToken).ConfigureAwait(false);
        // The connection was closed since its hello (CloseConnection): it greets again with a new session.
        if (!reply.Contains("\"reason\":\"no-hello\"", StringComparison.Ordinal)) return reply;
        await HelloAsync(transport, connectionKey, clientKey, state.NextSession()).ConfigureAwait(false);
        return await transport.RequestAsync(json, connectionKey, clientKey, cancellationToken).ConfigureAwait(false);
    }

    // Sends the hello of a connection unless this helper already did.
    internal static void Hello(this InMemoryBridgeTransport transport,
        string connectionKey = InMemoryBridgeTransport.DefaultConnectionKey, string? clientKey = null)
    {
        if (States.GetOrCreateValue(transport).Greet(connectionKey) is { } session)
            HelloAsync(transport, connectionKey, clientKey, session).AsTask().GetAwaiter().GetResult();
    }

    private static async ValueTask HelloAsync(InMemoryBridgeTransport transport, string connectionKey, string? clientKey, string session)
    {
        var reply = await transport.RequestAsync(Json(writer =>
        {
            writer.WriteString("kind", "hello");
            writer.WriteNumber("protocol", BridgeProtocol.Version);
            writer.WriteString("session", session);
            writer.WriteStartObject("client");
            writer.WriteString("name", "Runic.Application.Testing.Tests");
            writer.WriteString("version", "1.0.0");
            writer.WriteEndObject();
        }), connectionKey, clientKey).ConfigureAwait(false);
        // A driver of the same transport may have greeted the connection already.
        if (!reply.StartsWith("{\"ok\":true", StringComparison.Ordinal) && !reply.Contains("already completed a hello", StringComparison.Ordinal))
            throw new InvalidOperationException($"The hello of {connectionKey} was refused: {reply}");
    }

    // {"kind":"snapshot","route"}: the reply with revision, state and fields.
    internal static string Snapshot(this InMemoryBridgeTransport transport, string route,
        string connectionKey = InMemoryBridgeTransport.DefaultConnectionKey) =>
        transport.Send(Json(writer => Route(writer, "snapshot", route)), connectionKey);

    // The state of a snapshot reply; throws when the reply is not ok.
    internal static JsonDocument SnapshotState(this InMemoryBridgeTransport transport, string route)
    {
        var reply = transport.Snapshot(route);
        var document = JsonDocument.Parse(reply);
        if (document.RootElement.GetProperty("ok").GetBoolean()) return document;
        document.Dispose();
        throw new InvalidOperationException($"The snapshot of {route} failed: {reply}");
    }

    // {"kind":"set","route","member","value","edit"}; without an edit, the next edit number of the member on the
    // connection.
    internal static string Set(this InMemoryBridgeTransport transport, string route, string member, string valueJson,
        long? edit = null, string connectionKey = InMemoryBridgeTransport.DefaultConnectionKey, string? clientKey = null)
    {
        var number = edit ?? States.GetOrCreateValue(transport).NextEdit(connectionKey, route, member);
        return transport.Send(Json(writer =>
        {
            Route(writer, "set", route);
            writer.WriteString("member", member);
            writer.WritePropertyName("value");
            writer.WriteRawValue(valueJson);
            writer.WriteNumber("edit", number);
        }), connectionKey, clientKey);
    }

    // {"kind":"command","route","member","input"?}.
    internal static string Command(this InMemoryBridgeTransport transport, string route, string member, string? inputJson = null,
        string connectionKey = InMemoryBridgeTransport.DefaultConnectionKey, string? clientKey = null) =>
        transport.CommandAsync(route, member, inputJson, connectionKey, clientKey).AsTask().GetAwaiter().GetResult();

    internal static ValueTask<string> CommandAsync(this InMemoryBridgeTransport transport, string route, string member,
        string? inputJson = null, string connectionKey = InMemoryBridgeTransport.DefaultConnectionKey, string? clientKey = null,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync(Json(writer => Member(writer, "command", route, member, inputJson)), connectionKey, clientKey, cancellationToken);

    // {"kind":"can","route","member","input"?}.
    internal static string Can(this InMemoryBridgeTransport transport, string route, string member, string? inputJson = null,
        string connectionKey = InMemoryBridgeTransport.DefaultConnectionKey) =>
        transport.Send(Json(writer => Member(writer, "can", route, member, inputJson)), connectionKey);

    // {"kind":"write","route","member","requestId","expectedVersion","expectedValue","value"}.
    internal static string Write(this InMemoryBridgeTransport transport, string route, string member, string requestId,
        long expectedVersion, string expectedValueJson, string valueJson,
        string connectionKey = InMemoryBridgeTransport.DefaultConnectionKey) =>
        transport.Send(Json(writer =>
        {
            Route(writer, "write", route);
            writer.WriteString("member", member);
            writer.WriteString("requestId", requestId);
            writer.WriteNumber("expectedVersion", expectedVersion);
            writer.WritePropertyName("expectedValue");
            writer.WriteRawValue(expectedValueJson);
            writer.WritePropertyName("value");
            writer.WriteRawValue(valueJson);
        }), connectionKey);

    // {"kind":"open","route","presentation","handlers"?}.
    internal static string Open(this InMemoryBridgeTransport transport, string route, string presentation,
        string connectionKey = InMemoryBridgeTransport.DefaultConnectionKey, string? clientKey = null, string? handlersJson = null) =>
        transport.Send(Json(writer =>
        {
            Route(writer, "open", route);
            writer.WriteString("presentation", presentation);
            if (handlersJson is null) return;
            writer.WritePropertyName("handlers");
            writer.WriteRawValue(handlersJson);
        }), connectionKey, clientKey);

    // {"kind":"close","route","presentation"}.
    internal static string Close(this InMemoryBridgeTransport transport, string route, string presentation,
        string connectionKey = InMemoryBridgeTransport.DefaultConnectionKey, string? clientKey = null) =>
        transport.Send(Json(writer =>
        {
            Route(writer, "close", route);
            writer.WriteString("presentation", presentation);
        }), connectionKey, clientKey);

    // {"kind":"start","route","member","operation","contract","input"?}: starts an operation (specs/application/README.md,
    // "Operations"). The contract of a generated Bridge is OperationContract<TModel>().
    internal static string Start(this InMemoryBridgeTransport transport, string route, string member, string operation, string contract,
        string? inputJson = null, string connectionKey = InMemoryBridgeTransport.DefaultConnectionKey, string? clientKey = null) =>
        transport.Send(Json(writer =>
        {
            Member(writer, "start", route, member, inputJson);
            writer.WriteString("operation", operation);
            writer.WriteString("contract", contract);
        }), connectionKey, clientKey);

    // {"kind":"status"|"watch"|"unwatch"|"cancel","route","member","operation","contract"}.
    internal static string Operation(this InMemoryBridgeTransport transport, string kind, string route, string member, string operation,
        string contract, string connectionKey = InMemoryBridgeTransport.DefaultConnectionKey, string? clientKey = null) =>
        transport.Send(Json(writer =>
        {
            Member(writer, kind, route, member, null);
            writer.WriteString("operation", operation);
            writer.WriteString("contract", contract);
        }), connectionKey, clientKey);

    // Reads an operation's status until it is no longer running and returns the status object.
    internal static async Task<JsonDocument> WaitForOperationAsync(this InMemoryBridgeTransport transport, string route, string member,
        string operation, string contract, TimeSpan timeout, string connectionKey = InMemoryBridgeTransport.DefaultConnectionKey)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            using var reply = JsonDocument.Parse(transport.Operation("status", route, member, operation, contract, connectionKey));
            if (!reply.RootElement.TryGetProperty("status", out var status))
                throw new InvalidOperationException($"The status of {operation} failed: {reply.RootElement}");
            if (status.GetProperty("kind").GetString() != "running") return JsonDocument.Parse(status.GetRawText());
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"The operation {operation} is still running.");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    // The operation contract of a generated Bridge's ViewModel, as RunicViewDriver<TModel>.Start sends it.
    internal static string OperationContract<TModel>() => $"{typeof(TModel).FullName}:{BridgeContractShape.Compute(typeof(TModel))}";

    // Waits for a push of the kind to one connection, such as an interaction, and returns it. Takes the transport's
    // messages while it waits: the other messages it takes are dropped.
    internal static async Task<JsonDocument> TakePushAsync(this InMemoryBridgeTransport transport, string connectionKey, string kind,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            foreach (var message in transport.TakeMessages())
            {
                if (message.ConnectionKey != connectionKey) continue;
                var push = JsonDocument.Parse(message.Json);
                if (push.RootElement.GetProperty("kind").GetString() == kind) return push;
                push.Dispose();
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"No {kind} push reached {connectionKey}.");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    // The pushes of kind state, frame or failure that the window broadcast for a route, taken from the transport's
    // messages; other messages stay recorded.
    internal static IReadOnlyList<JsonDocument> TakePushes(this InMemoryBridgeTransport transport, string route)
    {
        List<JsonDocument> pushes = [];
        foreach (var message in transport.TakeMessages())
        {
            var document = JsonDocument.Parse(message.Json);
            var root = document.RootElement;
            if (message.ConnectionKey is null && root.TryGetProperty("route", out var name) && name.GetString() == route
                && root.GetProperty("kind").GetString() is "state" or "frame" or "failure")
                pushes.Add(document);
            else document.Dispose();
        }
        return pushes;
    }

    internal static string Json(Action<Utf8JsonWriter> writeMembers)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writeMembers(writer);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Route(Utf8JsonWriter writer, string kind, string route)
    {
        writer.WriteString("kind", kind);
        writer.WriteString("route", route);
    }

    private static void Member(Utf8JsonWriter writer, string kind, string route, string member, string? inputJson)
    {
        Route(writer, kind, route);
        writer.WriteString("member", member);
        if (inputJson is null) return;
        writer.WritePropertyName("input");
        writer.WriteRawValue(inputJson);
    }

    private sealed class State
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _greeted = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Connection, string Route, string Member), long> _edits = [];
        private int _sessions;

        // The session of the connection's first hello, or null when it was greeted.
        internal string? Greet(string connectionKey)
        {
            lock (_gate) return _greeted.Add(connectionKey) ? $"{connectionKey}-tests{++_sessions}" : null;
        }

        internal string NextSession()
        {
            lock (_gate) return $"tests{++_sessions}";
        }

        internal long NextEdit(string connectionKey, string route, string member)
        {
            lock (_gate)
            {
                var next = _edits.GetValueOrDefault((connectionKey, route, member)) + 1;
                _edits[(connectionKey, route, member)] = next;
                return next;
            }
        }
    }
}
