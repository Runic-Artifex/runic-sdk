using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Runic.Application.Testing.Tests.Conformance;

// Runs a scenario against the .NET producer in the producer role (specs/application/conformance/README.md, "Steps"):
// the window is the test View the hook creates on a ScenarioTransport with a virtual clock, and the steps drive it.
// Steps for the client role, and call and client expectations, are skipped. When the steps end, no connection may have
// a reply or push left that no receive step matched.
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
        var replyTo = step["replyTo"]?.GetValue<string>();
        if (replyTo is not null)
        {
            _messages.Id(replyTo, "request");
            if (!_requests.TryGetValue(replyTo, out var sentOn) || sentOn != connection)
                throw new ScenarioFailure($"The request {replyTo} was not sent on the connection {connection.Name}.");
        }
        var what = replyTo is null ? "a push" : $"the reply to {replyTo}";
        ScenarioMessage message;
        using (var timeout = new CancellationTokenSource(ReceiveTimeout))
        {
            try { message = await connection.Messages.Reader.ReadAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                throw new ScenarioFailure($"The connection {connection.Name} received no message within {ReceiveTimeout.TotalSeconds} s; " +
                    $"expected {what}: {ScenarioMessages.Abbreviate(expected)}");
            }
        }
        if (message.ReplyTo != replyTo)
            throw new ScenarioFailure($"Expected {what}, but the next message was " +
                $"{(message.ReplyTo is null ? "a push" : $"the reply to {message.ReplyTo}")}: {Truncate(message.Json)}");
        JsonNode? actual;
        try { actual = JsonNode.Parse(message.Json); }
        catch (System.Text.Json.JsonException error)
        {
            throw new ScenarioFailure($"The producer sent a message that is not JSON ({error.Message}): {Truncate(message.Json)}");
        }
        _messages.Match(expected, actual);
    }

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
                    $"({(message.ReplyTo is null ? "a push" : $"the reply to {message.ReplyTo}")}): {Truncate(message.Json)}");
    }

    private static string Truncate(string json) => json.Length <= 2048 ? json : $"{json[..2048]}... ({json.Length} chars)";
}
