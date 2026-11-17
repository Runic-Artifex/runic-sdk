using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Tool;
using Runic.Application.Tool.WebKit;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

/// <summary>D070.7: the CS-WebUI browser's Development CDP endpoint in dev's Window environment and session file.</summary>
internal sealed partial class ToolTests
{
    private static string? CsWebUiPort(IReadOnlyList<KeyValuePair<string, string>> environment) =>
        environment.Where(static variable => variable.Key == CsWebUiDebugging.PortVariable).Select(static variable => variable.Value).SingleOrDefault();

    [Test, DisplayName("dev opens a CS-WebUI Window's CDP port only when an IDE asks: --debug-endpoint or --no-window (D070.17)")]
    public void DevAsksCsWebUiForADebuggingPort()
    {
        using var workspace = new TestWorkspace();
        DevProjectConfiguration csWebUi = CreateConfiguration(workspace, "vite") with { ApplicationHosts = ["cswebui"] };
        var terminal = new DevOptions(null, "Debug", true, true, true, false, []);
        DevOptions ide = terminal with { CsWebUiDebugEndpoint = true };

        // A terminal dotnet runic dev opens nothing (DEV-SEC-50), and says how to.
        Equal(null, CsWebUiPort(CsWebUiDebugging.WindowEnvironment(csWebUi, terminal)));
        False(CsWebUiDebugging.Asked(terminal), "a terminal dev asks");
        True(CsWebUiDebugging.OffHint.Contains("--debug-endpoint", StringComparison.Ordinal), CsWebUiDebugging.OffHint);
        // An IDE passes --debug-endpoint: a free port.
        Equal("0", CsWebUiPort(CsWebUiDebugging.WindowEnvironment(csWebUi, ide)));
        Equal(0, CsWebUiDebugging.NoWindowEndpoints(csWebUi, ide).Count);
        // --no-window: the IDE starts the Window with the session file's environment, on the fixed port.
        Equal("9225", CsWebUiPort(CsWebUiDebugging.WindowEnvironment(csWebUi, terminal with { StartWindow = false })));
        Equal(new DevSession.DebugEndpoint("chromium", "cdp", "http://127.0.0.1:9225/"),
            CsWebUiDebugging.NoWindowEndpoints(csWebUi, terminal with { StartWindow = false }).Single());
        // Both hosts: the CS-WebUI Window may use an installed browser.
        Equal("0", CsWebUiPort(CsWebUiDebugging.WindowEnvironment(csWebUi with { ApplicationHosts = ["desktop", "cswebui"] }, ide)));
        True(CsWebUiDebugging.NoWindowHint(CsWebUiDebugging.Endpoint(9225)).StartsWith("[dev] Debugging: ", StringComparison.Ordinal), "the hint is a [dev] line");
        True(CsWebUiDebugging.OpenedHint(CsWebUiDebugging.Endpoint(41625)).Contains("Any local process can attach", StringComparison.Ordinal), "the line names the exposure");

        // Not for Runic Desktop, --no-debug-endpoint (which wins) or --browser (no browser of the Window's own).
        Equal(null, CsWebUiPort(CsWebUiDebugging.WindowEnvironment(csWebUi with { ApplicationHosts = ["desktop"] }, ide)));
        Equal(null, CsWebUiPort(CsWebUiDebugging.WindowEnvironment(csWebUi, ide with { DebugEndpoint = false })));
        Equal(null, CsWebUiPort(CsWebUiDebugging.WindowEnvironment(csWebUi, ide with { Browser = true })));
        Equal(0, CsWebUiDebugging.NoWindowEndpoints(csWebUi, terminal with { StartWindow = false, DebugEndpoint = false }).Count);

        // The developer's -e value is forwarded as it is, and --no-window lists a fixed one.
        DevOptions own = terminal with { StartWindow = false, Environment = [new(CsWebUiDebugging.PortVariable, "9333")] };
        Equal(null, CsWebUiPort(CsWebUiDebugging.WindowEnvironment(csWebUi, own)));
        Equal("http://127.0.0.1:9333/", CsWebUiDebugging.NoWindowEndpoints(csWebUi, own).Single().Url);
        Equal(0, CsWebUiDebugging.NoWindowEndpoints(csWebUi, own with { Environment = [new(CsWebUiDebugging.PortVariable, "0")] }).Count);
    }

    [Test, DisplayName("dev --help lists --debug-endpoint")]
    public async System.Threading.Tasks.Task DebugEndpointOption()
    {
        var console = new CapturingConsole();
        Equal(Runic.Application.Tool.Program.Success, await Runic.Application.Tool.Program.RunAsync(["dev", "--help"], console));
        True(console.Output.Contains("--debug-endpoint", StringComparison.Ordinal), "dev --help lacks --debug-endpoint");
    }

    private const string Port9225Tcp = """
          sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
           0: 0100007F:2409 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1001        0 101 1 0000000000000000 100 0 0 10 0
        """;

    private const string Port9225Tcp6 = """
          sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
           0: 00000000000000000000000001000000:2409 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000  1002        0 108 1 0000000000000000 100 0 0 10 0
        """;

    // DEV-SEC-51: a fixed port that something holds when dev starts is listed as unavailable, with a warning.
    [Test, DisplayName("dev --no-window warns about a CS-WebUI port 9225 that another user or process holds")]
    public void DevChecksTheNoWindowPort()
    {
        var none = new LoopbackSockets(static _ => null, 1000);
        Equal(null, CsWebUiDebugging.NoWindowPortCheck(9225, none, static (_, _) => true));
        Equal(new CsWebUiDebugging.PortHolder("port-taken", null), CsWebUiDebugging.NoWindowPortCheck(9225, null, static (_, _) => false));
        // [::1] counts: Chromium listens there when 127.0.0.1's port is taken, and a debugger may resolve localhost to it.
        Equal(new CsWebUiDebugging.PortHolder("port-taken", null),
            CsWebUiDebugging.NoWindowPortCheck(9225, null, static (address, _) => !address.Equals(System.Net.IPAddress.IPv6Loopback)));
        var ipv4 = new LoopbackSockets(path => path.EndsWith("tcp6", StringComparison.Ordinal) ? null : Port9225Tcp, 1000);
        Equal(new CsWebUiDebugging.PortHolder("foreign-listener", 1001u), CsWebUiDebugging.NoWindowPortCheck(9225, ipv4, static (_, _) => true));
        var ipv6 = new LoopbackSockets(path => path.EndsWith("tcp6", StringComparison.Ordinal) ? Port9225Tcp6 : null, 1000);
        Equal(new CsWebUiDebugging.PortHolder("foreign-listener", 1002u), CsWebUiDebugging.NoWindowPortCheck(9225, ipv6, static (_, _) => true));
        // The user's own listener, such as their Window: not foreign.
        var own = new LoopbackSockets(path => path.EndsWith("tcp6", StringComparison.Ordinal) ? null : Port9225Tcp, 1001);
        Equal(null, CsWebUiDebugging.NoWindowPortCheck(9225, own, static (_, _) => true));

        string warning = CsWebUiDebugging.PortTakenWarning(CsWebUiDebugging.Endpoint(9225), 1001);
        True(warning.StartsWith("[dev] Debugging: warning: http://127.0.0.1:9225", StringComparison.Ordinal) && warning.Contains("uid 1001", StringComparison.Ordinal), warning);
        True(CsWebUiDebugging.PortTakenWarning(CsWebUiDebugging.Endpoint(9225), null).Contains("another process", StringComparison.Ordinal), "the warning without an owner");
    }

    [Test, DisplayName("the session file moves the --no-window CS-WebUI endpoint to unavailableDebugEndpoints when another user takes its port")]
    public async System.Threading.Tasks.Task DevWatchesTheNoWindowPort()
    {
        using var workspace = new TestWorkspace();
        DevProjectConfiguration csWebUi = CreateConfiguration(workspace, "vite") with { ApplicationHosts = ["cswebui"] };
        DevSession.DebugEndpoint endpoint = CsWebUiDebugging.Endpoint(9225);
        var session = new DevSession(csWebUi, new DevOptions(null, "Debug", true, true, true, false, []) { StartWindow = false }, [endpoint],
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        session.Start();
        string? table = null;
        var sockets = new LoopbackSockets(path => path.EndsWith("tcp6", StringComparison.Ordinal) ? null : table, 1000);
        var time = new FakeTimeProvider();
        var lines = new List<string>();
        using var stop = new CancellationTokenSource();
        Task watch = CsWebUiDebugging.WatchNoWindowPortAsync(session, endpoint, 9225, sockets, lines.Add, write => write(), time, stop.Token);

        time.Advance(CsWebUiDebugging.NoWindowPortWatchInterval);
        Equal(1, session.ListedDebugEndpoints.Count);
        table = Port9225Tcp;
        for (int step = 0; step < 50 && !watch.IsCompleted; step++)
        {
            time.Advance(CsWebUiDebugging.NoWindowPortWatchInterval);
            await Task.Delay(10);
        }
        await watch.WaitAsync(TimeSpan.FromSeconds(10));
        Equal(0, session.ListedDebugEndpoints.Count);
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(session.Path)))
        {
            Equal(0, document.RootElement.GetProperty("debugEndpoints").GetArrayLength());
            JsonElement unavailable = document.RootElement.GetProperty("unavailableDebugEndpoints").EnumerateArray().Single();
            Equal("http://127.0.0.1:9225/", unavailable.GetProperty("url").GetString());
            Equal("foreign-listener", unavailable.GetProperty("reason").GetString());
        }
        True(lines.Single().Contains("uid 1001", StringComparison.Ordinal), lines.Single());
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
