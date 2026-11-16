using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool.WebKit;

/// <summary>
/// The WebKit Inspector Protocol channel to one page target. A page's backend multiplexes through
/// <c>Target.sendMessageToTarget</c> / <c>Target.dispatchMessageFromTarget</c> to the page's inner target, which changes
/// when a navigation swaps the web process (<c>Target.didCommitProvisionalTarget</c>).
/// </summary>
internal sealed class WebKitPage : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private readonly InspectorConnection _connection;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _probes = new();
    private readonly TaskCompletionSource<string> _firstInnerTarget = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _closed = new();
    private string? _innerTarget;
    private long _outerId;
    private long _innerId;
    private int _closing;

    internal WebKitPage(InspectorConnection connection, InspectorTarget target)
    {
        _connection = connection;
        Target = target;
    }

    internal InspectorTarget Target { get; }

    /// <summary>A WIP event of the page. Raised on the connection's read loop.</summary>
    internal event Action<string, JsonObject>? EventReceived;

    /// <summary>The page went away: the Window closed, navigated away from the target or the connection closed.</summary>
    internal event Action? Detached;

    /// <summary>Another client took WebKit's slot from the page's connection (<see cref="InspectorConnection.Abandon"/>).</summary>
    internal bool ConnectionReplaced => _connection.IsReplaced;

    /// <summary>Attaches as the page's frontend, as WebKit's own inspector does.</summary>
    internal async Task OpenAsync(CancellationToken cancellationToken)
    {
        _connection.MessageToFrontend += OnMessage;
        _connection.Closed += OnDetached;
        await _connection.SetupAsync(Target, cancellationToken).ConfigureAwait(false);
        await _firstInnerTarget.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        // Probed from now on: WebKit answers a page's probe once the page is set up.
        _connection.PageOpened(this);
        // WebKit's own frontend sends these first; a target that waits for its frontend then continues.
        if (_connection.Protocol.HasCommand("Inspector.enable")) _ = SendQuietlyAsync("Inspector.enable");
        if (_connection.Protocol.HasCommand("Inspector.initialized")) _ = SendQuietlyAsync("Inspector.initialized");
    }

    private async Task SendQuietlyAsync(string method)
    {
        try
        {
            await SendAsync(method, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is CdpException or IOException or TimeoutException or OperationCanceledException)
        {
        }
    }

    /// <summary>Sends a WIP command to the page and returns its result; a WIP error is a <see cref="CdpException"/>.</summary>
    internal async Task<JsonObject> SendAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        if (!_connection.Protocol.HasCommand(method)) throw new CdpException(CdpException.MethodNotFound, $"WebKit has no '{method}'.");
        string inner = _innerTarget ?? await _firstInnerTarget.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        long id = Interlocked.Increment(ref _innerId);
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            // WIP rejects null for optional parameters: they are left out.
            var clean = new JsonObject();
            foreach ((string name, JsonNode? value) in parameters ?? [])
            {
                if (value is not null) clean[name] = value.DeepClone();
            }
            string message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = clean }.ToJsonString();
            CdpTrace.Write("wip>", message);
            string outer = new JsonObject
            {
                ["id"] = Interlocked.Increment(ref _outerId),
                ["method"] = "Target.sendMessageToTarget",
                ["params"] = new JsonObject { ["targetId"] = inner, ["message"] = message },
            }.ToJsonString();
            await _connection.SendToBackendAsync(Target, outer, cancellationToken).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            JsonObject reply = await answer.Task.WaitAsync(CommandTimeout, linked.Token).ConfigureAwait(false);
            if (reply["error"] is JsonObject error)
            {
                throw new CdpException(error["code"]?.GetValue<int>() ?? CdpException.ServerError,
                    error["message"]?.GetValue<string>() ?? "WebKit reported an error.");
            }
            return reply["result"] as JsonObject ?? [];
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new CdpException(CdpException.ServerError, "The page closed.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Whether WebKit still sends this page's messages to this connection: a command for an unknown domain on the page's
    /// own channel, which the Window's UI process answers with an error even while the page is paused or busy. When
    /// another client holds WebKit's slot, the answer goes to that client, or nowhere once it left; a Window whose main
    /// thread is stopped, for example at a .NET breakpoint, does not answer either.
    /// </summary>
    internal async Task<bool> ProbeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref _outerId);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _probes[id] = answer;
        try
        {
            await _connection.SendToBackendAsync(Target, new JsonObject { ["id"] = id, ["method"] = "Runic.probe" }.ToJsonString(), cancellationToken)
                .ConfigureAwait(false);
            await answer.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is TimeoutException or IOException)
        {
            return false;
        }
        finally
        {
            _probes.TryRemove(id, out _);
        }
    }

    private void OnMessage(ulong connectionId, ulong targetId, string text)
    {
        if (connectionId != Target.ConnectionId || targetId != Target.TargetId) return;
        JsonObject message;
        try
        {
            message = JsonNode.Parse(text) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return;
        }
        // The answer to a command on the page's own channel: Target.sendMessageToTarget's, or a probe's.
        if (message["id"] is JsonValue outerValue && outerValue.TryGetValue(out long outer))
        {
            if (_probes.TryGetValue(outer, out TaskCompletionSource? probe)) probe.TrySetResult();
            return;
        }
        JsonObject? p = message["params"] as JsonObject;
        string? createdType = p is null ? null : p["targetInfo"]?["type"]?.GetValue<string>();
        switch (message["method"]?.GetValue<string>())
        {
            case "Target.targetCreated" when createdType == "page":
                if (!(p!["targetInfo"]!["isProvisional"]?.GetValue<bool>() ?? false) && _innerTarget is null)
                {
                    _innerTarget = p["targetInfo"]!["targetId"]?.GetValue<string>();
                    if (_innerTarget is not null) _firstInnerTarget.TrySetResult(_innerTarget);
                }
                return;
            case "Target.didCommitProvisionalTarget":
                _innerTarget = p?["newTargetId"]?.GetValue<string>() ?? _innerTarget;
                return;
            case "Target.targetDestroyed":
                return;
            case "Target.dispatchMessageFromTarget":
                if (p?["message"]?.GetValue<string>() is not { } innerText) return;
                CdpTrace.Write("wip<", innerText);
                JsonObject inner;
                try
                {
                    inner = JsonNode.Parse(innerText) as JsonObject ?? [];
                }
                catch (JsonException)
                {
                    return;
                }
                if (inner["id"] is JsonValue idValue && idValue.TryGetValue(out long id))
                {
                    if (_pending.TryGetValue(id, out TaskCompletionSource<JsonObject>? answer)) answer.TrySetResult(inner);
                }
                else if (inner["method"]?.GetValue<string>() is { } method)
                {
                    EventReceived?.Invoke(method, inner["params"] as JsonObject ?? []);
                }
                return;
        }
    }

    private void OnDetached()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0) return;
        _closed.Cancel();
        Detached?.Invoke();
    }

    /// <summary>The target left the server's list: the page closed.</summary>
    internal void TargetRemoved() => OnDetached();

    public async ValueTask DisposeAsync()
    {
        bool open = Interlocked.Exchange(ref _closing, 1) == 0;
        _connection.MessageToFrontend -= OnMessage;
        _connection.Closed -= OnDetached;
        _connection.PageClosed(this);
        _closed.Cancel();
        if (open && !_connection.IsClosed)
        {
            try
            {
                await _connection.FrontendDidCloseAsync(Target, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Whether a target list still has this page.</summary>
    internal bool IsListedIn(System.Collections.Generic.IEnumerable<InspectorTarget> targets) =>
        targets.Any(target => target.PageId == Target.PageId);
}
