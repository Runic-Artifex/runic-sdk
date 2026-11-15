// SPIKE (runic-sdk#1029): serves a Chrome DevTools Protocol endpoint for WebKitGTK pages.
// Discovery: GET /json/version, /json/list (/json). WebSockets: /devtools/browser/<secret> (flat sessions, what VS Code's
// js-debug uses) and /devtools/page/<secret>/<pageId> (one page, no sessions).
// Security: loopback bind only; Host must be an IP literal or localhost (DNS rebinding); a request carrying an Origin
// header is refused unless the Origin is in --allow-origin (browser pages cannot reach it); the WebSocket paths carry
// a random secret that only /json responses reveal, which a browser page cannot read cross-origin.
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Runic.Spike.WebKitCdp;

internal static class CdpServer
{
    internal static async Task RunAsync(InspectorSocket socket, int port, IReadOnlySet<string>? allowedOrigins = null)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            var host = context.Request.Host.Host;
            var hostOk = host is "localhost" || IPAddress.TryParse(host.Trim('[', ']'), out _);
            var origin = context.Request.Headers.Origin.ToString();
            var originOk = origin.Length == 0 || (allowedOrigins?.Contains(origin) ?? false);
            if (!hostOk || !originOk)
            {
                Log.Write("adapter", $"refused {context.Request.Path} Host={host} Origin={origin}");
                context.Response.StatusCode = 403;
                return;
            }
            await next();
        });
        string Ws(HttpContext c, string path) => $"ws://{c.Request.Host}/devtools/{path}";
        app.MapGet("/json/version", (HttpContext c) => Results.Json(new JsonObject
        {
            ["Browser"] = "WebKitGTK (Runic WebKit CDP adapter spike)",
            ["Protocol-Version"] = "1.3",
            ["User-Agent"] = "WebKitGTK",
            ["webSocketDebuggerUrl"] = Ws(c, $"browser/{secret}"),
        }));
        IResult List(HttpContext c) => Results.Json(new JsonArray(socket.Targets.Where(t => t.Type == "WebPage").Select(t => (JsonNode)new JsonObject
        {
            ["description"] = "",
            ["id"] = PageId(t),
            ["title"] = t.Name,
            ["type"] = "page",
            ["url"] = t.Url,
            ["webSocketDebuggerUrl"] = Ws(c, $"page/{secret}/{PageId(t)}"),
        }).ToArray()));
        app.MapGet("/json/list", List);
        app.MapGet("/json", List);
        app.Map("/devtools/browser/{key}", async (HttpContext c, string key) =>
        {
            if (!c.WebSockets.IsWebSocketRequest || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(key), Encoding.ASCII.GetBytes(secret))) { c.Response.StatusCode = 404; return; }
            using var ws = await c.WebSockets.AcceptWebSocketAsync();
            await new CdpConnection(socket, ws, null).RunAsync();
        });
        app.Map("/devtools/page/{key}/{pageId}", async (HttpContext c, string key, string pageId) =>
        {
            if (!c.WebSockets.IsWebSocketRequest || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(key), Encoding.ASCII.GetBytes(secret))) { c.Response.StatusCode = 404; return; }
            using var ws = await c.WebSockets.AcceptWebSocketAsync();
            await new CdpConnection(socket, ws, pageId).RunAsync();
        });
        Log.Write("adapter", $"CDP endpoint http://127.0.0.1:{port}/ for WebKit's inspector");
        await app.RunAsync();
    }

    internal static string PageId(InspectorTarget t) => $"wk-{t.ConnectionId}-{t.TargetId}";
}

// One CDP client. With pageId == null it is a browser connection with flat sessions; otherwise the page is implicit.
internal sealed class CdpConnection
{
    private readonly InspectorSocket _socket;
    private readonly WebSocket _ws;
    private readonly string? _directPage;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly Dictionary<string, WipPage> _sessions = new();
    private const string BrowserSession = "browser";

    internal CdpConnection(InspectorSocket socket, WebSocket ws, string? directPage)
    {
        _socket = socket;
        _ws = ws;
        _directPage = directPage;
    }

    internal async Task RunAsync()
    {
        Log.Write("adapter", $"CDP client connected ({(_directPage is null ? "browser" : "page " + _directPage)})");
        if (_directPage is not null && Find(_directPage) is { } direct) _sessions[""] = await AttachAsync(direct, "");
        var buffer = new byte[1 << 20];
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) goto done;
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                var text = Encoding.UTF8.GetString(ms.ToArray());
                Log.Write("cdp>", text);
                _ = HandleAsync(JsonNode.Parse(text)!.AsObject());
            }
        }
        catch (WebSocketException) { }
        done:
        foreach (var page in _sessions.Values.Distinct()) await page.CloseAsync();
        Log.Write("adapter", "CDP client disconnected");
    }

    private InspectorTarget? Find(string pageId) => _socket.Targets.FirstOrDefault(t => CdpServer.PageId(t) == pageId);

    internal async Task SendAsync(JsonObject message)
    {
        var text = message.ToJsonString();
        Log.Write("cdp<", text);
        await _send.WaitAsync();
        try { if (_ws.State == WebSocketState.Open) await _ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _send.Release(); }
    }

    internal Task EmitAsync(string? sessionId, string method, JsonObject @params)
    {
        var m = new JsonObject { ["method"] = method, ["params"] = @params };
        if (!string.IsNullOrEmpty(sessionId)) m["sessionId"] = sessionId;
        return SendAsync(m);
    }

    private static JsonObject TargetInfo(InspectorTarget t, bool attached) => new()
    {
        ["targetId"] = CdpServer.PageId(t), ["type"] = "page", ["title"] = t.Name, ["url"] = t.Url, ["attached"] = attached,
        ["canAccessOpener"] = false, ["browserContextId"] = "default",
    };

    private async Task<WipPage> AttachAsync(InspectorTarget target, string sessionId)
    {
        var page = new WipPage(_socket, target, this, sessionId);
        await page.SetupAsync();
        return page;
    }

    private async Task HandleAsync(JsonObject m)
    {
        var id = m["id"]!.GetValue<long>();
        var method = m["method"]!.GetValue<string>();
        var p = m["params"] as JsonObject ?? new JsonObject();
        var sessionId = m["sessionId"]?.GetValue<string>() ?? (_directPage is null ? null : "");
        JsonObject reply = new() { ["id"] = id };
        if (!string.IsNullOrEmpty(sessionId) || _directPage is not null) reply["sessionId"] = sessionId is "" ? null : sessionId;
        if (reply["sessionId"] is null) reply.Remove("sessionId");
        try
        {
            JsonObject result;
            if (sessionId is not null && _sessions.TryGetValue(sessionId, out var page))
                result = await page.HandleAsync(method, p);
            else
                result = await HandleBrowserAsync(method, p);
            reply["result"] = result;
        }
        catch (CdpError error)
        {
            reply["error"] = new JsonObject { ["code"] = error.Code, ["message"] = error.Message };
        }
        catch (Exception error)
        {
            Log.Write("adapter", $"{method} failed: {error}");
            reply["error"] = new JsonObject { ["code"] = -32000, ["message"] = error.Message };
        }
        await SendAsync(reply);
    }

    private async Task<JsonObject> HandleBrowserAsync(string method, JsonObject p)
    {
        switch (method)
        {
            case "Target.attachToBrowserTarget":
                return new() { ["sessionId"] = BrowserSession };
            case "Target.setDiscoverTargets":
                foreach (var t in _socket.Targets.Where(t => t.Type == "WebPage"))
                    _ = EmitAsync(BrowserSession, "Target.targetCreated", new() { ["targetInfo"] = TargetInfo(t, false) });
                return new();
            case "Target.getTargets":
                return new() { ["targetInfos"] = new JsonArray(_socket.Targets.Where(t => t.Type == "WebPage").Select(t => (JsonNode)TargetInfo(t, false)).ToArray()) };
            case "Target.getTargetInfo":
                return new() { ["targetInfo"] = new JsonObject { ["targetId"] = "browser", ["type"] = "browser", ["title"] = "", ["url"] = "", ["attached"] = true } };
            case "Target.attachToTarget":
            {
                var target = Find(p["targetId"]!.GetValue<string>()) ?? throw new CdpError(-32602, "No target with given id found");
                var sessionId = "s-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
                _sessions[sessionId] = await AttachAsync(target, sessionId);
                _ = EmitAsync(BrowserSession, "Target.attachedToTarget", new()
                {
                    ["sessionId"] = sessionId, ["targetInfo"] = TargetInfo(target, true), ["waitingForDebugger"] = false,
                });
                return new() { ["sessionId"] = sessionId };
            }
            case "Target.detachFromTarget":
                if (p["sessionId"]?.GetValue<string>() is { } s && _sessions.Remove(s, out var detached)) await detached.CloseAsync();
                return new();
            case "Browser.getVersion":
                return new() { ["protocolVersion"] = "1.3", ["product"] = "WebKitGTK", ["revision"] = "", ["userAgent"] = "WebKitGTK", ["jsVersion"] = "" };
            case "Target.setAutoAttach":
            case "Target.setRemoteLocations":
            case "Browser.setDownloadBehavior":
                return new();
            default:
                throw new CdpError(-32601, $"'{method}' wasn't found");
        }
    }
}

internal sealed class CdpError(int code, string message) : Exception(message)
{
    internal int Code { get; } = code;
}
