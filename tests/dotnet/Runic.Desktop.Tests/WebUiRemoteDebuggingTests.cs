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

        Assert.Equal(0, WebUiRemoteDebugging.Available(taken));
        Assert.Equal(0, WebUiRemoteDebugging.Available(0));
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
            _ => Assert.Fail("The endpoint was reported unavailable."));

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
        await WebUiRemoteDebugging.ReportAsync(Task.CompletedTask, _profile.FullName, 0, lines.Add, _ => unavailable = true)
            .WaitAsync(deadline.Token);
        Assert.Empty(lines);
        Assert.False(unavailable);
    }

    [Test]
    public async Task APortThatNeverOpensIsReportedUnavailable()
    {
        using var deadline = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
        var time = new ManualTime();
        var lines = new List<string>();
        int? unavailable = null;
        var exited = new TaskCompletionSource();
        var report = WebUiRemoteDebugging.ReportAsync(exited.Task, _profile.FullName, 9225, lines.Add, port => unavailable = port, time);

        await time.WaitForTimerAsync(WebUiRemoteDebugging.PortWait, deadline.Token);
        time.Advance(WebUiRemoteDebugging.PortWait);
        await report.WaitAsync(deadline.Token);

        Assert.Empty(lines);
        Assert.Equal(9225, unavailable);
    }

    // A real browser, when the machine has one: in Development the variable opens a loopback endpoint that answers CDP.
    [Test]
    public async Task DevelopmentOpensALoopbackEndpointInAnInstalledBrowser()
    {
        var browser = FindChromiumBrowser();
        if (browser is null) return;

        var previousEnvironment = System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var previousPort = System.Environment.GetEnvironmentVariable(WebUiRemoteDebugging.PortVariable);
        System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
        System.Environment.SetEnvironmentVariable(WebUiRemoteDebugging.PortVariable, "0");
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
            var profile = window.GeneratedProfilePath;
            Assert.NotNull(profile);

            var port = await WebUiRemoteDebugging.ReadActivePortAsync(profile!, timeout.Token);
            using var client = new HttpClient { BaseAddress = new Uri(WebUiRemoteDebugging.EndpointUrl(port)) };
            using var version = JsonDocument.Parse(await client.GetStringAsync("json/version", timeout.Token));
            Assert.StartsWith($"ws://127.0.0.1:{port}/devtools/browser/",
                version.RootElement.GetProperty("webSocketDebuggerUrl").GetString(), StringComparison.Ordinal);

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
