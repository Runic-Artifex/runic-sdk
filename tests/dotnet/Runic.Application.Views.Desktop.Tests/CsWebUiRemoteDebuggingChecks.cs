extern alias cswebui;
using System.Net;
using System.Net.Sockets;
using CsWebUi;
using TUnit.Core;
using CsWebUiListenerOwner = cswebui::Runic.Application.Views.CsWebUi.CsWebUiListenerOwner;
using CsWebUiRemoteDebugging = cswebui::Runic.Application.Views.CsWebUi.CsWebUiRemoteDebugging;
using Session = cswebui::Runic.Application.Views.CsWebUi.CsWebUiRemoteDebugging.Session;

// sdk#1221: a CS-WebUI browser window's Development DevTools Protocol endpoint. Which windows ask for one, the browser
// parameters and the per-run profile they get (DEV-SEC-85), and how the endpoint is confirmed (DEV-SEC-84); the probes'
// --probe-debug-endpoint checks a real Chromium or Edge.
internal sealed class CsWebUiRemoteDebuggingChecks : IDisposable
{
    private readonly List<string> _profiles = [];

    public void Dispose()
    {
        foreach (var profile in _profiles)
        {
            try { Directory.Delete(profile, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    private CsWebUiRemoteDebugging? Request(Session session, string? port, Action<string>? warn = null, bool browserWindow = true,
        bool chromium = true, string? environment = "Development")
    {
        var debugging = CsWebUiRemoteDebugging.Request(Environment(port, environment), browserWindow, () => chromium, warn ?? (_ => { }), session);
        if (debugging is not null) _profiles.Add(debugging.Profile);
        return debugging;
    }

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
        var session = new Session();
        Require(Request(session, null, warnings.Add) is null, "Without the variable an endpoint opened.");
        Require(Request(session, "0", warnings.Add, environment: "Production") is null, "Outside Development an endpoint opened.");
        Require(Request(session, "0", warnings.Add, browserWindow: false) is null, "A WebView, ShowWindow or serve-only window asked for an endpoint.");
        Require(Request(session, "x", warnings.Add) is null, "An invalid port opened an endpoint.");
        Require(warnings.Count == 0, $"Warned: {string.Join(" | ", warnings)}");
        Require(Request(session, "0", warnings.Add, chromium: false) is null
            && warnings.Single().Contains("not Chromium-based", StringComparison.Ordinal), $"A non-Chromium browser: {string.Join(" | ", warnings)}");
        // None of them set a profile up.
        Require(session.Profile is null && session.Opener is null, $"A window without an endpoint created profile {session.Profile}.");
    }

    [Test]
    public void AFreePortKeepsWebUisChromiumDefaultsInAProfileOfTheRunsOwn()
    {
        using var debugging = Request(new Session(), "0") ?? throw new InvalidOperationException("Port 0 opened no endpoint.");
        Require(debugging.Opens && debugging.Port == 0 && debugging.Requested == 0, $"Port {debugging.Port}, opens {debugging.Opens}.");
        Require(debugging.Parameters == string.Join(' ', CsWebUiRemoteDebugging.WebUiChromiumDefaults) + " --remote-debugging-port=0",
            $"Parameters: {debugging.Parameters}");
        // Not WebUI's shared .WebUI profile: a new directory that only the user can open.
        Require(Directory.Exists(debugging.Profile) && Path.GetFileName(debugging.Profile).StartsWith(CsWebUiRemoteDebugging.ProfilePrefix, StringComparison.Ordinal)
            && Path.GetFullPath(Path.GetDirectoryName(debugging.Profile)!).TrimEnd(Path.DirectorySeparatorChar) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
            $"Profile {debugging.Profile}.");
        if (!OperatingSystem.IsWindows())
            Require(File.GetUnixFileMode(debugging.Profile) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
                $"Profile mode {File.GetUnixFileMode(debugging.Profile)}.");
    }

    [Test]
    public void AFixedPortIsHeldUntilTheBrowserStartsOrReplacedByAFreeOneWhenTaken()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var taken = ((IPEndPoint)probe.LocalEndpoint).Port;
        var warnings = new List<string>();
        using (var replaced = Request(new Session(), taken.ToString(System.Globalization.CultureInfo.InvariantCulture), warnings.Add)
                   ?? throw new InvalidOperationException("A taken port opened no endpoint."))
            Require(replaced.Port == 0 && replaced.Requested == taken && replaced.Parameters.EndsWith(" --remote-debugging-port=0", StringComparison.Ordinal)
                && warnings.Single().Contains($"port {taken}", StringComparison.Ordinal),
                $"Taken port {taken}: got {replaced.Port}, warned {string.Join(" | ", warnings)}");
        probe.Stop();
        using var kept = Request(new Session(), taken.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ?? throw new InvalidOperationException("A free fixed port opened no endpoint.");
        Require(kept.Port == taken, $"A free fixed port {taken} became {kept.Port}.");
        Require(!CanListen(taken), "The fixed port was not held.");
        kept.ReleasePort();
        Require(CanListen(taken), "The fixed port stayed held after ReleasePort.");
    }

    [Test]
    public async Task LaterWindowsJoinTheOpenersBrowserUntilItExits()
    {
        var browser = new TaskCompletionSource();
        var session = new Session { BrowserExited = _ => browser.Task };
        var opener = Request(session, "0") ?? throw new InvalidOperationException("No endpoint.");
        File.WriteAllText(Path.Combine(opener.Profile, "DevToolsActivePort"), "41000\n/devtools/browser/x");
        // Shown, so its browser is known.
        opener.ReleasePort();
        var exited = opener.Started(processId: 4242) ?? throw new InvalidOperationException("The opener reported no browser.");
        var joined = Request(session, "0") ?? throw new InvalidOperationException("A later window got no profile.");
        Require(!joined.Opens && joined.Profile == opener.Profile && joined.Parameters == string.Join(' ', CsWebUiRemoteDebugging.WebUiChromiumDefaults),
            $"A later window opens {joined.Opens} with {joined.Parameters} in {joined.Profile}.");
        Require(joined.Started(processId: 4242) is null, "A joining window reported an endpoint of its own.");
        // The browser exits: the profile goes, and the next window opens a new endpoint in a new one.
        browser.SetResult();
        await exited;
        await WaitAsync(() => !Directory.Exists(opener.Profile), "The profile stayed after its browser exited.");
        using var next = Request(session, "0") ?? throw new InvalidOperationException("No endpoint after the browser exited.");
        Require(next.Opens && next.Profile != opener.Profile, $"The next window opens {next.Opens} in {next.Profile}.");
    }

    [Test]
    public async Task AProfileStaysWhileAJoiningWindowMayStartABrowser()
    {
        var browser = new TaskCompletionSource();
        var session = new Session { BrowserExited = _ => browser.Task };
        var opener = Request(session, "0") ?? throw new InvalidOperationException("No endpoint.");
        var exited = opener.Started(processId: 4242) ?? throw new InvalidOperationException("The opener reported no browser.");
        using var joining = Request(session, "0") ?? throw new InvalidOperationException("A later window got no profile.");
        browser.SetResult();
        await exited;
        await Task.Delay(100);
        Require(Directory.Exists(opener.Profile) && session.Opener is null && session.Profile == opener.Profile,
            "The profile was deleted while a joining window was being shown.");
        // That window's browser may have started in it: the next window opens there, and a stale record goes first.
        File.WriteAllText(Path.Combine(opener.Profile, "DevToolsActivePort"), "41000\n/devtools/browser/x");
        joining.Dispose();
        using var next = Request(session, "0") ?? throw new InvalidOperationException("No endpoint.");
        Require(next.Opens && next.Profile == opener.Profile && !File.Exists(Path.Combine(next.Profile, "DevToolsActivePort")),
            "The next window did not reuse the profile without its stale record.");
    }

    [Test]
    public void AnOpenerThatWasNotShownGivesWay()
    {
        var session = new Session { BrowserExited = _ => new TaskCompletionSource().Task };
        var opener = Request(session, "0") ?? throw new InvalidOperationException("No endpoint.");
        opener.Dispose();
        Require(session.Opener is null && Directory.Exists(opener.Profile), "A window that was not shown kept the endpoint or lost the profile.");
        using var next = Request(session, "0") ?? throw new InvalidOperationException("No endpoint.");
        Require(next.Opens && next.Profile == opener.Profile, "The next window did not open the endpoint.");
    }

    [Test]
    public async Task TheFreePortComesFromTheRunsProfile()
    {
        var session = new Session();
        using var debugging = Request(session, "0") ?? throw new InvalidOperationException("No endpoint.");
        File.WriteAllText(Path.Combine(debugging.Profile, "DevToolsActivePort"), "41007\n/devtools/browser/x");
        var lines = new List<string>();
        var warnings = new List<string>();
        await debugging.ReportAsync(Task.Delay(TimeSpan.FromMilliseconds(300)), processId: 4242, lines.Add, warnings.Add);
        Require(lines.SequenceEqual(["RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp http://127.0.0.1:41007/",
            "RUNIC_APPLICATION_DEBUG_ENDPOINT_CLOSED=http://127.0.0.1:41007/"]) && warnings.Count == 0,
            $"Lines: {string.Join(" | ", lines)}; warnings: {string.Join(" | ", warnings)}");
    }

    [Test]
    public async Task AFixedPortIsReportedOnlyWhenTheBrowserHoldsItsListener()
    {
        foreach (var foreign in new[] { "the listener there is not the browser process 4242", null })
        {
            var port = FreePort();
            var session = new Session();
            using var debugging = Request(session, port.ToString(System.Globalization.CultureInfo.InvariantCulture))
                ?? throw new InvalidOperationException("No endpoint.");
            debugging.ReleasePort();
            var lines = new List<string>();
            var warnings = new List<string>();
            var checkedPort = 0;
            var probes = new cswebui::Runic.Desktop.Internal.WebUiRemoteDebugging.Probes(
                (address, _, _) => Task.FromResult(address.Equals(IPAddress.Loopback)), _ => [], 0,
                port => { checkedPort = port; return foreign; });
            await debugging.ReportAsync(Task.Delay(TimeSpan.FromMilliseconds(300)), processId: 4242, lines.Add, warnings.Add, probes);
            Require(checkedPort == port, $"The listener of port {checkedPort} was checked, not {port}.");
            if (foreign is null)
                Require(lines.Count == 2 && lines[0] == $"RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp http://127.0.0.1:{port}/" && warnings.Count == 0,
                    $"Lines: {string.Join(" | ", lines)}; warnings: {string.Join(" | ", warnings)}");
            else
                Require(lines.Count == 0 && warnings.Single().Contains(foreign, StringComparison.Ordinal)
                    && warnings[0].Contains($"127.0.0.1:{port}", StringComparison.Ordinal),
                    $"A foreign listener: lines {string.Join(" | ", lines)}; warnings {string.Join(" | ", warnings)}");
        }
    }

    [Test]
    public async Task ABrowserWebUiDidNotFindReportsNoEndpoint()
    {
        using var debugging = Request(new Session(), FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture))
            ?? throw new InvalidOperationException("No endpoint.");
        debugging.ReleasePort();
        var lines = new List<string>();
        var warnings = new List<string>();
        await debugging.ReportAsync(Task.CompletedTask, processId: 0, lines.Add, warnings.Add);
        Require(lines.Count == 0 && warnings.Single().Contains("WebUI found no browser process", StringComparison.Ordinal),
            $"Lines: {string.Join(" | ", lines)}; warnings: {string.Join(" | ", warnings)}");
    }

    [Test]
    public void TheListenerOwnerIsReadFromTheProcessSide()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var pid = (nuint)self.Id;
        var started = self.StartTime.ToUniversalTime();
        Require(CsWebUiListenerOwner.ForeignListener(pid, started, port) is null,
            $"This process's own listener on {port}: {CsWebUiListenerOwner.ForeignListener(pid, started, port)}");
        Require(CsWebUiListenerOwner.ForeignListener(pid, DateTime.UtcNow + TimeSpan.FromMinutes(1), port)?.Contains("could not be confirmed", StringComparison.Ordinal) == true,
            "A process that started before the window was shown passed.");
        listener.Stop();
        Require(CsWebUiListenerOwner.ForeignListener(pid, started, port)?.Contains("not the browser process", StringComparison.Ordinal) == true,
            "A port nobody holds passed.");
        Require(CsWebUiListenerOwner.ForeignListener(0, started, port) is not null, "No browser process passed.");
    }

    [Test]
    public void TheListenerTablesParse()
    {
        // /proc/net/tcp on a little-endian machine: 127.0.0.1:9225 listening, a connection on the same port, and
        // 0.0.0.0:9225 listening.
        var linux = """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 0100007F:2409 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 26262130 1 0000000000000000 100 0 0 10 0
               1: 0100007F:2409 0100007F:D431 01 00000000:00000000 00:00000000 00000000  1000        0 26262131 1 0000000000000000 20 4 30 10 -1
               2: 00000000:2409 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1001        0 26262132 1 0000000000000000 100 0 0 10 0
            """;
        if (BitConverter.IsLittleEndian)
        {
            var inodes = CsWebUiListenerOwner.LinuxListenerInodes(new StringReader(linux), 9225).ToList();
            Require(inodes.SequenceEqual(["26262130"]), $"Inodes: {string.Join(", ", inodes)}");
        }
        // MIB_TCPTABLE_OWNER_PID: two rows, 0.0.0.0:9225 of process 7, then 127.0.0.1:9225 of process 4242.
        var table = new byte[4 + 2 * 24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(table, 2);
        void Row(int index, byte[] address, uint pid)
        {
            var row = table.AsSpan(4 + index * 24, 24);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(row, 2);
            address.CopyTo(row[4..]);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(row[8..], 9225);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(row[20..], pid);
        }
        Row(0, [0, 0, 0, 0], 7);
        Row(1, [127, 0, 0, 1], 4242);
        Require(CsWebUiListenerOwner.WindowsTableHolds(table, 4242, 9225), "The browser's row was not found.");
        Require(!CsWebUiListenerOwner.WindowsTableHolds(table, 7, 9225) && !CsWebUiListenerOwner.WindowsTableHolds(table, 4242, 9226),
            "Another process or port matched.");
        Require(CsWebUiListenerOwner.LsofListsProcess("4242\n", 4242) && !CsWebUiListenerOwner.LsofListsProcess("42420\n", 4242)
            && !CsWebUiListenerOwner.LsofListsProcess("", 4242), "lsof's output was misread.");
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

    private static async Task WaitAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException(message);
            await Task.Delay(25);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
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
