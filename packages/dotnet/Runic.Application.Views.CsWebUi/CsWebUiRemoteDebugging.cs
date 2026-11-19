using System.Net;
using System.Net.Sockets;
using CsWebUi;
using Runic.Desktop.Internal;

namespace Runic.Application.Views.CsWebUi;

// sdk#1221 (D070.7, D070.17): the Chrome DevTools Protocol endpoint of a CS-WebUI browser window's Chromium or Edge, which
// dotnet runic dev --debug-endpoint, --no-window and the template's "CS-WebUI script debugging" profile ask for with
// RUNIC_WEBUI_REMOTE_DEBUGGING_PORT. Runic Desktop's installed-browser windows have had it since 0.22
// (Runic.Desktop WebUiRemoteDebugging, linked here); this is the same contract for the windows WebUI's own browser
// launcher starts. It opens only when all of these hold:
// - Development, a valid RUNIC_WEBUI_REMOTE_DEBUGGING_PORT, and an untrimmed build (WebUiRemoteDebugging.RequestedPort,
//   IsSupported);
// - a browser window that this host shows: not UseWebView, ShowWindow or serve-only mode;
// - a Chromium-based browser: CSWEBUI_BROWSER_PATH's, or WebUI's best one.
// WebUI starts each Chromium window with its own WebUI profile, shared by the process's windows, and the host does not know
// its path, so it cannot read DevToolsActivePort: for 0 it reserves a free loopback port itself and confirms the endpoint
// as for a fixed port, through /json/version (and on Linux the listener's owner). WebUI's custom parameters replace its
// Chromium defaults, so the parameters repeat them (WebUiChromiumDefaults) before the port.
internal sealed class CsWebUiRemoteDebugging : IDisposable
{
    // The switches WebUI 24c7d196 passes a Chromium window without custom parameters (webui.c, _webui_get_browser_args),
    // with --no-proxy-server, which it passes without a proxy.
    internal static readonly string[] WebUiChromiumDefaults =
    [
        "--no-first-run", "--safe-mode", "--disable-extensions", "--disable-background-mode", "--disable-plugins",
        "--disable-plugins-discovery", "--disable-translate", "--disable-features=Translate", "--bwsi", "--disable-sync",
        "--disable-sync-preferences", "--disable-component-update", "--allow-insecure-localhost",
        "--auto-accept-camera-and-microphone-capture", "--no-proxy-server",
    ];

    private static readonly string[] ChromiumNames = ["chrome", "chromium", "msedge", "edge", "brave", "vivaldi", "yandex", "epic"];

    private WebUiRemoteDebugging.Reservation? _reservation;

    private CsWebUiRemoteDebugging(WebUiRemoteDebugging.Reservation reservation, int requested)
    {
        _reservation = reservation;
        Port = reservation.Port;
        Requested = requested;
    }

    // The port Chromium listens on, and the one the variable asked for (0 for any).
    internal int Port { get; }

    internal int Requested { get; }

    // The browser's command-line parameters for the window.
    internal string Parameters => string.Join(' ', [.. WebUiChromiumDefaults, WebUiRemoteDebugging.Argument(Port)]);

    // Whether the browser WebUI starts is Chromium-based: the one CSWEBUI_BROWSER_PATH names, else WebUI's best one.
    internal static bool IsChromiumBased(string? configuredPath, WebUiBrowser best)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var name = Path.GetFileNameWithoutExtension(configuredPath.TrimEnd('/', '\\')).ToLowerInvariant();
            return ChromiumNames.Any(name.Contains);
        }
        return best is WebUiBrowser.Chrome or WebUiBrowser.Edge or WebUiBrowser.Chromium or WebUiBrowser.Brave
            or WebUiBrowser.Vivaldi or WebUiBrowser.Epic or WebUiBrowser.Yandex;
    }

    // The endpoint a browser window asks for, with its port reserved until ReleasePort, or null for none. A non-Chromium
    // browser writes a warning instead.
    internal static CsWebUiRemoteDebugging? Request(Func<string, string?> readEnvironment, bool browserWindow, Func<bool> chromiumBased,
        Action<string> warn)
    {
        if (!WebUiRemoteDebugging.IsSupported || !browserWindow) return null;
        if (WebUiRemoteDebugging.RequestedPort(readEnvironment, devTools: null) is not int requested) return null;
        if (!chromiumBased())
        {
            warn($"warning: Runic: {WebUiRemoteDebugging.PortVariable} asks for a DevTools Protocol endpoint, but the CS-WebUI window's browser is not Chromium-based, so it opens none.");
            return null;
        }
        var reservation = requested == 0 ? null : WebUiRemoteDebugging.Reserve(requested);
        if (reservation is { FellBack: true })
            warn($"warning: Runic: port {requested} for the browser's DevTools Protocol endpoint is taken on 127.0.0.1 or [::1]; the browser uses a free port instead, which the {WebUiRemoteDebugging.OpenedPrefix.TrimEnd('=')} line names. Do not attach a debugger to port {requested}.");
        if (reservation is null or { FellBack: true }) reservation = ReserveFree();
        return reservation is null ? null : new CsWebUiRemoteDebugging(reservation, requested);
    }

    // A free port held on 127.0.0.1 and [::1], as WebUiRemoteDebugging.Reserve holds a fixed one.
    private static WebUiRemoteDebugging.Reservation? ReserveFree()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var first = new TcpListener(IPAddress.Loopback, 0);
            first.Start();
            var port = ((IPEndPoint)first.LocalEndpoint).Port;
            first.Dispose();
            var reservation = WebUiRemoteDebugging.Reserve(port);
            if (!reservation.FellBack) return reservation;
        }
        return null;
    }

    // Just before the browser starts.
    internal void ReleasePort()
    {
        _reservation?.Dispose();
        _reservation = null;
    }

    public void Dispose() => ReleasePort();

    // The endpoint of a window the host is about to show, with the window's parameters set, or null for none.
    internal static CsWebUiRemoteDebugging? ForWindow(WebUiWindow window, bool browserWindow)
    {
        // First, so a trimmed build, where the switch is off, keeps none of the code below or its delegates.
        if (!WebUiRemoteDebugging.IsSupported) return null;
        var debugging = Request(Environment.GetEnvironmentVariable, browserWindow,
            () => IsChromiumBased(Environment.GetEnvironmentVariable(WebUiApplication.BrowserPathEnvironmentVariable), window.BestBrowser),
            WebUiRemoteDebugging.WriteWarning);
        if (debugging is null) return null;
        try { window.SetCustomParameters(debugging.Parameters); }
        catch
        {
            debugging.Dispose();
            throw;
        }
        return debugging;
    }

    // Once the window is shown: reports the endpoint in the background, until the browser exits.
    internal void Start(WebUiWindow window)
    {
        nuint processId;
        try { processId = window.BrowserProcessId; }
        catch (ObjectDisposedException) { return; }
        _ = ReportAsync(BrowserExited(processId), WebUiRemoteDebugging.WriteLine, WebUiRemoteDebugging.WriteWarning);
    }

    // After the browser started: confirms the endpoint, writes RUNIC_APPLICATION_DEBUG_ENDPOINT (standard output), and its
    // end when the browser exits; or a warning on standard error when it cannot be confirmed.
    internal Task ReportAsync(Task exited, Action<string> writeLine, Action<string> warn) =>
        WebUiRemoteDebugging.ReportAsync(exited, profilePath: string.Empty, Port, writeLine, (port, reason) =>
            warn($"warning: Runic: the CS-WebUI window's browser opened no DevTools Protocol endpoint on 127.0.0.1:{port}: {reason}. Do not attach a debugger to that port."));

    // Completes when the browser process exits, or never when WebUI reports none.
    internal static Task BrowserExited(nuint processId)
    {
        if (processId == 0) return Task.Delay(Timeout.Infinite);
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(checked((int)processId));
            return process.WaitForExitAsync().ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        {
            return Task.CompletedTask;
        }
    }
}
