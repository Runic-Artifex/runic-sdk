using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Runic.Application.Testing.Tests.Conformance;

// Runs a scenario against the .NET producer in the producer role (specs/application/conformance/README.md, "Steps"):
// the window is the test View the hook creates on a ScenarioTransport with a virtual clock, and the steps drive it.
// Steps for the client role, and call and client expectations, are skipped. The receives of an unordered group match
// the producer's next messages in any order. When the steps end, no connection may have a reply or push left that no
// receive step matched.
internal sealed class ProducerScenarioRunner
{
    // How long a receive step waits, in real time, for the producer's next message. Scenario time passes only
    // through advance.
    internal static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    private readonly ConformanceScenario _scenario;
    private readonly ScenarioMessages _messages;
    private readonly ScenarioTransport _transport;
    private readonly FakeTimeProvider _clock = new();
    private readonly Dictionary<string, ScenarioConnection> _connections = new(StringComparer.Ordinal);
    // The request step ids sent so far, with their connections.
    private readonly Dictionary<string, ScenarioConnection> _requests = new(StringComparer.Ordinal);
    private ScenarioConnection? _first;

    private ProducerScenarioRunner(ConformanceScenario scenario, ScenarioTransport transport)
    {
        _scenario = scenario;
        _transport = transport;
        _messages = new(scenario.Ids);
    }

    internal static async Task RunAsync(ConformanceScenario scenario, IConformanceViews views)
    {
        using var transport = new ScenarioTransport();
        var runner = new ProducerScenarioRunner(scenario, transport);
        using var window = views.CreateWindow(scenario.View, scenario.Model,
            new(transport, runner._clock, scenario.InputBytes));
        runner._messages.Provider = window.Provide;
        try
        {
            for (var index = 0; index < scenario.Steps.Count; index++)
            {
                var step = scenario.Steps[index];
                if (IsUnordered(step))
                {
                    var group = UnorderedGroup(scenario.Steps, index);
                    try { await runner.ReceiveUnorderedAsync(group).ConfigureAwait(false); }
                    catch (ScenarioFailure failure)
                    {
                        throw new ScenarioFailure($"{scenario.Id}, the unordered steps {string.Join(", ", group.Select(item => item.Index + 1))}:\n{failure.Message}");
                    }
                    // The steps the group spans are receives of the group or steps the runner skips.
                    index = group[^1].Index;
                    continue;
                }
                try { await runner.RunStepAsync(step, window).ConfigureAwait(false); }
                catch (ScenarioFailure failure)
                {
                    throw new ScenarioFailure($"{scenario.Id}, step {index + 1} {ScenarioMessages.Abbreviate(step)}:\n{failure.Message}");
                }
            }
            runner.RequireNothingUnmatched();
        }
        finally
        {
            foreach (var connection in runner._connections.Values.Where(connection => connection.IsOpen)) transport.Disconnect(connection);
        }
    }

    private async Task RunStepAsync(JsonObject step, IConformanceWindow window)
    {
        if (step["role"]?.GetValue<string>() == "client") return;
        if (step["connect"] is JsonValue connect) Connect(connect.GetValue<string>());
        else if (step["disconnect"] is JsonValue disconnect) _transport.Disconnect(Open(disconnect.GetValue<string>()));
        else if (step["send"] is { } send) Send(step, send);
        else if (step["receive"] is { } receive) await ReceiveAsync(step, receive).ConfigureAwait(false);
        else if (step["advance"] is JsonValue advance) _clock.Advance(TimeSpan.FromMilliseconds(advance.GetValue<int>()));
        else if (step["delivery"] is JsonValue delivery)
        {
            if (delivery.GetValue<string>() == "hold") _transport.Hold();
            else _transport.Release();
        }
        else if (step["mutate"] is JsonArray mutations)
            await window.MutateAsync(mutations, step["units"]?.GetValue<string>() == "each").ConfigureAwait(false);
        else if (step["expect"] is JsonObject expect)
        {
            // Only the model is the producer's; state, call, operation, diagnostics and notifications are the client's.
            if (expect["model"] is JsonObject model)
                foreach (var (member, value) in model)
                    _messages.MatchValue(value, window.ReadMember(member), $"model.{member}");
        }
        else if (!step.ContainsKey("call")) throw new ScenarioFailure("The step has no action a producer runner knows.");
    }

    private void Connect(string name)
    {
        _messages.Id(name, "connection");
        if (_connections.ContainsKey(name)) throw new ScenarioFailure($"The connection {name} was connected before.");
        var connection = _transport.Connect(name);
        _connections.Add(name, connection);
        _first ??= connection;
    }

    private void Send(JsonObject step, JsonNode message)
    {
        var id = step["id"]!.GetValue<string>();
        _messages.Id(id, "request");
        if (_requests.ContainsKey(id)) throw new ScenarioFailure($"The request {id} was sent before.");
        var connection = ConnectionOf(step);
        var bytes = _messages.Write(message);
        _requests.Add(id, connection);
        _transport.Send(connection, bytes, id);
    }

    private async Task ReceiveAsync(JsonObject step, JsonNode expected)
    {
        var connection = ConnectionOf(step);
        var replyTo = ReplyTo(step, connection);
        var what = replyTo is null ? "a push" : $"the reply to {replyTo}";
        var message = await NextAsync(connection, $"{what}: {ScenarioMessages.Abbreviate(expected)}").ConfigureAwait(false);
        if (message.ReplyTo != replyTo)
            throw new ScenarioFailure($"Expected {what}, but the next message was {Describe(message)}: {Truncate(message.Json)}");
        var actual = Parse(message);
        try { _messages.Match(expected, actual); }
        catch (ScenarioFailure failure) { throw new ScenarioFailure($"{failure.Message}\n  actual: {Truncate(message.Json)}"); }
    }

    // A receive step with unordered: true (specs/application/conformance/README.md, "Steps").
    private static bool IsUnordered(JsonObject step) =>
        step["role"]?.GetValue<string>() != "client" && step["receive"] is not null && step["unordered"]?.GetValue<bool>() == true;

    // A step that the producer runner skips, which may separate the receives of an unordered group: a call, a step of
    // the client role, or an expectation of the client.
    private static bool IsSkipped(JsonObject step) =>
        step["role"]?.GetValue<string>() == "client" || step.ContainsKey("call")
        || (step["expect"] is JsonObject expect && !expect.ContainsKey("model"));

    // The unordered receives of the group that starts at index, with their step indexes.
    private static List<(int Index, JsonObject Step)> UnorderedGroup(IReadOnlyList<JsonObject> steps, int index)
    {
        List<(int Index, JsonObject Step)> group = [];
        for (var next = index; next < steps.Count; next++)
        {
            if (IsUnordered(steps[next])) group.Add((next, steps[next]));
            else if (!IsSkipped(steps[next])) break;
        }
        return group;
    }

    // Requires that the producer's next messages are the group's, in any order.
    private async Task ReceiveUnorderedAsync(List<(int Index, JsonObject Step)> group)
    {
        var connection = ConnectionOf(group[0].Step);
        var remaining = new List<(int Index, string? ReplyTo, JsonNode Expected)>();
        foreach (var (index, step) in group)
        {
            if (ConnectionOf(step) != connection)
                throw new ScenarioFailure($"Step {index + 1} is on another connection than step {group[0].Index + 1}.");
            remaining.Add((index, ReplyTo(step, connection), step["receive"]!));
        }
        while (remaining.Count > 0)
        {
            var message = await NextAsync(connection,
                string.Join("; ", remaining.Select(item => $"step {item.Index + 1}: {ScenarioMessages.Abbreviate(item.Expected)}"))).ConfigureAwait(false);
            var actual = Parse(message);
            var failures = new List<string>();
            var matched = -1;
            for (var candidate = 0; candidate < remaining.Count && matched < 0; candidate++)
            {
                var (index, replyTo, expected) = remaining[candidate];
                if (replyTo != message.ReplyTo) continue;
                if (_messages.TryMatch(expected, actual) is { } failure) failures.Add($"step {index + 1}: {failure}");
                else matched = candidate;
            }
            if (matched < 0)
                throw new ScenarioFailure($"The producer sent {Describe(message)}, which none of the remaining steps " +
                    $"{string.Join(", ", remaining.Select(item => item.Index + 1))} expects: {Truncate(message.Json)}" +
                    string.Concat(failures.Select(failure => $"\n  {failure}")));
            remaining.RemoveAt(matched);
        }
    }

    // The request a receive step answers, checked to have been sent on the connection, or null for a push.
    private string? ReplyTo(JsonObject step, ScenarioConnection connection)
    {
        var replyTo = step["replyTo"]?.GetValue<string>();
        if (replyTo is null) return null;
        _messages.Id(replyTo, "request");
        if (!_requests.TryGetValue(replyTo, out var sentOn) || sentOn != connection)
            throw new ScenarioFailure($"The request {replyTo} was not sent on the connection {connection.Name}.");
        return replyTo;
    }

    private static async Task<ScenarioMessage> NextAsync(ScenarioConnection connection, string expected)
    {
        using var timeout = TestTimeouts.Cancellation(ReceiveTimeout);
        try { return await connection.Messages.Reader.ReadAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            throw new ScenarioFailure($"The connection {connection.Name} received no message within {ReceiveTimeout.TotalSeconds} s; " +
                $"expected {expected}");
        }
    }

    private static JsonNode? Parse(ScenarioMessage message)
    {
        try { return JsonNode.Parse(message.Json); }
        catch (System.Text.Json.JsonException error)
        {
            throw new ScenarioFailure($"The producer sent a message that is not JSON ({error.Message}): {Truncate(message.Json)}");
        }
    }

    private static string Describe(ScenarioMessage message) =>
        message.ReplyTo is null ? "a push" : $"the reply to {message.ReplyTo}";

    private ScenarioConnection ConnectionOf(JsonObject step)
    {
        if (step["connection"] is JsonValue name) return Open(name.GetValue<string>());
        if (_first is null) throw new ScenarioFailure("The step uses a connection before the scenario connected one.");
        if (!_first.IsOpen) throw new ScenarioFailure($"The connection {_first.Name} is closed.");
        return _first;
    }

    private ScenarioConnection Open(string name)
    {
        _messages.Id(name, "connection");
        if (!_connections.TryGetValue(name, out var connection)) throw new ScenarioFailure($"The connection {name} is not connected.");
        if (!connection.IsOpen) throw new ScenarioFailure($"The connection {name} is closed.");
        return connection;
    }

    private void RequireNothingUnmatched()
    {
        foreach (var connection in _connections.Values)
            if (connection.Messages.Reader.TryRead(out var message))
                throw new ScenarioFailure($"{_scenario.Id}: the connection {connection.Name} has a message no receive step matched " +
                    $"({Describe(message)}): {Truncate(message.Json)}");
    }

    private static string Truncate(string json) => json.Length <= 2048 ? json : $"{json[..2048]}... ({json.Length} chars)";
}
