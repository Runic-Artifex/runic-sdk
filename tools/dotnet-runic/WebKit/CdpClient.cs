using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool.WebKit;

/// <summary>
/// One CDP WebSocket. On a browser connection (no direct page) the client attaches to pages with
/// <c>Target.attachToTarget {flatten}</c> and addresses them by <c>sessionId</c>; with <c>setDiscoverTargets</c> it
/// learns of pages as they come and go. On a page connection, the page is implicit. When <c>dotnet watch</c> restarts the
/// Window, an attached session continues on the new Window's page as after a reload, since js-debug ends a session whose
/// target is destroyed.
/// </summary>
internal sealed class CdpClient : IAsyncDisposable
{
    private const string BrowserSessionId = "browser";
    private readonly WebSocket _socket;
    private readonly WebKitDebugEndpoint _endpoint;
    private readonly string? _directPage;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, AttachedPage> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    // Pages of a restarted Window that continue a waiting session: never announced as new targets.
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private bool _discover;

    internal CdpClient(WebSocket socket, WebKitDebugEndpoint endpoint, string? directPage)
    {
        _socket = socket;
        _endpoint = endpoint;
        _directPage = directPage;
    }

    // A CDP session on a page. TargetId is the target the debugger knows; Page changes when the Window restarts.
    private sealed class AttachedPage(string sessionId, string targetId, WebKitPage page, CdpPageSession session, Task events)
    {
        internal string SessionId { get; } = sessionId;
        internal string TargetId { get; } = targetId;
        internal WebKitPage Page { get; set; } = page;
        internal CdpPageSession Session { get; } = session;
        internal Task Events { get; } = events;
        internal volatile bool Suspended;
        // A restarted Window's page was picked for it; set and cleared under the client's lock.
        internal bool Rebinding;
        // While the session waits: the inspector connection of the page that went away (InspectorTarget.Generation), and
        // the pages that connection listed then.
        internal long GoneGeneration;
        internal IReadOnlySet<string> GonePeers = new HashSet<string>(StringComparer.Ordinal);
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        _endpoint.ClientConnected(this);
        try
        {
            if (_directPage is not null)
            {
                InspectorTarget target = (await _endpoint.ListPagesAsync(linked.Token, this).ConfigureAwait(false))
                    .FirstOrDefault(page => page.PageId == _directPage)
                    ?? throw new IOException($"No page {_directPage}.");
                await AttachAsync(target, "", linked.Token).ConfigureAwait(false);
            }
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            while (_socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                message.SetLength(0);
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(buffer.AsMemory(), linked.Token).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, received.Count);
                    if (message.Length > 64 * 1024 * 1024) return;
                } while (!received.EndOfMessage);
                JsonObject? command;
                try
                {
                    command = JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length)) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }
                if (command is not null)
                {
                    CdpTrace.Write("cdp>", command.ToJsonString());
                    _ = HandleAsync(command, linked.Token);
                }
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException)
        {
        }
        finally
        {
            _endpoint.ClientDisconnected(this);
        }
    }

    private async Task HandleAsync(JsonObject command, CancellationToken cancellationToken)
    {
        if (command["id"] is not JsonValue idValue || !idValue.TryGetValue(out long id)) return;
        string method = command["method"]?.GetValue<string>() ?? "";
        JsonObject parameters = command["params"] as JsonObject ?? [];
        string? sessionId = command["sessionId"]?.GetValue<string>() ?? (_directPage is null ? null : "");
        var reply = new JsonObject { ["id"] = id };
        if (!string.IsNullOrEmpty(sessionId)) reply["sessionId"] = sessionId;
        AttachedPage? page = null;
        try
        {
            if (sessionId is not null) lock (_gate) _sessions.TryGetValue(sessionId, out page);
            reply["result"] = page is not null
                ? await page.Session.HandleAsync(method, parameters, cancellationToken).ConfigureAwait(false)
                : await HandleBrowserAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (CdpException error)
        {
            reply["error"] = new JsonObject { ["code"] = error.Code, ["message"] = error.Message };
        }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or JsonException or FormatException)
        {
            reply["error"] = new JsonObject { ["code"] = CdpException.ServerError, ["message"] = error.Message };
        }
        catch (OperationCanceledException)
        {
            page?.Session.ReplyDropped(method);
            return;
        }
        await SendAsync(reply).ConfigureAwait(false);
        page?.Session.Replied(method);
    }

    private async Task<JsonObject> HandleBrowserAsync(string method, JsonObject p, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "Target.attachToBrowserTarget":
                return new() { ["sessionId"] = BrowserSessionId };
            case "Target.setDiscoverTargets":
                lock (_gate) _discover = p["discover"]?.GetValue<bool>() ?? false;
                // Connects to the Window's inspector if needed, so the pages are known.
                await _endpoint.ListPagesAsync(cancellationToken, this).ConfigureAwait(false);
                if (_discover) await TargetsChangedAsync().ConfigureAwait(false);
                return [];
            case "Target.getTargets":
            {
                IReadOnlyList<InspectorTarget> pages = await _endpoint.ListPagesAsync(cancellationToken, this).ConfigureAwait(false);
                return new() { ["targetInfos"] = new JsonArray([.. pages.Select(page => (JsonNode)TargetInfo(page, IsAttached(page)))]) };
            }
            case "Target.getTargetInfo" when p["targetId"] is null:
                return new() { ["targetInfo"] = new JsonObject { ["targetId"] = "browser", ["type"] = "browser", ["title"] = "", ["url"] = "", ["attached"] = true } };
            case "Target.attachToTarget":
            {
                string targetId = p["targetId"]?.GetValue<string>() ?? "";
                InspectorTarget target = (await _endpoint.ListPagesAsync(cancellationToken, this).ConfigureAwait(false))
                    .FirstOrDefault(page => page.PageId == targetId)
                    ?? throw new CdpException(CdpException.InvalidParams, "No target with given id found");
                string sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
                await AttachAsync(target, sessionId, cancellationToken).ConfigureAwait(false);
                return new() { ["sessionId"] = sessionId };
            }
            case "Target.detachFromTarget":
                if (p["sessionId"]?.GetValue<string>() is { } detach) await DetachAsync(detach, notify: false).ConfigureAwait(false);
                return [];
            case "Browser.getVersion":
                return CdpBrowser.Version();
            case "Target.setAutoAttach" or "Target.setRemoteLocations" or "Browser.setDownloadBehavior":
                return [];
            default:
                throw new CdpException(CdpException.MethodNotFound, $"'{method}' wasn't found");
        }
    }

    private bool IsAttached(InspectorTarget target)
    {
        lock (_gate) return _sessions.Values.Any(attached => attached.Page.Target.PageId == target.PageId);
    }

    private static JsonObject TargetInfo(InspectorTarget target, bool attached, string? targetId = null) => new()
    {
        ["targetId"] = targetId ?? target.PageId,
        ["type"] = "page",
        ["title"] = target.Name,
        ["url"] = target.Url,
        ["attached"] = attached,
        ["canAccessOpener"] = false,
        ["browserContextId"] = "default",
    };

    private async Task AttachAsync(InspectorTarget target, string sessionId, CancellationToken cancellationToken)
    {
        (WebKitPage page, WebKitProtocol protocol) = await _endpoint.OpenPageAsync(target, cancellationToken).ConfigureAwait(false);
        AttachedPage? attached = null;
        var session = new CdpPageSession(page.SendAsync, (method, parameters) => EmitAsync(sessionId, method, parameters), protocol);
        attached = new AttachedPage(sessionId, target.PageId, page, session, Task.Run(() => session.RunAsync(_stop.Token), CancellationToken.None));
        Bind(attached, page);
        lock (_gate) _sessions[sessionId] = attached;
        if (sessionId.Length > 0)
        {
            await EmitAsync(BrowserSessionId, "Target.attachedToTarget", new()
            {
                ["sessionId"] = sessionId,
                ["targetInfo"] = TargetInfo(target, attached: true),
                ["waitingForDebugger"] = false,
            }).ConfigureAwait(false);
        }
    }

    private void Bind(AttachedPage attached, WebKitPage page)
    {
        page.EventReceived += attached.Session.Post;
        page.Detached += () => PageGone(attached, page);
    }

    /// <summary>
    /// The page went away. When its Window restarts (<c>dotnet watch</c>), the session waits for the new Window's page and
    /// continues on it like after a reload, so the debugger stays attached; a page that another page replaced in a Window
    /// that keeps running ends its session, and so does a restart that does not come (<see cref="ClosedInRunningWindow"/>). When another client took WebKit's inspector, the session ends as Chrome ends one whose page
    /// another DevTools took over: <c>Inspector.detached</c>, then <c>Target.detachedFromTarget</c>.
    /// </summary>
    private void PageGone(AttachedPage attached, WebKitPage page)
    {
        lock (_gate)
        {
            if (attached.Page != page || attached.Suspended || !_sessions.ContainsKey(attached.SessionId)) return;
            attached.Suspended = true;
        }
        if (page.ConnectionReplaced)
        {
            CdpTrace.Write("adapter", $"another client took the inspector of page {page.Target.PageId}; session {attached.SessionId} ends");
            _ = EndAsync(attached.SessionId, ReplacedReason);
            return;
        }
        attached.GoneGeneration = page.Target.Generation;
        attached.GonePeers = _endpoint.ConnectedGeneration == page.Target.Generation
            ? _endpoint.KnownPages.Where(known => known.Generation == page.Target.Generation).Select(static known => known.PageId).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        CdpTrace.Write("adapter", $"page {page.Target.PageId} went away; session {attached.SessionId} waits for a restarted Window");
        attached.Session.Suspend();
        _ = page.DisposeAsync().AsTask();
        // Whether the Window restarts or the page closed in a Window that keeps running is decided by events of the
        // Window's inspector connection (ClosedInRunningWindow, TargetsChangedAsync), not by a timer (#1210): the page
        // leaves the list before its Window's process exits, and on a slow machine that exit can take seconds. The
        // session waits while that process runs; only a restart that never comes ends it, after RestartTimeout, and so
        // does dev's stop.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RestartTimeout, _stop.Token).ConfigureAwait(false);
                if (attached.Suspended) await EndAsync(attached.SessionId).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Whether the page that <paramref name="attached"/> waits for a replacement of closed for good: its Window's
    /// inspector connection is still open and lists a page it did not list when this one went away, so the Window
    /// process runs on with another page in its place. When the connection closes instead, the Window process has
    /// exited, and the session waits for the restarted Window's page on a later connection
    /// (<see cref="TargetsChangedAsync"/>). Pages that were listed already prove nothing: a Window process that exits
    /// can drop its pages one at a time before its connection closes.
    /// </summary>
    private bool ClosedInRunningWindow(AttachedPage attached) =>
        ClosedInRunningWindow(_endpoint.ConnectedGeneration, _endpoint.KnownPages, attached.GoneGeneration, attached.GonePeers);

    /// <summary>The decision of <see cref="ClosedInRunningWindow(AttachedPage)"/>, from the endpoint's state.</summary>
    internal static bool ClosedInRunningWindow(long? connectedGeneration, IReadOnlyList<InspectorTarget> pages, long goneGeneration,
        IReadOnlySet<string> gonePeers) =>
        connectedGeneration == goneGeneration &&
        pages.Any(page => page.Generation == goneGeneration && !gonePeers.Contains(page.PageId));

    // Chrome's Inspector.detached reason when another DevTools client takes the page over.
    internal const string ReplacedReason = "replaced_with_devtools";
    private static readonly TimeSpan RestartTimeout = TimeSpan.FromMinutes(3);

    // Continues a waiting session on a page of the restarted Window.
    private async Task RebindAsync(AttachedPage attached, InspectorTarget target)
    {
        try
        {
            (WebKitPage page, WebKitProtocol protocol) = await _endpoint.OpenPageAsync(target, _stop.Token).ConfigureAwait(false);
            lock (_gate) attached.Page = page;
            Bind(attached, page);
            CdpTrace.Write("adapter", $"session {attached.SessionId} continues on {target.PageId}");
            await attached.Session.ResumeAsync(page.SendAsync, protocol).ConfigureAwait(false);
            lock (_gate)
            {
                attached.Suspended = false;
                attached.Rebinding = false;
            }
        }
        catch (Exception error) when (error is IOException or TimeoutException or CdpException or OperationCanceledException)
        {
            CdpTrace.Write("adapter", $"session {attached.SessionId} could not continue: {error.Message}");
            await EndAsync(attached.SessionId).ConfigureAwait(false);
        }
    }

    private Task DetachAsync(string sessionId, bool notify) => notify ? EndAsync(sessionId) : RemoveAsync(sessionId);

    // The debugger detached: no events for it.
    private async Task RemoveAsync(string sessionId)
    {
        AttachedPage? attached;
        lock (_gate)
        {
            if (!_sessions.Remove(sessionId, out attached)) return;
        }
        attached.Session.Complete();
        await attached.Page.DisposeAsync().ConfigureAwait(false);
    }

    // The page is gone for good: the debugger learns that its target detached and was destroyed. With a reason, the page's
    // session hears Inspector.detached first.
    private async Task EndAsync(string sessionId, string? detachReason = null)
    {
        AttachedPage? attached;
        lock (_gate)
        {
            if (!_sessions.Remove(sessionId, out attached)) return;
            _announced.Remove(attached.TargetId);
        }
        attached.Session.Complete();
        await attached.Page.DisposeAsync().ConfigureAwait(false);
        if (detachReason is not null) await EmitAsync(sessionId, "Inspector.detached", new() { ["reason"] = detachReason }).ConfigureAwait(false);
        if (sessionId.Length > 0)
        {
            await EmitAsync(BrowserSessionId, "Target.detachedFromTarget", new() { ["sessionId"] = sessionId, ["targetId"] = attached.TargetId }).ConfigureAwait(false);
            await EmitAsync(BrowserSessionId, "Target.targetDestroyed", new() { ["targetId"] = attached.TargetId }).ConfigureAwait(false);
        }
        else
        {
            // A page connection has nothing left to debug.
            _stop.Cancel();
        }
    }

    /// <summary>
    /// The Window's pages changed: sessions whose page went away wait for a restarted Window's page; a discovering client
    /// learns of the other pages that came and went.
    /// </summary>
    internal async Task TargetsChangedAsync()
    {
        IReadOnlyList<InspectorTarget> pages = _endpoint.KnownPages;
        foreach (AttachedPage attached in Snapshot().Where(attached => !attached.Suspended && !attached.Page.IsListedIn(pages)))
        {
            attached.Page.TargetRemoved();
        }
        List<AttachedPage> closed;
        lock (_gate) closed = [.. _sessions.Values.Where(attached => attached.Suspended && !attached.Rebinding && ClosedInRunningWindow(attached))];
        foreach (AttachedPage attached in closed)
        {
            CdpTrace.Write("adapter", $"the Window of session {attached.SessionId} runs on with another page; the session ends");
            await EndAsync(attached.SessionId).ConfigureAwait(false);
        }
        var rebinds = new List<(AttachedPage, InspectorTarget)>();
        List<string> gone;
        List<InspectorTarget> added;
        bool discover;
        lock (_gate)
        {
            discover = _discover;
            var used = new HashSet<string>(_sessions.Values.Select(static attached => attached.Page.Target.PageId), StringComparer.Ordinal);
            foreach (AttachedPage waiting in _sessions.Values.Where(static attached => attached.Suspended && !attached.Rebinding))
            {
                InspectorTarget? next = pages
                    .Where(page => page.Generation > waiting.Page.Target.Generation && !used.Contains(page.PageId) && !_announced.Contains(page.PageId))
                    .OrderByDescending(page => SameDocument(page, waiting.Page.Target))
                    .FirstOrDefault();
                if (next is null) continue;
                waiting.Rebinding = true;
                used.Add(next.PageId);
                _consumed.Add(next.PageId);
                rebinds.Add((waiting, next));
            }
            var current = new HashSet<string>(pages.Select(static page => page.PageId), StringComparer.Ordinal);
            var attachedIds = new HashSet<string>(_sessions.Values.Select(static attached => attached.TargetId), StringComparer.Ordinal);
            gone = _announced.Where(id => !current.Contains(id) && !attachedIds.Contains(id)).ToList();
            added = discover ? pages.Where(page => !_announced.Contains(page.PageId) && !_consumed.Contains(page.PageId)).ToList() : [];
            foreach (string id in gone) _announced.Remove(id);
            foreach (InspectorTarget page in added) _announced.Add(page.PageId);
        }
        foreach ((AttachedPage waiting, InspectorTarget next) in rebinds) await RebindAsync(waiting, next).ConfigureAwait(false);
        if (!discover) return;
        foreach (string id in gone) await EmitAsync(BrowserSessionId, "Target.targetDestroyed", new() { ["targetId"] = id }).ConfigureAwait(false);
        foreach (InspectorTarget page in added)
            await EmitAsync(BrowserSessionId, "Target.targetCreated", new() { ["targetInfo"] = TargetInfo(page, attached: false) }).ConfigureAwait(false);
    }

    // The restarted Window's page for a waiting session: the same document (the surface's random path segment aside) and
    // title first.
    private static int SameDocument(InspectorTarget candidate, InspectorTarget previous) =>
        (DocumentPath(candidate.Url) == DocumentPath(previous.Url) ? 2 : 0) + (candidate.Name == previous.Name ? 1 : 0);

    private static string DocumentPath(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? string.Join('/', uri.AbsolutePath.Split('/').Where(static segment => !segment.StartsWith("surface-", StringComparison.Ordinal))) : url;

    private List<AttachedPage> Snapshot()
    {
        lock (_gate) return [.. _sessions.Values];
    }

    private Task EmitAsync(string? sessionId, string method, JsonObject parameters)
    {
        var message = new JsonObject { ["method"] = method, ["params"] = parameters };
        if (!string.IsNullOrEmpty(sessionId) && !(sessionId == BrowserSessionId && _directPage is not null)) message["sessionId"] = sessionId;
        return SendAsync(message);
    }

    private async Task SendAsync(JsonObject message)
    {
        string text = message.ToJsonString();
        CdpTrace.Write("cdp<", text);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await _send.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is WebSocketException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            _send.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (AttachedPage attached in Snapshot()) await DetachAsync(attached.SessionId, notify: false).ConfigureAwait(false);
        foreach (AttachedPage attached in Snapshot()) await attached.Events.ConfigureAwait(false);
        _stop.Dispose();
        _send.Dispose();
    }
}
