using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Runic.Application.Testing;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// The host bridge of a generated-client harness: a Bun script whose generated clients talk Views protocol 4 to the
/// real generated bridges of the windows this test serves (GeneratedLiveBridge.ts is the script's side).
/// </summary>
/// <remarks>
/// The script writes each request to standard output as a line <c>\u0001runic-live {"id":n,"request":"&lt;json&gt;"}</c>
/// and asks for a model change of the test with <c>{"id":n,"step":"&lt;name&gt;"}</c>. The host answers on the script's
/// standard input with <c>{"id":n,"reply":"&lt;json&gt;"}</c> or <c>{"id":n,"error":"&lt;message&gt;"}</c>, after the
/// pushes the windows sent until then (<c>{"push":"&lt;json&gt;"}</c>); pushes that come later are forwarded as they
/// arrive. <c>{"done":true}</c> ends the run: the host closes the script's standard input.
/// </remarks>
internal sealed partial class GeneratedClientLiveHost : IDisposable
{
    internal const string Marker = "\u0001runic-live ";

    private readonly List<(string Route, InMemoryBridgeTransport Transport)> _windows = [];
    private readonly Dictionary<string, InMemoryBridgeTransport> _contentRoutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<Task>> _steps = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _output = new(1, 1);
    private StreamWriter? _input;

    /// <summary>Answers the requests of the root route <paramref name="route"/> and its content from <paramref name="transport"/>.</summary>
    internal GeneratedClientLiveHost Serve(string route, InMemoryBridgeTransport transport)
    {
        _windows.Add((route, transport));
        return this;
    }

    /// <summary>A model change or check the script runs by name between its requests; a throw fails the script's step.</summary>
    internal GeneratedClientLiveHost Step(string name, Action step) => Step(name, () => { step(); return Task.CompletedTask; });

    /// <inheritdoc cref="Step(string, Action)"/>
    internal GeneratedClientLiveHost Step(string name, Func<Task> step)
    {
        _steps.Add(name, step);
        return this;
    }

    internal void Attach(StreamWriter input) => _input = input;

    public void Dispose() => _output.Dispose();

    /// <summary>Handles one protocol line of the script.</summary>
    internal async Task HandleAsync(string line)
    {
        var message = JsonNode.Parse(line)!.AsObject();
        if (message["done"] is not null)
        {
            await _output.WaitAsync().ConfigureAwait(false);
            try { _input?.Close(); _input = null; }
            finally { _output.Release(); }
            return;
        }
        var id = message["id"]!.GetValue<long>();
        string? reply = null, error = null;
        try
        {
            if (message["step"] is { } step)
            {
                var name = step.GetValue<string>();
                await (_steps.TryGetValue(name, out var run) ? run() : throw new InvalidOperationException($"No step {name}.")).ConfigureAwait(false);
                reply = "{}";
            }
            else reply = await RequestAsync(message["request"]!.GetValue<string>()).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            error = failure.ToString();
        }
        await WriteAsync(new JsonObject { ["id"] = id, [error is null ? "reply" : "error"] = error ?? reply }).ConfigureAwait(false);
    }

    /// <summary>Forwards pushes until <paramref name="cancellationToken"/> is cancelled.</summary>
    internal async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await WriteAsync(null).ConfigureAwait(false);
            try { await Task.Delay(10, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<string> RequestAsync(string json)
    {
        var request = JsonNode.Parse(json)!.AsObject();
        string reply;
        if (request["kind"]?.GetValue<string>() == "hello")
        {
            // The page has one connection, which every window serves.
            string? first = null;
            foreach (var (_, transport) in _windows)
            {
                var answer = await transport.RequestAsync(json).ConfigureAwait(false);
                first ??= answer;
            }
            return first ?? throw new InvalidOperationException("No window is served.");
        }
        var route = request["route"]?.GetValue<string>() ?? "";
        var window = _windows.Where(entry => entry.Route == route).Select(entry => entry.Transport).FirstOrDefault()
            ?? (_contentRoutes.TryGetValue(route, out var content) ? content : null)
            ?? (_windows.Count == 1 ? _windows[0].Transport : throw new InvalidOperationException($"No window serves the route {route}."));
        reply = await window.RequestAsync(json).ConfigureAwait(false);
        Learn(window, reply);
        return reply;
    }

    // Content routes belong to the window whose state presented their id.
    private void Learn(InMemoryBridgeTransport window, string json)
    {
        foreach (Match match in ContentId().Matches(json))
            _contentRoutes.TryAdd("content" + match.Groups[1].Value, window);
    }

    // Writes the windows' pushes, then `message` if there is one, so a reply follows the pushes sent before it.
    private async Task WriteAsync(JsonObject? message)
    {
        await _output.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_input is null) return;
            foreach (var (_, transport) in _windows)
                foreach (var push in transport.TakeMessages())
                {
                    Learn(transport, push.Json);
                    await _input.WriteLineAsync(new JsonObject { ["push"] = push.Json }.ToJsonString()).ConfigureAwait(false);
                }
            if (message is not null) await _input.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
            await _input.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The script exited; its exit code reports why.
        }
        finally
        {
            _output.Release();
        }
    }

    [GeneratedRegex("\"id\":\"([0-9a-f]{32})\"")]
    private static partial Regex ContentId();
}
