using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
            new Dictionary<string, string?>(), static _ => null, [new(BrowserEntry.ServeOnlyVariable, "0")], browser: true);
        Equal("1", served[BrowserEntry.ServeOnlyVariable]);
        IReadOnlyDictionary<string, string?> chosen = HostProcessController.CreateDevelopmentEnvironment(desktop,
            new Dictionary<string, string?>(), static _ => null, [new(BrowserEntry.ServeOnlyVariable, "1")]);
        Equal("1", chosen[BrowserEntry.ServeOnlyVariable]);
        False(HostProcessController.CreateDevelopmentEnvironment(desktop, new Dictionary<string, string?>(), static _ => null)
            .ContainsKey(BrowserEntry.ServeOnlyVariable), "dev sets serve-only only with --browser");
    }

    [Test, DisplayName("the browser entry redirects only the browser's own navigations to the Window's page")]
    public void BrowserEntryPolicy()
    {
        string[] targets = [SurfaceUrl, "http://127.0.0.1:41235/?credential=other"];
        BrowserEntry.Response Get(string path = "/", string? host = "127.0.0.1:9224", string? origin = null, string? site = "none",
            string method = "GET", string[]? windows = null) =>
            BrowserEntry.Respond(method, path, host, origin, site, windows ?? targets);

        Equal(new BrowserEntry.Response(302, SurfaceUrl, ""), Get());
        Equal(302, Get(host: "localhost:9224").Status);
        // A reload of the waiting page, and a browser or tool that sends no Sec-Fetch-Site.
        Equal(302, Get(site: "same-origin").Status);
        Equal(302, Get(site: null).Status);
        Equal(302, Get(method: "HEAD").Status);
        Equal(targets[1], Get("/windows/2").Location);
        Equal(SurfaceUrl, Get("/windows/1?reload").Location);
        // Before the Window serves: a page that reloads itself.
        Equal(new BrowserEntry.Response(200, null, BrowserEntry.WaitingPage), Get(windows: []));
        Equal(200, Get("/windows/3").Status);
        Contains(BrowserEntry.WaitingPage, "http-equiv=\"refresh\"");

        // A web page cannot send the browser here (it would take the surface's one client), nor reach it by DNS rebinding.
        foreach (string site in new[] { "cross-site", "same-site" }) Equal(403, Get(site: site).Status);
        Equal(403, Get(origin: "http://example.com").Status);
        Equal(403, Get(origin: "null").Status);
        Equal(403, Get(host: "attacker.example:9224").Status);
        Equal(403, Get(host: null).Status);
        Equal(405, Get(method: "POST").Status);
        foreach (string path in new[] { "/windows/0", "/windows/x", "/json", "/windows/1/more" }) Equal(404, Get(path).Status);
    }

    [Test, DisplayName("the browser entry follows the serve-only lines of the Window process")]
    public async Task BrowserEntryServesTheWindow()
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

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using (HttpResponseMessage redirect = await client.GetAsync(new Uri(root)))
        {
            Equal(HttpStatusCode.Found, redirect.StatusCode);
            Equal(new Uri(SurfaceUrl), redirect.Headers.Location);
            True(redirect.Headers.CacheControl?.NoStore == true, "the redirect is not cached");
        }
        using (var crossSite = new HttpRequestMessage(HttpMethod.Get, root))
        {
            crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
            using HttpResponseMessage refused = await client.SendAsync(crossSite);
            Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        using (var rebound = new HttpRequestMessage(HttpMethod.Get, root))
        {
            rebound.Headers.Host = "attacker.example";
            using HttpResponseMessage refused = await client.SendAsync(rebound);
            Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        // The Window process exited: the waiting page until the next one serves.
        entry.Clear();
        Equal(0, entry.Windows.Count);
        using HttpResponseMessage waiting = await client.GetAsync(new Uri(root));
        Equal(HttpStatusCode.OK, waiting.StatusCode);
        Contains(await waiting.Content.ReadAsStringAsync(), "starting the Window");

        // A taken port is reported, so dev can fall back to a free one.
        Equal(null, BrowserEntry.TryStart(entry.Port));
    }

    [Test, DisplayName("--browser: Ready follows the Window's first page, and the session file lists its entry addresses")]
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
            Equal(0, browser.GetProperty("windows").GetArrayLength());
            Equal("dev", starting.RootElement.GetProperty("window").GetProperty("owner").GetString());
        }
        session.BrowserStarted("http://127.0.0.1:9224/");

        var entry = BrowserEntry.TryStart(0)!;
        try
        {
            var seen = new List<string>();
            void Read(string line)
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(session.Path));
                JsonElement root = document.RootElement;
                string windows = string.Join(",", root.GetProperty("browser").GetProperty("windows").EnumerateArray()
                    .Select(window => $"{window.GetProperty("url").GetString()![entry.Url.Length..]}={window.GetProperty("type").GetString() ?? "?"}"));
                seen.Add($"{line.Replace(entry.Url, "<entry>/", StringComparison.Ordinal)} | {root.GetProperty("state").GetString()} | [{windows}]");
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
                "[dev] Browser: the Window serves at <entry>/ | starting | [=?]",
                "[dev] Ready | ready | [=?]",
                "[dev] Browser: another Window serves at <entry>/windows/2 | ready | [=App.MainWindow,windows/2=?]",
                "[dev] Window exited: process 4242 | starting | []",
                "[dev] Window started: process 4243 | starting | []",
                "[dev] Browser: the Window serves at <entry>/ | starting | [=?]",
                "[dev] Ready | ready | [=?]",
            ], seen);

            // The Windows' own addresses carry the surface's credential and stay out of the file.
            DoesNotContain(File.ReadAllText(session.Path), "credential");
            using (JsonDocument ready = JsonDocument.Parse(File.ReadAllBytes(session.Path)))
                Equal("http://127.0.0.1:9224/", ready.RootElement.GetProperty("browser").GetProperty("url").GetString());
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

    [Test, DisplayName("the --browser hint names the entry address and a moved port")]
    public void BrowserHints()
    {
        string hint = DevApplication.BrowserHint(BrowserEntry.DefaultPort);
        Contains(hint, "[dev] Browser: ");
        Contains(hint, "http://127.0.0.1:9224/");
        Contains(hint, "Chromium, not the Window's WebView");
        DoesNotContain(hint, "was taken");
        Contains(DevApplication.BrowserHint(40001), "Port 9224 was taken: set \"url\": \"http://127.0.0.1:40001/\"");
        Equal("[dev] Browser: the Window serves at http://127.0.0.1:9224/",
            DevApplication.BrowserServedLine(new BrowserEntry.ServedWindow(0, "http://127.0.0.1:9224/", null)));
    }
}
