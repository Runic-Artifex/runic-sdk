using System.Net.Http;
using System.Text.Json;
using Runic.Desktop.Internal;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// D070.7: the Development-only CDP endpoint of CS-WebUI's installed Chromium browsers.
public sealed class WebUiRemoteDebuggingTests : IDisposable
{
    private static readonly Uri PageUrl = new("http://127.0.0.1:12345/index.html");
    private static readonly WebUiBrowserInstallation Chromium = new(WebUiBrowser.Chrome, "/browser", IsChromiumBased: true);
    private readonly DirectoryInfo _profile = Directory.CreateTempSubdirectory("runic-remote-debugging-");

    private static Func<string, string?> Environment(string? dotnet, string? port, string? aspnet = null) => name => name switch
    {
        "DOTNET_ENVIRONMENT" => dotnet,
        "ASPNETCORE_ENVIRONMENT" => aspnet,
        WebUiRemoteDebugging.PortVariable => port,
        _ => null,
    };

    // Untrimmed builds, such as dotnet runic dev's and these tests, keep the endpoint; trimmed and NativeAOT publishes turn
    // the switch off (desktop-targets.test.mjs).
    [Test]
    public void TheEndpointIsSupportedInUntrimmedBuilds() => Assert.True(WebUiRemoteDebugging.IsSupported);

    [Test]
    public void DevelopmentAsksForTheVariablesPort()
    {
        Assert.Equal(0, WebUiRemoteDebugging.RequestedPort(Environment("Development", "0"), devTools: null));
        Assert.Equal(9225, WebUiRemoteDebugging.RequestedPort(Environment("development", " 9225 "), devTools: true));
        Assert.Equal(0, WebUiRemoteDebugging.RequestedPort(Environment(null, "0", aspnet: "Development"), devTools: null));
    }

    [Test]
    [DisplayName("NoPortOutsideDevelopmentOrWithoutTheVariable(environment: $environment, port: $port)")]
    [Arguments("Production", "0")]
    [Arguments("Staging", "9225")]
    [Arguments(null, "0")]
    [Arguments("Development", null)]
    [Arguments("Development", "")]
    [Arguments("Development", "off")]
    [Arguments("Development", "-1")]
    [Arguments("Development", "65536")]
    public void NoPortOutsideDevelopmentOrWithoutTheVariable(string? environment, string? port) =>
        Assert.Null(WebUiRemoteDebugging.RequestedPort(Environment(environment, port), devTools: null));

    [Test]
    public void DevToolsTurnedOffTurnsThePortOff() =>
        Assert.Null(WebUiRemoteDebugging.RequestedPort(Environment("Development", "0"), devTools: false));

    [Test]
    public void DevelopmentAddsThePortBesideTheDefaults()
    {
        var arguments = WebUiBrowserHost.BuildArguments(Chromium, PageUrl, Options([], port: 0));

        Assert.Single(arguments, argument => argument.StartsWith("--remote-debugging-port", StringComparison.Ordinal));
        Assert.Contains("--remote-debugging-port=0", arguments);
        Assert.Contains("--no-first-run", arguments);
        Assert.Contains("--disable-extensions", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--remote-debugging-address", StringComparison.Ordinal));
        Assert.Equal($"--app={PageUrl.AbsoluteUri}", arguments[^1]);
    }

    [Test]
    public void DevelopmentAddsThePortBesideCustomArguments()
    {
        var arguments = WebUiBrowserHost.BuildArguments(Chromium, PageUrl, Options(["--no-sandbox"], port: 9225));

        Assert.Contains("--remote-debugging-port=9225", arguments);
        Assert.Contains("--no-sandbox", arguments);
    }

    [Test]
    [DisplayName("TheApplicationsOwnPortWinsWithoutADuplicate(argument: $argument)")]
    [Arguments("--remote-debugging-port=9333")]
    [Arguments("--REMOTE-DEBUGGING-PORT=9333")]
    [Arguments("--remote-debugging-port")]
    public void TheApplicationsOwnPortWinsWithoutADuplicate(string argument)
    {
        var arguments = WebUiBrowserHost.BuildArguments(Chromium, PageUrl, Options([argument], port: 0));

        Assert.Single(arguments, value => value.StartsWith("--remote-debugging-port", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(argument, arguments);
    }

    [Test]
    public void NoPortWithoutARequestAProfileOrChromium()
    {
        Assert.DoesNotContain(WebUiBrowserHost.BuildArguments(Chromium, PageUrl, Options([], port: null)),
            argument => argument.StartsWith("--remote-debugging-port", StringComparison.Ordinal));
        Assert.DoesNotContain(WebUiBrowserHost.BuildArguments(Chromium, PageUrl, Options([], port: 0, profile: null)),
            argument => argument.StartsWith("--remote-debugging-port", StringComparison.Ordinal));
        var firefox = new WebUiBrowserInstallation(WebUiBrowser.Firefox, "/firefox", IsChromiumBased: false);
        Assert.DoesNotContain(WebUiBrowserHost.BuildArguments(firefox, PageUrl, Options([], port: 0)),
            argument => argument.StartsWith("--remote-debugging-port", StringComparison.Ordinal));
    }

    [Test]
    public void AFixedPortThatIsTakenFallsBackToAFreeOne()
    {
        using var holder = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        holder.Start();
        var taken = ((System.Net.IPEndPoint)holder.LocalEndpoint).Port;

        using var fallback = WebUiRemoteDebugging.Reserve(taken);
        Assert.Equal(0, fallback.Port);
        Assert.True(fallback.FellBack);
        using var free = WebUiRemoteDebugging.Reserve(0);
        Assert.Equal(0, free.Port);
        Assert.False(free.FellBack);
    }

    [Test]
    public void AFixedPortStaysReservedUntilTheBrowserStarts()
    {
        var port = FreePort();
        var reservation = WebUiRemoteDebugging.Reserve(port);
        Assert.Equal(port, reservation.Port);
        Assert.False(reservation.FellBack);
        // Nobody else can take it on 127.0.0.1 meanwhile.
        using (var other = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port))
        {
            Assert.Throws<System.Net.Sockets.SocketException>(() => other.Start());
        }
        reservation.Dispose();
        using var after = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        after.Start();
    }

    [Test]
    public async Task ReportsTheEndpointWhileTheBrowserRuns()
    {
        using var deadline = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
        await File.WriteAllTextAsync(Path.Combine(_profile.FullName, WebUiRemoteDebugging.ActivePortFile),
            "52444\n/devtools/browser/test", deadline.Token);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var report = WebUiRemoteDebugging.ReportAsync(exited.Task, _profile.FullName, 0,
            line => { lines.Enqueue(line); opened.TrySetResult(); },
            (_, reason) => Assert.Fail($"The endpoint was reported unavailable: {reason}"));

        await opened.Task.WaitAsync(deadline.Token);
        Assert.Equal(["RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp http://127.0.0.1:52444/"], lines.ToArray());

        exited.SetResult();
        await report.WaitAsync(deadline.Token);
        Assert.Equal([
            "RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp http://127.0.0.1:52444/",
            "RUNIC_APPLICATION_DEBUG_ENDPOINT_CLOSED=http://127.0.0.1:52444/",
        ], lines.ToArray());
    }

    [Test]
    public async Task ABrowserThatExitsFirstReportsNothing()
    {
        using var deadline = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
        var lines = new List<string>();
        var unavailable = false;
        await WebUiRemoteDebugging.ReportAsync(Task.CompletedTask, _profile.FullName, 0, lines.Add, (_, _) => unavailable = true)
            .WaitAsync(deadline.Token);
        Assert.Empty(lines);
        Assert.False(unavailable);
    }

    [Test]
    public async Task AFreePortWithoutARecordIsReportedUnavailable()
    {
        using var deadline = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
        var time = new ManualTime();
        var lines = new List<string>();
        string? reason = null;
        var report = WebUiRemoteDebugging.ReportAsync(new TaskCompletionSource().Task, _profile.FullName, 0, lines.Add,
            (_, why) => reason = why, time);

        await time.WaitForTimerAsync(WebUiRemoteDebugging.PortWait, deadline.Token);
        time.Advance(WebUiRemoteDebugging.PortWait);
        await report.WaitAsync(deadline.Token);

        Assert.Empty(lines);
        Assert.Contains("DevToolsActivePort", reason);
    }

    // DEV-SEC-54: Chromium writes no DevToolsActivePort for a fixed port, so the endpoint is confirmed on 127.0.0.1 instead.
    [Test]
    public async Task AFixedPortIsConfirmedThroughVersionWithoutARecord()
    {
        using var deadline = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = Probes(loopback: true, ipv6: false, owners: [1000]);
        var report = WebUiRemoteDebugging.ReportAsync(exited.Task, _profile.FullName, 9225,
            line => { lines.Enqueue(line); opened.TrySetResult(); },
            (_, reason) => Assert.Fail($"The endpoint was reported unavailable: {reason}"), probes: probes);

        await opened.Task.WaitAsync(deadline.Token);
        exited.SetResult();
        await report.WaitAsync(deadline.Token);
        Assert.Equal([
            "RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp http://127.0.0.1:9225/",
            "RUNIC_APPLICATION_DEBUG_ENDPOINT_CLOSED=http://127.0.0.1:9225/",
        ], lines.ToArray());
    }

    // DEV-SEC-51: a fixed port whose listener cannot be the browser's is not reported, and says why.
    [Test]
    [DisplayName("AFixedPortThatIsNotTheBrowsersIsNotReported(case: $case)")]
    [Arguments("foreign-owner")]
    [Arguments("ipv6-too")]
    [Arguments("ipv6-only")]
    [Arguments("silent")]
    public async Task AFixedPortThatIsNotTheBrowsersIsNotReported(string @case)
    {
        using var deadline = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
        var time = new ManualTime();
        var probes = @case switch
        {
            "foreign-owner" => Probes(loopback: true, ipv6: false, owners: [1000, 1001]),
            "ipv6-too" => Probes(loopback: true, ipv6: true, owners: [1000]),
            "ipv6-only" => Probes(loopback: false, ipv6: true, owners: []),
            _ => Probes(loopback: false, ipv6: false, owners: []),
        };
        var lines = new List<string>();
        string? reason = null;
        var report = WebUiRemoteDebugging.ReportAsync(new TaskCompletionSource().Task, _profile.FullName, 9225, lines.Add,
            (port, why) => { Assert.Equal(9225, port); reason = why; }, time, probes);
        if (@case is "ipv6-only" or "silent")
        {
            await time.WaitForTimerAsync(WebUiRemoteDebugging.PortWait, deadline.Token);
            time.Advance(WebUiRemoteDebugging.PortWait);
        }
        await report.WaitAsync(deadline.Token);

        Assert.Empty(lines);
        Assert.Contains(@case switch
        {
            "foreign-owner" => "uid 1001",
            "ipv6-too" => "[::1] answers",
            "ipv6-only" => "listens on [::1]:9225",
            _ => "nothing answered",
        }, reason);
    }

    // Linux: the owners of the listeners on a port, from /proc/net/tcp (DEV-SEC-51).
    [Test]
    public void FindsTheListenersOwnersOnAPort()
    {
        const string table = """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 0100007F:23F1 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1001        0 1 1 0
               1: 00000000:23F1 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1002        0 2 1 0
               2: 0100007F:23F1 0100007F:9C40 01 00000000:00000000 00:00000000 00000000  1003        0 3 1 0
               3: 0100007F:23F2 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1004        0 4 1 0
            """;
        Assert.Equal([1001u, 1002u],
            LoopbackPeerOwner.ListenerOwners(new StringReader(table), System.Net.IPAddress.Loopback, 9201).ToArray());
        Assert.Empty(LoopbackPeerOwner.ListenerOwners(new StringReader(table), System.Net.IPAddress.Loopback, 9203));
    }

    private static WebUiRemoteDebugging.Probes Probes(bool loopback, bool ipv6, uint[] owners) => new(
        (address, _, _) => Task.FromResult(address.Equals(System.Net.IPAddress.Loopback) ? loopback : ipv6),
        _ => owners,
        CurrentUser: 1000);

    // A port that no bind(0) or connection takes before the product binds it, which callers do next (TestPorts).
    private static int FreePort() => TestPorts.Lease().Release();

    // A real browser, when the machine has one: in Development the variable opens a loopback endpoint that answers CDP, and
    // the window reports it, with a free port (DevToolsActivePort) and with a fixed one (/json/version, DEV-SEC-54).
    [Test]
    public Task DevelopmentOpensALoopbackEndpointInAnInstalledBrowser() => OpensEndpointAsync(fixedPort: false);

    [Test]
    public Task DevelopmentOpensAFixedLoopbackEndpointInAnInstalledBrowser() => OpensEndpointAsync(fixedPort: true);

    private static async Task OpensEndpointAsync(bool fixedPort)
    {
        var browser = FindChromiumBrowser();
        if (browser is null) return;

        var requested = fixedPort ? FreePort() : 0;
        var previousEnvironment = System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var previousPort = System.Environment.GetEnvironmentVariable(WebUiRemoteDebugging.PortVariable);
        System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
        System.Environment.SetEnvironmentVariable(WebUiRemoteDebugging.PortVariable, requested.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // TUnit keeps each case's console output, also from the window's background report.
        var context = TestContext.Current!;
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(90));
        WebUiApplication.SetConnectionTimeout((nuint)TestTimeouts.Scale(TimeSpan.FromSeconds(30)).TotalSeconds);
        WebUiApplication.SetBrowserLaunchAttempts(2);
        try
        {
            await using var window = new WebUiWindow();
            window.SetHidden(true);
            window.SetBrowserArguments("--no-first-run --no-sandbox --disable-gpu --disable-dev-shm-usage");
            await window.ShowInBrowserAsync(
                """<!doctype html><html><head><title>debugging</title><script src="webui.js"></script></head><body></body></html>""",
                browser.Value, timeout.Token);

            // The opened line names the port; with a fixed one, the requested port.
            string? line = null;
            while ((line = context.GetStandardOutput().Split('\n').Select(text => text.TrimEnd('\r'))
                       .FirstOrDefault(text => text.StartsWith(WebUiRemoteDebugging.OpenedPrefix, StringComparison.Ordinal))) is null)
            {
                await Task.Delay(50, timeout.Token);
            }
            var port = new Uri(line.Split(' ')[^1]).Port;
            if (fixedPort) Assert.Equal(requested, port);
            using var client = new HttpClient { BaseAddress = new Uri(WebUiRemoteDebugging.EndpointUrl(port)) };
            using var version = JsonDocument.Parse(await client.GetStringAsync("json/version", timeout.Token));
            Assert.StartsWith($"ws://127.0.0.1:{port}/devtools/browser/",
                version.RootElement.GetProperty("webSocketDebuggerUrl").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("not reported", context.GetErrorOutput());

            await window.CloseAsync(timeout.Token);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousEnvironment);
            System.Environment.SetEnvironmentVariable(WebUiRemoteDebugging.PortVariable, previousPort);
            WebUiApplication.SetConnectionTimeout(15);
        }
    }

    private static WebUiBrowser? FindChromiumBrowser()
    {
        foreach (var browser in new[] { WebUiBrowser.Chrome, WebUiBrowser.Edge, WebUiBrowser.Chromium })
        {
            if (WebUiApplication.BrowserExists(browser)) return browser;
        }
        return null;
    }

    private static WebUiBrowserLaunchOptions Options(IReadOnlyList<string> arguments, int? port, string? profile = "/profile") =>
        new(null, profile, null, arguments, Kiosk: false, Hidden: false, null, null, null, null, RemoteDebuggingPort: port);

    public void Dispose() => _profile.Delete(recursive: true);
}
