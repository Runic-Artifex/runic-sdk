extern alias cswebui;
using System.Net;
using System.Net.Sockets;
using CsWebUi;
using TUnit.Core;
using CsWebUiRemoteDebugging = cswebui::Runic.Application.Views.CsWebUi.CsWebUiRemoteDebugging;

// sdk#1221: a CS-WebUI browser window's Development DevTools Protocol endpoint. Which windows ask for one, and the
// browser parameters they get; the probes' --probe-debug-endpoint checks a real Chromium or Edge.
internal sealed class CsWebUiRemoteDebuggingChecks
{
    private static Func<string, string?> Environment(string? port, string? environment = "Development") => name => name switch
    {
        "DOTNET_ENVIRONMENT" => environment,
        "RUNIC_WEBUI_REMOTE_DEBUGGING_PORT" => port,
        _ => null,
    };

    [Test]
    public void OnlyADevelopmentBrowserWindowThatAsksGetsAnEndpoint()
    {
        var warnings = new List<string>();
        Require(CsWebUiRemoteDebugging.Request(Environment(null), browserWindow: true, () => true, warnings.Add) is null, "Without the variable an endpoint opened.");
        Require(CsWebUiRemoteDebugging.Request(Environment("0", "Production"), browserWindow: true, () => true, warnings.Add) is null,
            "Outside Development an endpoint opened.");
        Require(CsWebUiRemoteDebugging.Request(Environment("0"), browserWindow: false, () => true, warnings.Add) is null,
            "A WebView, ShowWindow or serve-only window asked for an endpoint.");
        Require(CsWebUiRemoteDebugging.Request(Environment("x"), browserWindow: true, () => true, warnings.Add) is null, "An invalid port opened an endpoint.");
        Require(warnings.Count == 0, $"Warned: {string.Join(" | ", warnings)}");
        Require(CsWebUiRemoteDebugging.Request(Environment("0"), browserWindow: true, () => false, warnings.Add) is null
            && warnings.Single().Contains("not Chromium-based", StringComparison.Ordinal), $"A non-Chromium browser: {string.Join(" | ", warnings)}");
    }

    [Test]
    public void AFreePortKeepsWebUisChromiumDefaults()
    {
        using var debugging = CsWebUiRemoteDebugging.Request(Environment("0"), browserWindow: true, () => true, _ => { })
            ?? throw new InvalidOperationException("Port 0 opened no endpoint.");
        Require(debugging.Port > 0 && debugging.Requested == 0, $"Port {debugging.Port}.");
        Require(debugging.Parameters == string.Join(' ', CsWebUiRemoteDebugging.WebUiChromiumDefaults) + $" --remote-debugging-port={debugging.Port}",
            $"Parameters: {debugging.Parameters}");
        // Held until the browser starts.
        Require(!CanListen(debugging.Port), "The free port was not held.");
        debugging.ReleasePort();
        Require(CanListen(debugging.Port), "The free port stayed held after ReleasePort.");
    }

    [Test]
    public void AFixedPortIsKeptOrReplacedWhenTaken()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var taken = ((IPEndPoint)probe.LocalEndpoint).Port;
        var warnings = new List<string>();
        using (var replaced = CsWebUiRemoteDebugging.Request(Environment(taken.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                   browserWindow: true, () => true, warnings.Add) ?? throw new InvalidOperationException("A taken port opened no endpoint."))
            Require(replaced.Port != taken && replaced.Port > 0 && warnings.Single().Contains($"port {taken}", StringComparison.Ordinal),
                $"Taken port {taken}: got {replaced.Port}, warned {string.Join(" | ", warnings)}");
        probe.Stop();
        using var kept = CsWebUiRemoteDebugging.Request(Environment(taken.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            browserWindow: true, () => true, _ => { }) ?? throw new InvalidOperationException("A free fixed port opened no endpoint.");
        Require(kept.Port == taken, $"A free fixed port {taken} became {kept.Port}.");
    }

    [Test]
    public void TheBrowserIsChromiumBasedByItsPathOrWebUisChoice()
    {
        foreach (var path in new[] { "/usr/bin/google-chrome-stable", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "/run/current-system/sw/bin/chromium", "/Applications/Brave Browser.app" })
            Require(CsWebUiRemoteDebugging.IsChromiumBased(path, WebUiBrowser.Firefox), $"{path} is Chromium-based.");
        Require(!CsWebUiRemoteDebugging.IsChromiumBased("/usr/bin/firefox", WebUiBrowser.Chrome), "Firefox counted as Chromium-based.");
        Require(CsWebUiRemoteDebugging.IsChromiumBased(null, WebUiBrowser.Edge) && !CsWebUiRemoteDebugging.IsChromiumBased(null, WebUiBrowser.Firefox),
            "WebUI's choice was not read.");
    }

    private static bool CanListen(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException) { return false; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
