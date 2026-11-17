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
    // The pause before a first command that got no answer is sent again, for an inspector that was still settling.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);
    private readonly InspectorConnection _connection;
    private readonly Action<string> _report;
    private readonly TimeSpan _firstAnswerTimeout;
    private int _heard;
    private int _reportedSilence;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _probes = new();
    private readonly TaskCompletionSource<string> _firstInnerTarget = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _closed = new();
    private string? _innerTarget;
    private long _outerId;
    private long _innerId;
    private int _closing;

    /// <summary>
    /// <paramref name="report"/> receives dev's line when the page does not answer the debugger's first commands within
    /// <paramref name="firstAnswerTimeout"/> (#1065).
    /// </summary>
    internal WebKitPage(InspectorConnection connection, InspectorTarget target, Action<string>? report = null, TimeSpan? firstAnswerTimeout = null)
    {
        _connection = connection;
        Target = target;
        _report = report ?? (static _ => { });
        _firstAnswerTimeout = firstAnswerTimeout ?? DefaultFirstAnswerTimeout;
    }

    /// <summary>How long the debugger's first commands wait for the page's first message before dev reports it.</summary>
    internal static readonly TimeSpan DefaultFirstAnswerTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Whether the page sent anything on its channel since it was opened: an answer or an event.</summary>
    private bool Heard => Volatile.Read(ref _heard) != 0;

    /// <summary>dev's line when the page has not answered a first command in time, and whether it is tried again.</summary>
    internal static string NoAnswerLine(string method, TimeSpan waited, bool retrying) =>
        $"[dev] Debugging: the Window's page did not answer the debugger's {method} within " +
        $"{waited.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s of attaching. " +
        (retrying
            ? "WebKit's inspector may still be settling, for example right after its Web Inspector opened; dev sends it again."
            : NoAnswerAdvice);

    /// <summary>dev's line when the page did not answer the first command sent again either.</summary>
    internal static string StillNoAnswerLine(string method) =>
        $"[dev] Debugging: the Window's page did not answer {method} sent again either, so the debugger's attach fails. " + NoAnswerAdvice;

    private const string NoAnswerAdvice =
        "Close WebKit's Web Inspector in the Window (Inspect Element) and any inspector:// page in GNOME Web, then attach " +
        "again. A Window stopped at a .NET breakpoint does not answer either. To report it, start dev with " +
        CdpTrace.Variable + "=<file> and attach again.";

    // Commands that change nothing when WebKit gets them twice: the enables and reads a debugger sends first.
    private static bool CanSendAgain(string method)
    {
        int dot = method.IndexOf('.', StringComparison.Ordinal);
        string command = dot < 0 ? method : method[(dot + 1)..];
        return command is "enable" || command.StartsWith("get", StringComparison.Ordinal);
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
            await SendAsync(method, null, quiet: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is CdpException or IOException or TimeoutException or OperationCanceledException)
        {
        }
    }

    /// <summary>Sends a WIP command to the page and returns its result; a WIP error is a <see cref="CdpException"/>.</summary>
    internal Task<JsonObject> SendAsync(string method, JsonObject? parameters, CancellationToken cancellationToken) =>
        SendAsync(method, parameters, quiet: false, cancellationToken);

    /// <summary>
    /// Sends a command. Until the page sent its first message, a command that gets no answer within the first-answer
    /// timeout is reported, sent once more after a pause when sending it twice is harmless, and fails with a clear error if
    /// that gets no answer either, instead of waiting out <see cref="CommandTimeout"/> in silence (#1065). The adapter's own
    /// <paramref name="quiet"/> commands only wait.
    /// </summary>
    private async Task<JsonObject> SendAsync(string method, JsonObject? parameters, bool quiet, CancellationToken cancellationToken)
    {
        if (!_connection.Protocol.HasCommand(method)) throw new CdpException(CdpException.MethodNotFound, $"WebKit has no '{method}'.");
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
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            await SendToPageAsync(message, linked.Token).ConfigureAwait(false);
            JsonObject reply;
            if (quiet || Heard)
            {
                reply = await answer.Task.WaitAsync(CommandTimeout, linked.Token).ConfigureAwait(false);
            }
            else
            {
                reply = await FirstAnswerAsync(method, message, answer.Task, linked.Token).ConfigureAwait(false);
            }
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

    private async Task SendToPageAsync(string message, CancellationToken cancellationToken)
    {
        string inner = _innerTarget ?? await _firstInnerTarget.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        CdpTrace.Write("wip>", message);
        string outer = new JsonObject
        {
            ["id"] = Interlocked.Increment(ref _outerId),
            ["method"] = "Target.sendMessageToTarget",
            ["params"] = new JsonObject { ["targetId"] = inner, ["message"] = message },
        }.ToJsonString();
        await _connection.SendToBackendAsync(Target, outer, cancellationToken).ConfigureAwait(false);
    }

    // A command sent before the page's first message. A page that sends anything in the meantime is alive, and the command
    // waits the rest of the usual time; one that stays silent is reported, and the command is tried once more.
    private async Task<JsonObject> FirstAnswerAsync(string method, string message, Task<JsonObject> answer, CancellationToken cancellationToken)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return await answer.WaitAsync(_firstAnswerTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) when (Heard)
        {
            return await answer.WaitAsync(waited.Elapsed < CommandTimeout ? CommandTimeout - waited.Elapsed : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        bool again = CanSendAgain(method);
        CdpTrace.Write("adapter", $"page {Target.PageId} did not answer {method} within {_firstAnswerTimeout.TotalSeconds} s{(again ? "; sending it again" : "")}");
        bool first = Interlocked.Exchange(ref _reportedSilence, 1) == 0;
        if (first) _report(NoAnswerLine(method, _firstAnswerTimeout, again));
        if (!again)
        {
            throw new CdpException(CdpException.ServerError, $"The Window's page did not answer {method}; see dotnet runic dev's output.");
        }
        await Task.Delay(SettleDelay, cancellationToken).ConfigureAwait(false);
        if (!answer.IsCompleted)
        {
            // The same ID: whichever answer comes first completes the command.
            await SendToPageAsync(message, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            return await answer.WaitAsync(_firstAnswerTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) when (Heard)
        {
            return await answer.WaitAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            CdpTrace.Write("adapter", $"page {Target.PageId} did not answer {method} sent again");
            if (Interlocked.Exchange(ref _reportedSilence, 2) != 2) _report(StillNoAnswerLine(method));
            throw new CdpException(CdpException.ServerError,
                $"The Window's page did not answer {method}, also when sent again; see dotnet runic dev's output.");
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
                Volatile.Write(ref _heard, 1);
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
