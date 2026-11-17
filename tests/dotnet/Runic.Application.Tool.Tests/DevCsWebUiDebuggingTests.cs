using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Runic.Application.Tool;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

/// <summary>D070.7: the CS-WebUI browser's Development CDP endpoint in dev's Window environment and session file.</summary>
internal sealed partial class ToolTests
{
    private static string? CsWebUiPort(IReadOnlyList<KeyValuePair<string, string>> environment) =>
        environment.Where(static variable => variable.Key == CsWebUiDebugging.PortVariable).Select(static variable => variable.Value).SingleOrDefault();

    [Test, DisplayName("dev asks a CS-WebUI Window for a free CDP port, and --no-window for the fixed one")]
    public void DevAsksCsWebUiForADebuggingPort()
    {
        using var workspace = new TestWorkspace();
        DevProjectConfiguration csWebUi = CreateConfiguration(workspace, "vite") with { ApplicationHosts = ["cswebui"] };
        var options = new DevOptions(null, "Debug", true, true, true, false, []);

        Equal("0", CsWebUiPort(CsWebUiDebugging.WindowEnvironment(csWebUi, options)));
        Equal(0, CsWebUiDebugging.NoWindowEndpoints(csWebUi, options).Count);
        Equal("9225", CsWebUiPort(CsWebUiDebugging.WindowEnvironment(csWebUi, options with { StartWindow = false })));
        Equal(new DevSession.DebugEndpoint("chromium", "cdp", "http://127.0.0.1:9225/"),
            CsWebUiDebugging.NoWindowEndpoints(csWebUi, options with { StartWindow = false }).Single());
        // Both hosts: the CS-WebUI Window may use an installed browser.
        Equal("0", CsWebUiPort(CsWebUiDebugging.WindowEnvironment(csWebUi with { ApplicationHosts = ["desktop", "cswebui"] }, options)));
        True(CsWebUiDebugging.NoWindowHint(CsWebUiDebugging.Endpoint(9225)).StartsWith("[dev] Debugging: ", StringComparison.Ordinal), "the hint is a [dev] line");

        // Not for Runic Desktop, --no-debug-endpoint or --browser (no browser of the Window's own).
        Equal(null, CsWebUiPort(CsWebUiDebugging.WindowEnvironment(csWebUi with { ApplicationHosts = ["desktop"] }, options)));
        Equal(null, CsWebUiPort(CsWebUiDebugging.WindowEnvironment(csWebUi, options with { DebugEndpoint = false })));
        Equal(null, CsWebUiPort(CsWebUiDebugging.WindowEnvironment(csWebUi, options with { Browser = true })));
        Equal(0, CsWebUiDebugging.NoWindowEndpoints(csWebUi, options with { StartWindow = false, DebugEndpoint = false }).Count);

        // The developer's -e value is forwarded as it is, and --no-window lists a fixed one.
        DevOptions own = options with { StartWindow = false, Environment = [new(CsWebUiDebugging.PortVariable, "9333")] };
        Equal(null, CsWebUiPort(CsWebUiDebugging.WindowEnvironment(csWebUi, own)));
        Equal("http://127.0.0.1:9333/", CsWebUiDebugging.NoWindowEndpoints(csWebUi, own).Single().Url);
        Equal(0, CsWebUiDebugging.NoWindowEndpoints(csWebUi, own with { Environment = [new(CsWebUiDebugging.PortVariable, "0")] }).Count);
    }

    [Test, DisplayName("dev reads the Window's CDP endpoint lines and takes only loopback addresses")]
    public void DevReadsCsWebUiEndpointLines()
    {
        True(CsWebUiDebugging.TryRead("RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp http://127.0.0.1:41625/", out var opened, out var closed), "opened");
        Equal(new DevSession.DebugEndpoint("chromium", "cdp", "http://127.0.0.1:41625/"), opened);
        Equal(null, closed);
        // Later releases may add fields.
        True(CsWebUiDebugging.TryRead("RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp http://127.0.0.1:41625/ more", out opened, out _), "later fields");
        Equal("http://127.0.0.1:41625/", opened!.Url);
        True(CsWebUiDebugging.TryRead("RUNIC_APPLICATION_DEBUG_ENDPOINT_CLOSED=http://127.0.0.1:41625/", out opened, out closed), "closed");
        Equal(null, opened);
        Equal("http://127.0.0.1:41625/", closed);
        True(CsWebUiDebugging.IsProtocolLine("RUNIC_APPLICATION_DEBUG_ENDPOINT_CLOSED=x"), "closed line");
        False(CsWebUiDebugging.IsProtocolLine("RUNIC_APPLICATION_URL=http://127.0.0.1:1/"), "serve-only line");

        foreach (string line in new[]
        {
            "RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp http://0.0.0.0:41625/",
            "RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp http://example.test:41625/",
            "RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp http://127.0.0.1:70000/",
            "RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp",
            "RUNIC_APPLICATION_DEBUG_ENDPOINT=Chromium\"x cdp http://127.0.0.1:41625/",
            "RUNIC_APPLICATION_DEBUG_ENDPOINT_CLOSED=",
            "[dev] something else",
        })
        {
            False(CsWebUiDebugging.TryRead(line, out _, out _), line);
        }
    }

    [Test, DisplayName("the session file lists a CS-WebUI Window's CDP endpoint while its browser runs")]
    public void DevSessionListsTheCsWebUiEndpointWhileItRuns()
    {
        using var workspace = new TestWorkspace();
        DevProjectConfiguration csWebUi = CreateConfiguration(workspace, "vite") with { ApplicationHosts = ["cswebui"] };
        var options = new DevOptions(null, "Debug", true, true, true, false, []);
        var session = new DevSession(csWebUi, options, [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var lines = new List<string>();
        var contract = new DevApplication.ContractReporter(session, lines.Add, write => write());
        JsonElement[] Endpoints()
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(session.Path));
            return [.. document.RootElement.GetProperty("debugEndpoints").EnumerateArray().Select(static endpoint => endpoint.Clone())];
        }

        session.Start();
        contract.WindowStarted(4242);
        Equal(0, Endpoints().Length);

        contract.DebugEndpointLine("RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp http://127.0.0.1:41625/");
        JsonElement endpoint = Endpoints().Single();
        // The shape of WebView2's and WebKit's entries.
        SequenceEqual(["engine", "protocol", "url"], endpoint.EnumerateObject().Select(static property => property.Name).ToArray());
        Equal("chromium", endpoint.GetProperty("engine").GetString());
        Equal("cdp", endpoint.GetProperty("protocol").GetString());
        Equal("http://127.0.0.1:41625/", endpoint.GetProperty("url").GetString());
        True(lines[^1].StartsWith("[dev] Debugging: ", StringComparison.Ordinal) && lines[^1].Contains("http://127.0.0.1:41625/", StringComparison.Ordinal),
            lines[^1]);

        // A second window's browser; the first one's closes.
        contract.DebugEndpointLine("RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp http://127.0.0.1:41626/");
        Equal(2, Endpoints().Length);
        contract.DebugEndpointLine("RUNIC_APPLICATION_DEBUG_ENDPOINT_CLOSED=http://127.0.0.1:41625/");
        Equal("http://127.0.0.1:41626/", Endpoints().Single().GetProperty("url").GetString());

        // The Window process exits: its browsers ended with it.
        contract.WindowExited(4242);
        Equal(0, Endpoints().Length);

        contract.WindowStarted(4243);
        contract.DebugEndpointLine("RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp http://127.0.0.1:41700/");
        Equal(1, Endpoints().Length);
        session.Stop();
        Equal(0, Endpoints().Length);
    }
}
