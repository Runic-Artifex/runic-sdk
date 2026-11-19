using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using Runic.Application.Tool;
using Runic.Application.Tool.WebKit;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

/// <summary><c>dotnet runic dev --browser</c> (#1031, D066.38 item 4): the serve-only Window, opened by the IDE's browser.</summary>
internal sealed partial class ToolTests
{
    private const string SurfaceUrl = "http://127.0.0.1:41234/?credential=secret";

    [Test, DisplayName("--browser conflicts with --no-window and --webkit-inspector")]
    public async Task BrowserOptionConflicts()
    {
        foreach (string[] arguments in new[] { new[] { "--browser", "--no-window" }, ["--browser", "--webkit-inspector"] })
        {
            var console = new CapturingConsole();
            int exitCode = await Runic.Application.Tool.Program.RunAsync(["dev", .. arguments, "--project", "missing.csproj"], console);
            Equal(Runic.Application.Tool.Program.UsageFailure, exitCode);
        }
        // The same rule for options that are not read from a command line.
        var browser = new DevOptions(null, "Debug", true, true, true, false, []) { Browser = true };
        ToolOptionValues.RequireWindowOptions(browser);
        Contains(Throws<DevUsageException>(() => ToolOptionValues.RequireWindowOptions(browser with { StartWindow = false })).Message, "--browser");
    }

    [Test, DisplayName("--browser opens no WebKit or WebView2 endpoint and serves the Window serve-only")]
    public void BrowserOpensNoEngineEndpoint()
    {
        using var workspace = new TestWorkspace();
        DevProjectConfiguration desktop = CreateConfiguration(workspace, "vite") with { ApplicationHosts = ["desktop"] };
        var browser = new DevOptions(null, "Debug", true, true, true, false, []) { Browser = true };
        Equal(WebKitDebugging.Plan.None, WebKitDebugging.Decide(desktop, browser, static _ => null, true, () => 40000, () => null));
        // WebView2's variable opens nothing in a Window that has no WebView2.
        var session = DevSession.Create(desktop, browser with
        {
            Environment = [new(DevSession.WebView2ArgumentsVariable, "--remote-debugging-port=9222")],
        }, [WebKitDebugging.CdpEndpoint(9222)]);
        session.Start();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(session.Path));
        Equal(0, document.RootElement.GetProperty("debugEndpoints").GetArrayLength());

        // RUNIC_APPLICATION_SERVE_ONLY=1 whatever -e says; without --browser, -e decides.
        IReadOnlyDictionary<string, string?> served = HostProcessController.CreateDevelopmentEnvironment(desktop,
            new Dictionary<string, string?>(), static _ => null, [new(BrowserEntry.ServeOnlyVariable, "0"), new(BrowserEntry.ServeOriginVariable, "http://127.0.0.1:1")],
            browser: true, browserOrigin: "http://127.0.0.1:9224");
        Equal("1", served[BrowserEntry.ServeOnlyVariable]);
        // The Window admits --browser's address, and a Runic Desktop page's Bridge connects through it (#1187, D077.14).
        Equal("http://127.0.0.1:9224", served[BrowserEntry.ServeOriginVariable]);
        IReadOnlyDictionary<string, string?> chosen = HostProcessController.CreateDevelopmentEnvironment(desktop,
            new Dictionary<string, string?>(), static _ => null, [new(BrowserEntry.ServeOnlyVariable, "1")]);
        Equal("1", chosen[BrowserEntry.ServeOnlyVariable]);
        False(HostProcessController.CreateDevelopmentEnvironment(desktop, new Dictionary<string, string?>(), static _ => null)
            .ContainsKey(BrowserEntry.ServeOnlyVariable), "dev sets serve-only only with --browser");
    }


    // A Runic Desktop surface's page, below its surface path, and a second process's after a restart.
    private const string DesktopSurfaceUrl = "http://127.0.0.1:41234/surface-a/index.html";
    private const string RestartedSurfaceUrl = "http://127.0.0.1:41236/surface-b/index.html";

    private static BrowserEntry.Request BrowserRequest(string target = "/", string? host = "127.0.0.1:9224", string? origin = null,
        string? site = "none", string? cookie = BrowserEntry.CookieName + "=bound", string method = "GET", bool upgrade = false) =>
        new(method, target, host, origin, site, cookie, upgrade);

    [Test, DisplayName("the browser entry admits a browser by its one-time code and then only that browser's own requests")]
    public void BrowserEntryPolicy()
    {
        Uri[] targets = [new(SurfaceUrl), new("http://127.0.0.1:41235/?credential=other")];
        var state = new BrowserEntry.State("the-code", "bound", targets, new Dictionary<string, int>());
        BrowserEntry.Decision Decide(BrowserEntry.Request request, BrowserEntry.State? with = null) => BrowserEntry.Decide(request, with ?? state);

        // The code admits a browser, also from the code file's page (a file: document, so cross-site) and without a cookie.
        foreach (string? site in new[] { "cross-site", "none", null })
            Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Admit, Location: "/"), Decide(BrowserRequest("/?code=the-code", site: site, cookie: null)));
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Admit, Location: "/windows/2"),
            Decide(BrowserRequest("/windows/2?code=the-code", origin: "null", site: "cross-site", cookie: null)));
        Equal(BrowserEntry.Outcome.Admit, Decide(BrowserRequest("/?code=the-code", method: "HEAD", cookie: null)).Outcome);
        // A wrong or used code admits nothing; in the bound browser it is its entry, without the code.
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Refuse, 403, BrowserEntry.CodeRequiredBody), Decide(BrowserRequest("/?code=guess", cookie: null)));
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Redirect, Location: "/"), Decide(BrowserRequest("/?code=used")));
        Equal(403, Decide(BrowserRequest("/?code=the-code", host: "attacker.example:9224", cookie: null)).Status);
        Equal(405, Decide(BrowserRequest("/?code=the-code", method: "POST", cookie: null)).Status);
        Equal(404, Decide(BrowserRequest("/json?code=the-code", cookie: null)).Status);
        Equal(404, Decide(BrowserRequest("/?code=the-code", cookie: null, upgrade: true)).Status);

        // Without the bound browser's cookie, nothing: another browser, a page of another site, a tool on the machine.
        foreach (string? cookie in new[] { null, "", BrowserEntry.CookieName + "=other", "other=bound", BrowserEntry.CookieName + "=bound-not" })
            Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Refuse, 403, BrowserEntry.CodeRequiredBody), Decide(BrowserRequest(cookie: cookie)));
        Equal(403, Decide(BrowserRequest(), state with { BrowserCookie = null }).Status);
        Equal(BrowserEntry.Outcome.Redirect, Decide(BrowserRequest(cookie: "theme=dark; " + BrowserEntry.CookieName + "=bound")).Outcome);

        // The bound browser: the entry sends it to the Window's page. A CS-WebUI page is at the root, which the proxy
        // serves for the first such Window; a second one opens at its own address.
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Redirect, Location: "/?credential=secret"), Decide(BrowserRequest()));
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Forward, Authority: "127.0.0.1:41234", Path: "/?credential=secret"),
            Decide(BrowserRequest("/?credential=secret")));
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Forward, Authority: "127.0.0.1:41234", Path: "/app.js"),
            Decide(BrowserRequest("/app.js", site: "same-origin")));
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Forward, Authority: "127.0.0.1:41234", Path: "/"),
            Decide(BrowserRequest(), state with { Windows = [new("http://127.0.0.1:41234/")] }));
        Equal(BrowserEntry.Outcome.Forward, Decide(BrowserRequest("/?credential=secret", host: "localhost:9224", site: "same-origin")).Outcome);
        Equal(BrowserEntry.Outcome.Forward, Decide(BrowserRequest("/?credential=secret", site: null, origin: "http://127.0.0.1:9224")).Outcome);
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Redirect, Location: "http://127.0.0.1:41235/?credential=other"),
            Decide(BrowserRequest("/windows/2")));
        var desktop = state with { Windows = [new(DesktopSurfaceUrl)] };
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Redirect, Location: "/surface-a/index.html"), Decide(BrowserRequest(), desktop));
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Forward, Authority: "127.0.0.1:41234", Path: "/surface-a/app.js?v=1"),
            Decide(BrowserRequest("/surface-a/app.js?v=1", site: "same-origin"), desktop));
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Forward, Authority: "127.0.0.1:41234", Path: "/surface-a/_webui_ws_connect"),
            Decide(BrowserRequest("/surface-a/_webui_ws_connect", origin: "http://127.0.0.1:9224", site: "same-origin", upgrade: true), desktop));
        Equal(404, Decide(BrowserRequest("/surface-z/app.js"), desktop).Status);
        // Before the Window serves: a page that reloads itself.
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Wait), Decide(BrowserRequest(), state with { Windows = [] }));
        Equal(BrowserEntry.Outcome.Wait, Decide(BrowserRequest("/windows/3")).Outcome);
        Contains(BrowserEntry.WaitingPage, "http-equiv=\"refresh\"");

        // A Window process restarted: a page of the old one reloads onto the new one. Its Bridge's reconnection is turned
        // away (the page then reloads), also before the new Window serves; the reload goes to the entry.
        var restarted = state with
        {
            Windows = [new(RestartedSurfaceUrl)],
            StaleBases = new Dictionary<string, int> { ["/surface-a/"] = 0, ["/surface-c/"] = 1 },
        };
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.TurnAway),
            Decide(BrowserRequest("/surface-a/_webui_ws_connect", origin: "http://127.0.0.1:9224", site: "same-origin", upgrade: true), restarted));
        Equal(BrowserEntry.Outcome.TurnAway, Decide(BrowserRequest("/surface-a/_webui_ws_connect", upgrade: true), restarted with { Windows = [] }).Outcome);
        Equal(404, Decide(BrowserRequest("/surface-a/other", upgrade: true), restarted).Status);
        // Only the bound browser's: another client's gets nothing.
        Equal(403, Decide(BrowserRequest("/surface-a/_webui_ws_connect", cookie: null, upgrade: true), restarted).Status);
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Redirect, Location: "/"), Decide(BrowserRequest("/surface-a/index.html"), restarted));
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Redirect, Location: "/windows/2"), Decide(BrowserRequest("/surface-c/index.html"), restarted));
        Equal(404, Decide(BrowserRequest("/surface-a/save", method: "POST"), restarted).Status);
        // A Window that closed its page (the Bridge's close command): its address waits, and its paths are an earlier
        // Window's, so the page's reload goes to the entry.
        // A pending admission: its cookie confirms on a navigation to an entry, and opens nothing else before that.
        var pending = state with { Windows = [new(DesktopSurfaceUrl)], PendingCookie = "pending" };
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Redirect, Location: "/surface-a/index.html", Confirms: true),
            Decide(BrowserRequest(cookie: BrowserEntry.CookieName + "=pending", site: "same-origin"), pending));
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Redirect, Location: "/surface-a/index.html"), Decide(BrowserRequest(), pending));
        Equal(403, Decide(BrowserRequest("/surface-a/app.js", cookie: BrowserEntry.CookieName + "=pending"), pending).Status);
        Equal(403, Decide(BrowserRequest("/surface-a/_webui_ws_connect", cookie: BrowserEntry.CookieName + "=pending", upgrade: true), pending).Status);
        Equal(403, Decide(BrowserRequest(cookie: BrowserEntry.CookieName + "=pending", site: "cross-site"), pending).Status);
        Equal(403, Decide(BrowserRequest(cookie: BrowserEntry.CookieName + "=other"), pending).Status);

        // The application closed a Window and its process keeps running: / opens the first Window that serves, and a closed
        // /windows/<n> says so; within the grace after the close, both wait, as for a restart.
        Uri second = new(RestartedSurfaceUrl);
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Redirect, Location: "/surface-b/index.html"),
            Decide(BrowserRequest(), state with { Windows = [null, second] }));
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Wait), Decide(BrowserRequest(), state with { Windows = [null, second], Closing = new HashSet<int> { 0 } }));
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Closed), Decide(BrowserRequest("/windows/2"), state with { Windows = [second, null] }));
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Wait),
            Decide(BrowserRequest("/windows/2"), state with { Windows = [second, null], Closing = new HashSet<int> { 1 } }));
        Contains(BrowserEntry.ClosedPage, "The application closed this Window");
        Contains(BrowserEntry.ClosedPage, "http-equiv=\"refresh\"");

        var closed = state with { Windows = [null], StaleBases = new Dictionary<string, int> { ["/surface-a/"] = 0 } };
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Wait), Decide(BrowserRequest(), closed));
        Equal(new BrowserEntry.Decision(BrowserEntry.Outcome.Redirect, Location: "/"), Decide(BrowserRequest("/surface-a/index.html"), closed));
        Equal(BrowserEntry.Outcome.TurnAway, Decide(BrowserRequest("/surface-a/_webui_ws_connect", upgrade: true), closed).Outcome);

        // A web page cannot use the bound browser's admission, nor reach the address by DNS rebinding.
        foreach (string site in new[] { "cross-site", "same-site" }) Equal(403, Decide(BrowserRequest(site: site)).Status);
        Equal(403, Decide(BrowserRequest(origin: "http://example.com")).Status);
        Equal(403, Decide(BrowserRequest(origin: "null")).Status);
        Equal(403, Decide(BrowserRequest(origin: "http://127.0.0.1:41234")).Status);
        Equal(403, Decide(BrowserRequest(host: "attacker.example:9224")).Status);
        Equal(403, Decide(BrowserRequest(host: null)).Status);
        Equal(405, Decide(BrowserRequest("/windows/2", method: "POST")).Status);
        foreach (string path in new[] { "/windows/0", "/windows/x", "/windows/1/more" })
            Equal(404, Decide(BrowserRequest(path), desktop).Status);
    }

    [Test, DisplayName("the browser entry forwards the bound browser's request with the Window's Host and moves its redirects")]
    public void BrowserEntryForwardsHeaders()
    {
        HttpRequestHead head = HttpRequestHead.Parse(
            "GET /surface-a/app.js HTTP/1.1\r\nHost: 127.0.0.1:9224\r\nConnection: keep-alive\r\nOrigin: http://127.0.0.1:9224\r\n" +
            $"Cookie: theme=dark; {BrowserEntry.CookieName}=bound; webui_auth=1\r\nAccept: */*\r\nAccept: text/html\r\nKeep-Alive: 5\r\n\r\n")!;
        string forwarded = BrowserEntry.ForwardedHead(head, upgrade: false, "127.0.0.1:41234", "/surface-a/app.js");
        Equal("GET /surface-a/app.js HTTP/1.1\r\nHost: 127.0.0.1:41234\r\nOrigin: http://127.0.0.1:9224\r\n" +
            "Cookie: theme=dark; webui_auth=1\r\nAccept: */*\r\nAccept: text/html\r\nConnection: close\r\n\r\n", forwarded);
        HttpRequestHead only = HttpRequestHead.Parse(
            $"GET /surface-a/_webui_ws_connect HTTP/1.1\r\nHost: localhost:9224\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nCookie: {BrowserEntry.CookieName}=bound\r\n" +
            "Sec-WebSocket-Key: a2V5\r\nSec-WebSocket-Version: 13\r\n\r\n")!;
        Equal("GET /surface-b/_webui_ws_connect HTTP/1.1\r\nHost: 127.0.0.1:41236\r\nSec-WebSocket-Key: a2V5\r\nSec-WebSocket-Version: 13\r\n" +
            "Connection: Upgrade\r\nUpgrade: websocket\r\n\r\n",
            BrowserEntry.ForwardedHead(only, upgrade: true, "127.0.0.1:41236", "/surface-b/_webui_ws_connect"));

        // A Location on the Window's own address, by IP or by localhost, comes back on the entry's; others stay.
        string Location(string location) => BrowserEntry.ForwardedResponseHead(
            $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nConnection: keep-alive\r\n\r\n", upgrade: false, "127.0.0.1:41234", "127.0.0.1:9224");
        Equal("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:9224/surface-a/next?x=1#top\r\nConnection: close\r\n\r\n",
            Location("http://127.0.0.1:41234/surface-a/next?x=1#top"));
        Contains(Location("http://localhost:41234/next"), "Location: http://127.0.0.1:9224/next\r\n");
        Contains(Location("/surface-a/next"), "Location: /surface-a/next\r\n");
        Contains(Location("https://example.com/"), "Location: https://example.com/\r\n");
        Contains(Location("http://127.0.0.1:41235/other"), "Location: http://127.0.0.1:41235/other\r\n");
        Equal("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n", BrowserEntry.ForwardedResponseHead(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n", upgrade: true, "127.0.0.1:41234", "127.0.0.1:9224"));

        // The turn-away selects the page's offered subprotocol, without which a browser fails the handshake.
        Equal("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\nSec-WebSocket-Protocol: runic-desktop.old\r\n\r\n",
            BrowserEntry.TurnAwayHead("dGhlIHNhbXBsZSBub25jZQ==", "runic-desktop.old, other"));
        DoesNotContain(BrowserEntry.TurnAwayHead("dGhlIHNhbXBsZSBub25jZQ==", null), "Sec-WebSocket-Protocol");

        // The upgrade offers no compression to the Window, so the proxy can read the Bridge's frames.
        HttpRequestHead compressed = HttpRequestHead.Parse(
            "GET /surface-a/_webui_ws_connect HTTP/1.1\r\nHost: 127.0.0.1:9224\r\nSec-WebSocket-Extensions: permessage-deflate\r\n\r\n")!;
        DoesNotContain(BrowserEntry.ForwardedHead(compressed, upgrade: true, "127.0.0.1:41234", "/surface-a/_webui_ws_connect"), "Extensions");

        Equal(null, BrowserEntry.WithoutOwnCookie($"{BrowserEntry.CookieName}=bound"));
        Equal("a=1; b=2", BrowserEntry.WithoutOwnCookie($"a=1;{BrowserEntry.CookieName}=bound;  b=2"));
        Equal("x", BrowserEntry.CookieValue($"a=1; {BrowserEntry.CookieName}=x", BrowserEntry.CookieName));
        Equal(null, BrowserEntry.CookieValue($"a{BrowserEntry.CookieName}=x", BrowserEntry.CookieName));
    }

    [Test, DisplayName("the browser entry relays the Window's Bridge frames and turns its close command into a reload")]
    public async Task BrowserEntryRelaysTheBridge()
    {
        byte[] close = [0x82, 9, 221, 1, 2, 3, 4, 0, 0, 250, 0];
        byte[] push = [0x82, 9, 221, 1, 2, 3, 4, 0, 0, 248, 0];
        byte[] large = [0x82, 126, 0x01, 0x00, .. Enumerable.Repeat((byte)7, 256)];
        byte[] text = [0x81, 4, (byte)'p', (byte)'i', (byte)'n', (byte)'g'];
        byte[] closing = [0x88, 2, 0x03, 0xE8];
        using var from = new MemoryStream([.. push, .. large, .. text, .. close, .. closing]);
        using var to = new MemoryStream();
        await BrowserEntry.RelayBridgeAsync(from, to, CancellationToken.None);
        SequenceEqual([.. push, .. large, .. text, .. BrowserEntry.TurnAwayFrame, .. closing], to.ToArray());
        // The refusal is the Bridge's token check reply with a refused token: the page's client reloads on it.
        SequenceEqual(new byte[] { 0x82, 9, 221, 0, 0, 0, 0, 0, 0, 245, 0 }, BrowserEntry.TurnAwayFrame);
    }

    [Test, DisplayName("the browser entry follows the serve-only lines of the Window process")]
    public async Task BrowserEntryFollowsTheWindow()
    {
        await using BrowserEntry entry = BrowserEntry.TryStart(0)!;
        string root = entry.Url;
        Equal($"http://127.0.0.1:{entry.Port}/", root);
        Equal(null, entry.Observe("[host] something else"));
        // Only the Window's own loopback page is a target.
        Equal(null, entry.Observe("RUNIC_APPLICATION_URL=http://example.com/"));
        Equal(new BrowserEntry.ServedWindow(0, root, null), entry.Observe("RUNIC_APPLICATION_URL=" + SurfaceUrl));
        Equal(new BrowserEntry.ServedWindow(0, root, "App.MainWindow"), entry.Observe($"RUNIC_APPLICATION_WINDOW={SurfaceUrl} App.MainWindow future"));
        Equal(new BrowserEntry.ServedWindow(1, root + "windows/2", null), entry.Observe("RUNIC_APPLICATION_URL=http://127.0.0.1:41235/"));
        Equal(null, entry.Observe("RUNIC_APPLICATION_WINDOW=http://127.0.0.1:1/ Unknown"));
        SequenceEqual([new BrowserEntry.ServedWindow(0, root, "App.MainWindow"), new BrowserEntry.ServedWindow(1, root + "windows/2", null)],
            entry.Windows);
        True(BrowserEntry.IsServeOnlyLine("RUNIC_APPLICATION_URL=" + SurfaceUrl), "the URL line carries the credential");
        False(BrowserEntry.IsServeOnlyLine("[dev] Ready"), "other lines are printed");
        Equal("/surface-a/", BrowserEntry.BasePath(new Uri(DesktopSurfaceUrl)));
        Equal("/", BrowserEntry.BasePath(new Uri(SurfaceUrl)));

        // A taken port is reported, so dev can fall back to a free one.
        Equal(null, BrowserEntry.TryStart(entry.Port));
    }

    [Test, DisplayName("the browser entry proxies the bound browser to the Window, across a restart, and writes a private code file")]
    public async Task BrowserEntryServesTheWindow()
    {
        using var workspace = new TestWorkspace();
        string codeFile = Path.Combine(workspace.Root, "obj", "runic", BrowserEntry.CodeFileName);
        await using var first = FakeWindow.Start();
        await using var second = FakeWindow.Start();
        var codes = new List<string>();
        BrowserEntry entry = BrowserEntry.TryStart(0, codeFile: codeFile)!;
        try
        {
            entry.CodeChanged += () => { lock (codes) codes.Add(entry.Code); };
            string root = entry.Url;
            string host = $"127.0.0.1:{entry.Port}";
            // The code file opens the address with the current code; only this user can read it.
            string firstCode = entry.Code;
            Contains(File.ReadAllText(codeFile), BrowserEntry.CodeUrl(entry.Port, 0, firstCode));
            RequirePrivate(codeFile);
            entry.Observe($"RUNIC_APPLICATION_URL=http://127.0.0.1:{first.Port}/surface-a/index.html");

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
            async Task<HttpResponseMessage> Send(string target, string? cookie = null, string? site = "same-origin", string? origin = null,
                string? hostHeader = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(root), target));
                if (cookie is not null) request.Headers.Add("Cookie", cookie);
                if (site is not null) request.Headers.Add("Sec-Fetch-Site", site);
                if (origin is not null) request.Headers.Add("Origin", origin);
                if (hostHeader is not null) request.Headers.Host = hostHeader;
                return await client.SendAsync(request);
            }

            // Nothing without the code.
            using (HttpResponseMessage refused = await Send("/", site: "none"))
            {
                Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Contains(await refused.Content.ReadAsStringAsync(), BrowserEntry.CodeFileName);
            }
            // The code file's page (file:, so cross-site) admits the browser: dev's cookie, a new code, a rewritten file.
            string cookie;
            using (HttpResponseMessage admitted = await Send("/?code=" + firstCode, site: "cross-site", origin: "null"))
            {
                Equal(HttpStatusCode.OK, admitted.StatusCode);
                string setCookie = admitted.Headers.GetValues("Set-Cookie").Single();
                StartsWith(setCookie, BrowserEntry.CookieName + "=");
                Contains(setCookie, "; Path=/; HttpOnly; SameSite=Strict");
                cookie = setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)];
                True(admitted.Headers.CacheControl?.NoStore == true, "the admission is not cached");
                // A same-origin navigation, which carries the SameSite=Strict cookie, where a redirect from file: would not.
                Contains(await admitted.Content.ReadAsStringAsync(), "http-equiv=\"refresh\" content=\"0;url=/\"");
            }
            string secondCode = entry.Code;
            True(secondCode != firstCode, "an admission replaces the code");
            lock (codes) SequenceEqual([secondCode], codes);
            Contains(File.ReadAllText(codeFile), BrowserEntry.CodeUrl(entry.Port, 0, secondCode));
            DoesNotContain(File.ReadAllText(codeFile), firstCode);
            RequirePrivate(codeFile);
            using (HttpResponseMessage used = await Send("/?code=" + firstCode, site: "none"))
                Equal(HttpStatusCode.Forbidden, used.StatusCode);

            // The bound browser: the entry sends it to the page, which the proxy serves from the Window.
            using (HttpResponseMessage entryRedirect = await Send("/", cookie, site: "none"))
            {
                Equal(HttpStatusCode.Found, entryRedirect.StatusCode);
                Equal("/surface-a/index.html", entryRedirect.Headers.Location?.OriginalString);
            }
            using (HttpResponseMessage page = await Send("/surface-a/index.html", "theme=dark; " + cookie, origin: "http://" + host))
            {
                Equal(HttpStatusCode.OK, page.StatusCode);
                Equal("page /surface-a/index.html", await page.Content.ReadAsStringAsync());
            }
            string seen = first.Requests[^1];
            StartsWith(seen, "GET /surface-a/index.html HTTP/1.1\r\n");
            Contains(seen, $"\r\nHost: 127.0.0.1:{first.Port}\r\n");
            Contains(seen, $"\r\nOrigin: http://{host}\r\n");
            Contains(seen, "\r\nCookie: theme=dark\r\n");
            DoesNotContain(seen, BrowserEntry.CookieName);
            using (HttpResponseMessage moved = await Send("/surface-a/redirect", cookie))
            {
                Equal(HttpStatusCode.Found, moved.StatusCode);
                Equal($"http://{host}/surface-a/next", moved.Headers.Location?.OriginalString);
            }
            // Not another browser, a foreign page or a rebound name.
            using (HttpResponseMessage other = await Send("/surface-a/index.html", BrowserEntry.CookieName + "=guess"))
                Equal(HttpStatusCode.Forbidden, other.StatusCode);
            using (HttpResponseMessage foreign = await Send("/surface-a/index.html", cookie, site: "cross-site"))
                Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            using (HttpResponseMessage foreign = await Send("/surface-a/index.html", cookie, origin: "http://example.com"))
                Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            using (HttpResponseMessage rebound = await Send("/surface-a/index.html", cookie, hostHeader: "attacker.example:" + entry.Port))
                Equal(HttpStatusCode.Forbidden, rebound.StatusCode);
            int forwarded = first.Requests.Count;

            // The page's Bridge socket goes through the proxy.
            Equal("pong:/surface-a/_webui_ws_connect", await WebSocketThrough(entry.Port, "/surface-a/_webui_ws_connect", cookie, host));
            // dotnet watch stops the Window, which sends its page the close command: the page gets the token refusal
            // instead and reloads, and its reload waits at the entry rather than reaching the closing Window.
            first.CloseBridge = true;
            SequenceEqual(BrowserEntry.TurnAwayFrame, await TurnedAway(entry.Port, "/surface-a/_webui_ws_connect", cookie, host, fromEntry: false));
            using (HttpResponseMessage reload = await Send("/surface-a/index.html", cookie))
                Equal("/", reload.Headers.Location?.OriginalString);
            using (HttpResponseMessage closing = await Send("/", cookie, site: "none"))
                Contains(await closing.Content.ReadAsStringAsync(), "starting the Window");
            Equal(new BrowserEntry.ServedWindow(0, root, null), entry.Windows.Single());

            // The Window restarts: the old page's Bridge reconnects to the new Window, and its reload goes to the entry.
            entry.Clear();
            using (HttpResponseMessage waiting = await Send("/", cookie, site: "none"))
                Contains(await waiting.Content.ReadAsStringAsync(), "starting the Window");
            entry.Observe($"RUNIC_APPLICATION_URL=http://127.0.0.1:{second.Port}/surface-b/index.html");
            SequenceEqual(BrowserEntry.TurnAwayFrame, await TurnedAway(entry.Port, "/surface-a/_webui_ws_connect", cookie, host));
            Equal("pong:/surface-b/_webui_ws_connect", await WebSocketThrough(entry.Port, "/surface-b/_webui_ws_connect", cookie, host));
            using (HttpResponseMessage reload = await Send("/surface-a/index.html", cookie))
                Equal("/", reload.Headers.Location?.OriginalString);
            using (HttpResponseMessage page = await Send("/surface-b/index.html", cookie))
                Equal("page /surface-b/index.html", await page.Content.ReadAsStringAsync());
            Equal(forwarded + 2, first.Requests.Count);

            // One bound browser. An admission request alone (a file: page that frames or opens the code file) ends nothing:
            // the admitted browser keeps its cookie until the new browser's navigation presents its own.
            string next;
            using (HttpResponseMessage again = await Send("/?code=" + secondCode, site: "cross-site"))
            {
                Equal(HttpStatusCode.OK, again.StatusCode);
                string setCookie = again.Headers.GetValues("Set-Cookie").Single();
                next = setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)];
            }
            using (HttpResponseMessage still = await Send("/surface-b/index.html", cookie))
                Equal(HttpStatusCode.OK, still.StatusCode);
            // The pending cookie opens nothing but its entry, which makes it the admitted one.
            using (HttpResponseMessage early = await Send("/surface-b/index.html", next))
                Equal(HttpStatusCode.Forbidden, early.StatusCode);
            // The admitted browser's open Bridge socket.
            using var live = new TcpClient();
            await live.ConnectAsync(IPAddress.Loopback, entry.Port);
            NetworkStream liveStream = live.GetStream();
            await liveStream.WriteAsync(Encoding.ASCII.GetBytes($"GET /surface-b/_webui_ws_connect HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\n" +
                $"Connection: Upgrade\r\nOrigin: http://{host}\r\nCookie: {cookie}\r\nSec-WebSocket-Key: a2V5a2V5a2V5a2V5a2V5aw==\r\nSec-WebSocket-Version: 13\r\n\r\n"));
            StartsWith(await ReadUntil(liveStream, "\r\n\r\n"), "HTTP/1.1 101 ");
            await liveStream.WriteAsync(Encoding.ASCII.GetBytes("ping\n"));
            Equal("pong:/surface-b/_webui_ws_connect\n", await ReadUntil(liveStream, "\n"));
            Equal(1, entry.RelayedUpgrades);
            using (HttpResponseMessage confirmed = await Send("/", next, site: "same-origin"))
                Equal(HttpStatusCode.Found, confirmed.StatusCode);
            // The previous browser's cookie has ended, and so has its socket.
            using (HttpResponseMessage ended = await Send("/surface-b/index.html", cookie))
                Equal(HttpStatusCode.Forbidden, ended.StatusCode);
            using (var closedRead = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                Equal(0, await liveStream.ReadAsync(new byte[16], closedRead.Token));
            Equal(0, entry.RelayedUpgrades);
            using (HttpResponseMessage admitted = await Send("/surface-b/index.html", next))
                Equal(HttpStatusCode.OK, admitted.StatusCode);
        }
        finally
        {
            await entry.DisposeAsync();
        }
        // The code admits no browser once dev has stopped, and its page goes.
        False(File.Exists(codeFile), "dev deletes the code file when it stops");
    }

    // Only the current user can read the code file: mode 0600, or on Windows an access list of that user alone with
    // nothing inherited.
    private static void RequirePrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            return;
        }
        System.Security.AccessControl.FileSecurity security = new FileInfo(path).GetAccessControl();
        True(security.AreAccessRulesProtected, "the code file inherits no access rules");
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToArray();
        Equal(1, rules.Length);
        Equal(System.Security.Principal.WindowsIdentity.GetCurrent().User, rules[0].IdentityReference);
        Equal(System.Security.AccessControl.AccessControlType.Allow, rules[0].AccessControlType);
        False(rules[0].IsInherited, "the user's rule is the file's own");
    }

    // A WebSocket upgrade through the entry; the fake Window answers the first frame-free message with pong:<path>.
    private static async Task<string> WebSocketThrough(int port, string path, string cookie, string host)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();
        string request = $"GET {path} HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nOrigin: http://{host}\r\n" +
            $"Sec-Fetch-Site: same-origin\r\nCookie: {cookie}\r\nSec-WebSocket-Key: a2V5a2V5a2V5a2V5a2V5aw==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        string head = await ReadUntil(stream, "\r\n\r\n");
        StartsWith(head, "HTTP/1.1 101 ");
        await stream.WriteAsync(Encoding.ASCII.GetBytes("ping\n"));
        return (await ReadUntil(stream, "\n")).TrimEnd('\n');
    }

    // A Bridge socket of an earlier Window through the entry: the handshake, then the frame the entry answers with.
    // fromEntry: false when the Window answers the handshake and the entry relays its frames.
    private static async Task<byte[]> TurnedAway(int port, string path, string cookie, string host, bool fromEntry = true)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();
        const string key = "dGhlIHNhbXBsZSBub25jZQ==";
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Origin: http://{host}\r\nCookie: {cookie}\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n" +
            "Sec-WebSocket-Protocol: runic-desktop.old\r\n\r\n"));
        string head = await ReadUntil(stream, "\r\n\r\n");
        StartsWith(head, "HTTP/1.1 101 ");
        if (fromEntry)
        {
            Contains(head, "\r\nSec-WebSocket-Protocol: runic-desktop.old\r\n");
            // RFC 6455's example key and its accept value.
            Contains(head, "\r\nSec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n");
        }
        byte[] frame = new byte[BrowserEntry.TurnAwayFrame.Length];
        await stream.ReadExactlyAsync(frame);
        return frame;
    }

    private static async Task<string> ReadUntil(Stream stream, string end)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var text = new StringBuilder();
        var one = new byte[1];
        while (!text.ToString().EndsWith(end, StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, timeout.Token) == 0) break;
            text.Append((char)one[0]);
        }
        return text.ToString();
    }

    /// <summary>A Window's loopback server for the proxy: pages, a redirect to its own address, and a WebSocket echo.</summary>
    private sealed class FakeWindow : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _accepting;
        private readonly List<string> _requests = [];

        private FakeWindow(TcpListener listener)
        {
            _listener = listener;
            _accepting = Task.Run(AcceptAsync);
        }

        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        // The Window closes its pages: a Bridge socket gets the close command.
        internal bool CloseBridge { get; set; }

        internal IReadOnlyList<string> Requests
        {
            get { lock (_requests) return [.. _requests]; }
        }

        internal static FakeWindow Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new FakeWindow(listener);
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    string head = await ReadUntil(stream, "\r\n\r\n");
                    lock (_requests) _requests.Add(head);
                    string path = head.Split(' ')[1];
                    if (head.Contains("\r\nUpgrade: websocket\r\n", StringComparison.Ordinal))
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: x\r\n\r\n"));
                        if (CloseBridge)
                        {
                            await stream.WriteAsync(new byte[] { 0x82, 9, 221, 9, 9, 9, 9, 0, 0, 250, 0 });
                            return;
                        }
                        await ReadUntil(stream, "\n");
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"pong:{path}\n"));
                        // Open until the page closes it, as a Bridge socket is (ReadUntil gives up after 10 s).
                        await ReadUntil(stream, "\n");
                        return;
                    }
                    string answer = path.EndsWith("/redirect", StringComparison.Ordinal)
                        ? $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{Port}{path[..path.LastIndexOf('/')]}/next\r\nContent-Length: 0\r\n\r\n"
                        : $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {("page " + path).Length}\r\nConnection: keep-alive\r\n\r\npage {path}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(answer));
                }
                catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _accepting;
            _listener.Dispose();
        }
    }

    [Test, DisplayName("--browser: Ready follows the Window's first page, and the session file lists its entry addresses and the code file")]
    public void BrowserContractLinesFollowTheSessionFile()
    {
        using var workspace = new TestWorkspace();
        var options = new DevOptions(null, "Debug", true, true, true, false, []) { Browser = true };
        var session = new DevSession(CreateConfiguration(workspace, "vite"), options, [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        session.Start();
        using (JsonDocument starting = JsonDocument.Parse(File.ReadAllBytes(session.Path)))
        {
            JsonElement browser = starting.RootElement.GetProperty("browser");
            Equal(JsonValueKind.Null, browser.GetProperty("url").ValueKind);
            Equal(JsonValueKind.Null, browser.GetProperty("codeFile").ValueKind);
            Equal(0, browser.GetProperty("windows").GetArrayLength());
            Equal("dev", starting.RootElement.GetProperty("window").GetProperty("owner").GetString());
        }
        string codeFile = Path.Combine(workspace.Root, "obj", "runic", BrowserEntry.CodeFileName);
        session.BrowserStarted("http://127.0.0.1:9224/", codeFile);

        var entry = BrowserEntry.TryStart(0, codeFile: codeFile)!;
        try
        {
            var seen = new List<string>();
            void Read(string line)
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(session.Path));
                JsonElement root = document.RootElement;
                string windows = string.Join(",", root.GetProperty("browser").GetProperty("windows").EnumerateArray()
                    .Select(window => $"{window.GetProperty("url").GetString()![entry.Url.Length..]}={window.GetProperty("type").GetString() ?? "?"}"));
                seen.Add($"{line.Replace(entry.Url, "<entry>/", StringComparison.Ordinal).Replace(entry.Code, "<code>", StringComparison.Ordinal)} | " +
                    $"{root.GetProperty("state").GetString()} | [{windows}]");
            }
            var contract = new DevApplication.ContractReporter(session, Read, write => write(), entry);

            contract.WindowStarted(4242);
            contract.ServeOnlyLine("RUNIC_APPLICATION_URL=" + SurfaceUrl);
            contract.ServeOnlyLine($"RUNIC_APPLICATION_WINDOW={SurfaceUrl} App.MainWindow");
            contract.ServeOnlyLine("RUNIC_APPLICATION_URL=http://127.0.0.1:41235/");
            contract.ServeOnlyLine("something else");
            contract.WindowExited(4242);
            contract.WindowStarted(4243);
            contract.ServeOnlyLine("RUNIC_APPLICATION_URL=http://127.0.0.1:41236/");
            SequenceEqual([
                "[dev] Window started: process 4242 | starting | []",
                "[dev] Browser: Window serves at <entry>/?code=<code> | starting | [=?]",
                "[dev] Ready | ready | [=?]",
                "[dev] Browser: Window serves at <entry>/windows/2?code=<code> | ready | [=App.MainWindow,windows/2=?]",
                "[dev] Window exited: process 4242 | starting | []",
                "[dev] Window started: process 4243 | starting | []",
                "[dev] Browser: Window serves at <entry>/?code=<code> | starting | [=?]",
                "[dev] Ready | ready | [=?]",
            ], seen);

            // The Windows' own addresses carry the surface's credential, and the code admits a browser: neither is in the
            // file, which names the code file instead.
            string text = File.ReadAllText(session.Path);
            DoesNotContain(text, "credential");
            DoesNotContain(text, entry.Code);
            using (JsonDocument ready = JsonDocument.Parse(File.ReadAllBytes(session.Path)))
            {
                Equal("http://127.0.0.1:9224/", ready.RootElement.GetProperty("browser").GetProperty("url").GetString());
                Equal(codeFile, ready.RootElement.GetProperty("browser").GetProperty("codeFile").GetString());
            }
            session.Stop();
            using JsonDocument stopped = JsonDocument.Parse(File.ReadAllBytes(session.Path));
            Equal(0, stopped.RootElement.GetProperty("browser").GetProperty("windows").GetArrayLength());
        }
        finally
        {
            entry.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        // Without --browser, browser is null.
        var native = new DevSession(CreateConfiguration(workspace, "vite"), options with { Browser = false }, [],
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        native.Start();
        using JsonDocument nativeDocument = JsonDocument.Parse(File.ReadAllBytes(native.Path));
        Equal(JsonValueKind.Null, nativeDocument.RootElement.GetProperty("browser").ValueKind);
    }

    [Test, DisplayName("the --browser hint names the address with its code, the code file and a moved port")]
    public void BrowserHints()
    {
        string hint = DevApplication.BrowserHint(BrowserEntry.DefaultPort, "abc");
        Contains(hint, "[dev] Browser: ");
        Contains(hint, "http://127.0.0.1:9224/?code=abc");
        Contains(hint, "obj/runic/dev-browser.html");
        Contains(hint, "a CS-WebUI page needs a reload");
        Contains(hint, "Chromium, not the Window's WebView");
        DoesNotContain(hint, "was taken");
        Contains(DevApplication.BrowserHint(40001, "abc"), "http://127.0.0.1:40001/?code=abc");
        Contains(DevApplication.BrowserHint(40001, "abc"), "Port 9224 was taken");
        Contains(DevApplication.BrowserAdmittedLine(9224, "next"), "The next one is http://127.0.0.1:9224/?code=next; the browser that opens it becomes the admitted one.");
        Equal("[dev] Browser: Window serves at http://127.0.0.1:9224/?code=abc",
            DevApplication.BrowserServedLine(new BrowserEntry.ServedWindow(0, "http://127.0.0.1:9224/", null), "abc"));
        string page = BrowserEntry.CodePage(9224, "a&b");
        Contains(page, "http-equiv=\"refresh\" content=\"0;url=http://127.0.0.1:9224/?code=a&amp;b\"");
        Contains(page, "name=\"referrer\" content=\"no-referrer\"");
    }

    [Test, DisplayName("the [dev] Browser contract line has one form for every Window, which the documented regex matches")]
    public void BrowserContractLineHasOneForm()
    {
        Equal(DocumentedBrowserLinePattern, DevApplication.BrowserServedPattern);
        foreach ((int index, string url) in new[] { (0, "http://127.0.0.1:9224/"), (1, "http://127.0.0.1:9224/windows/2"), (0, "http://127.0.0.1:40001/") })
        {
            Match match = Regex.Match(DevApplication.BrowserServedLine(new BrowserEntry.ServedWindow(index, url, null), "c0de_-x"), DocumentedBrowserLinePattern);
            True(match.Success, url);
            Equal(url + "?code=c0de_-x", match.Groups["url"].Value);
        }
        // The other [dev] Browser: lines are human text and do not match.
        False(Regex.IsMatch(DevApplication.BrowserHint(9224, "c"), DocumentedBrowserLinePattern), "the hint");
        False(Regex.IsMatch(DevApplication.BrowserAdmittedLine(9224, "c"), DocumentedBrowserLinePattern), "the admission");
        False(Regex.IsMatch(BrowserEntry.RefusedPeerMessage(9224, 1001), DocumentedBrowserLinePattern), "the refusal");
        Contains(File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "dotnet-runic", "README.md")),
            "`" + DocumentedBrowserLinePattern + "`");
    }

    [Test, DisplayName("the browser entry answers only the user who runs dev, from /proc/net/tcp")]
    public void BrowserEntryChecksThePeersUser()
    {
        // The synthetic tables of #1046's tests: the entry stands where the adapter does, on 127.0.0.1:9222.
        var sockets = new LoopbackSockets(path => path.EndsWith("tcp6", StringComparison.Ordinal) ? SyntheticTcp6 : SyntheticTcp, 1000);
        var entry = new IPEndPoint(IPAddress.Loopback, 9222);
        IPEndPoint Client(int port) => new(IPAddress.Loopback, port);
        Equal(new BrowserEntry.PeerCheck(true, false, 1000u), BrowserEntry.CheckPeer(sockets, entry, Client(0xA003)));
        Equal(new BrowserEntry.PeerCheck(false, true, 1001u), BrowserEntry.CheckPeer(sockets, entry, Client(0xA000)));
        Equal(new BrowserEntry.PeerCheck(false, true, 1003u), BrowserEntry.CheckPeer(sockets, entry, Client(0xA002)));
        // A peer gone already while the listener is listed: refused without a line. Unreadable tables: refused with one.
        Equal(new BrowserEntry.PeerCheck(false, false, null), BrowserEntry.CheckPeer(sockets, entry, Client(0xBEEF)));
        Equal(new BrowserEntry.PeerCheck(false, true, null), BrowserEntry.CheckPeer(new LoopbackSockets(static _ => null, 1000), entry, Client(0xA003)));
        // Off Linux there are no tables to read.
        Equal(new BrowserEntry.PeerCheck(true, false, null), BrowserEntry.CheckPeer(null, entry, Client(0xA000)));
        Contains(BrowserEntry.RefusedPeerMessage(9224, 1001), "from another user (uid 1001)");
        Contains(BrowserEntry.RefusedPeerMessage(9224, null), "could not be determined from /proc/net/tcp");
    }

    [Test, DisplayName("the browser entry refuses another local user's request with 403 and reports it once")]
    public async Task BrowserEntryRefusesOtherUsers()
    {
        if (!OperatingSystem.IsLinux()) return;
        var reported = new List<string>();
        await using BrowserEntry entry = BrowserEntry.TryStart(0,
            ForeignOwner(static row => row.State == 1 && row.Remote.Port != 0), line => { lock (reported) reported.Add(line); })!;
        entry.Observe("RUNIC_APPLICATION_URL=" + SurfaceUrl);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using HttpResponseMessage refused = await client.GetAsync(new Uri(BrowserEntry.CodeUrl(entry.Port, 0, entry.Code)));
            Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            string body = await refused.Content.ReadAsStringAsync();
            DoesNotContain(body, "credential");
            Equal(null, refused.Headers.Location);
            False(refused.Headers.Contains("Set-Cookie"), "another user's request is not admitted, even with the code");
        }
        lock (reported) Contains(reported.Single(), "from another user (uid 4242)");
    }

    [Test, DisplayName("the browser entry admits the developer's own requests through the real /proc/net/tcp lookup")]
    public async Task BrowserEntryAdmitsTheDevelopersUser()
    {
        if (!OperatingSystem.IsLinux()) return;
        var reported = new List<string>();
        await using BrowserEntry entry = BrowserEntry.TryStart(0, LoopbackSockets.Current, line => { lock (reported) reported.Add(line); })!;
        entry.Observe("RUNIC_APPLICATION_URL=" + SurfaceUrl);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using HttpResponseMessage admitted = await client.GetAsync(new Uri(BrowserEntry.CodeUrl(entry.Port, 0, entry.Code)));
        Equal(HttpStatusCode.OK, admitted.StatusCode);
        True(admitted.Headers.Contains("Set-Cookie"), "the developer's browser is admitted");
        lock (reported) Equal(0, reported.Count);
    }
}
