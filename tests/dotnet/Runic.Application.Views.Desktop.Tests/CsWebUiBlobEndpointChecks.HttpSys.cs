extern alias cswebui;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using BlobEndpoint = cswebui::Runic.Application.Views.CsWebUi.CsWebUiBlobEndpoint;
using UrlReservations = cswebui::Runic.Application.Views.CsWebUi.CsWebUiUrlReservations;

// On Windows the endpoint is an http.sys registration of http://localhost:{port}/, and http.sys routes a request to the
// longest registered URL that matches it, whichever process registered it. These checks start the endpoint as a standard
// user and have another process, of the same user and of a second standard user (eng/ci/windows-httpsys-agent.ps1), register URLs under it, which http.sys
// must refuse; and they offer the endpoint ports that a URL reservation names, which it must not use.
//
// They need accounts and reservations that only an ephemeral CI runner may get: the CI step "Check http.sys URL isolation
// of the CS-WebUI blob endpoint on Windows" creates them and sets RUNIC_TEST_HTTPSYS=1. Elsewhere the checks return.
internal sealed partial class CsWebUiBlobEndpointChecks
{
    private const string HttpSysVariable = "RUNIC_TEST_HTTPSYS";
    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    private static bool HttpSysRun => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable(HttpSysVariable) == "1";

    // The URLs another process tries to register under the endpoint's: every path the window serves, and the endpoint's
    // own root under the other host categories of http.sys.
    private static string[] PrefixesUnder(int port) =>
    [
        $"http://localhost:{port}/_runic/",
        $"http://localhost:{port}/_runic/blobs/",
        $"http://localhost:{port}/_runic/upload/",
        $"http://LOCALHOST:{port}/_RUNIC/",
        $"http://+:{port}/_runic/",
        $"http://*:{port}/_runic/",
        $"http://127.0.0.1:{port}/_runic/",
        $"http://[::1]:{port}/_runic/",
    ];

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AnotherProcessCannotRegisterAUrlUnderTheEndpoint(bool sameUser)
    {
        if (!HttpSysRun) return;
        RequireStandardUser();
        using var work = new Work();
        using var blobs = work.Blobs();
        using var endpoint = BlobEndpoint.Start(blobs, static () => PagePort, NullLogger.Instance, FreePort)
            ?? throw new InvalidOperationException("The endpoint did not start.");
        var grant = blobs.Grant(ConnectionKey) ?? throw new InvalidOperationException("The hello granted no upload.");
        using var other = await HttpSysProcess.StartAsync(sameUser, PrefixesUnder(endpoint.Port));
        Console.WriteLine($"Port {endpoint.Port}, {(sameUser ? "the same user" : "another standard user")}: {other.Outcome}");
        var answers = new List<string>();
        foreach (var address in Loopbacks())
        {
            var answer = await UploadAsync(address, endpoint.Port, grant.Token, "over " + address);
            if (answer.Status != 201 || blobs.Find(answer.Id!, ConnectionKey) is null) answers.Add($"over {address}: {answer.Status} {answer.Body}");
        }
        Require(other.Registered.Count == 0 && other.Requests.Count == 0 && answers.Count == 0,
            $"Another process registered [{string.Join(", ", other.Registered)}] under the endpoint's port and received " +
            $"[{string.Join("; ", other.Requests)}]; uploads the window did not receive: [{string.Join("; ", answers)}].");
    }

    // The CI step reserved http://localhost:{P1}/ and http://localhost:{P2}/_runic/ for every user. Through the first, any
    // user may register a URL under the endpoint's after it started there, as the second step of this check shows; the
    // second refuses the endpoint the port. The endpoint is offered both ports first and uses neither.
    [Test]
    public async Task APortThatAUrlReservationNamesIsNotUsed()
    {
        if (!HttpSysRun) return;
        RequireStandardUser();
        var reserved = (Environment.GetEnvironmentVariable("RUNIC_TEST_HTTPSYS_RESERVED_PORTS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        Require(reserved.Length == 2, "Set RUNIC_TEST_HTTPSYS_RESERVED_PORTS to the two reserved ports.");
        foreach (var port in reserved)
            Require(UrlReservations.Naming(port).Count > 0, $"No URL reservation names port {port}.");

        // What the reservation of the whole port allows: a URL under a listener's, for another standard user.
        using (var listener = new System.Net.HttpListener())
        {
            listener.Prefixes.Add($"http://localhost:{reserved[0]}/");
            listener.Start();
            using var other = await HttpSysProcess.StartAsync(sameUser: false, [$"http://localhost:{reserved[0]}/_runic/"]);
            Console.WriteLine($"Reserved port {reserved[0]}, another standard user: {other.Outcome}");
            Require(other.Registered.Count == 1, $"Under a reservation, another user could not register a URL under a listener: {other.Outcome}");
        }

        using var work = new Work();
        using var blobs = work.Blobs();
        var offered = new Queue<int>(reserved);
        using var endpoint = BlobEndpoint.Start(blobs, static () => PagePort, NullLogger.Instance,
            () => offered.Count > 0 ? offered.Dequeue() : FreePort()) ?? throw new InvalidOperationException("The endpoint did not start.");
        Require(offered.Count == 0 && !reserved.Contains(endpoint.Port),
            $"The endpoint listens on port {endpoint.Port}, which a URL reservation names ({string.Join(", ", reserved)}).");
        using var late = await HttpSysProcess.StartAsync(sameUser: false, PrefixesUnder(endpoint.Port));
        Require(late.Registered.Count == 0, $"Another process registered {string.Join(", ", late.Registered)} under the endpoint's port.");

        // Offered only reserved ports, the endpoint does not start and names the reservations, not another process.
        var logger = new EventLogger();
        var again = new Queue<int>(Enumerable.Range(0, 5).Select(i => reserved[i % 2]));
        using var spare = work.Blobs();
        using var none = BlobEndpoint.Start(spare, static () => PagePort, logger, () => again.Dequeue());
        Require(none is null, $"The endpoint started on {none?.BaseUrl}.");
        Require(logger.Events.Any(e => e.StartsWith("2104 CsWebUiBlobEndpointPortsReserved: URL reservations of http.sys (", StringComparison.Ordinal)
                && e.EndsWith(") name 5 of the 5 ports the window's blob endpoint tried for localhost, so it gave up localhost.", StringComparison.Ordinal))
            && !logger.Events.Any(e => e.StartsWith("2103 ", StringComparison.Ordinal)),
            $"The endpoint logged {string.Join(" | ", logger.Events)}.");
    }

    // Where the reservations cannot be read, also because httpapi or one of its entry points does not load, the endpoint
    // does not start (the window opens without blobs). The reader is a fake here, so this runs on every OS.
    [Test]
    [Arguments("win32")]
    [Arguments("dll")]
    [Arguments("entry-point")]
    public async Task UnreadableUrlReservationsFailClosed(string failure)
    {
        Exception error = failure switch
        {
            "win32" => new System.ComponentModel.Win32Exception(5),
            "dll" => new DllNotFoundException("httpapi"),
            _ => new EntryPointNotFoundException("HttpQueryServiceConfiguration"),
        };
        try
        {
            BlobEndpoint.Reservations(61000, _ => throw error);
            Require(false, "Reading the reservations did not fail.");
        }
        catch (PlatformNotSupportedException closed)
        {
            Require(ReferenceEquals(closed.InnerException, error), $"The failure wraps {closed.InnerException}.");
        }
        Require(BlobEndpoint.Reservations(61000, _ => ["http://+:61000/"]) is ["http://+:61000/"], "A readable list is passed on.");
        await Task.CompletedTask;
    }

    [Test]
    public async Task UrlReservationPortsAreRead()
    {
        if (!OperatingSystem.IsWindows()) return;
        Require(UrlReservations.PortOf("http://+:80/Temporary_Listen_Addresses/") == 80, "+:80");
        Require(UrlReservations.PortOf("https://*:5358/") == 5358, "*:5358");
        Require(UrlReservations.PortOf("http://[::1]:8080/a:b/") == 8080, "[::1]:8080");
        Require(UrlReservations.PortOf("http://localhost:61000") == 61000, "localhost:61000");
        Require(UrlReservations.PortOf("http://[::1]/") is null, "[::1] without a port");
        // The list itself: the call succeeds for any user, and every entry names a port.
        int[] ports = [80, 5985, 47001];
        var all = ports.SelectMany(UrlReservations.Naming).ToList();
        Console.WriteLine($"Reservations on ports 80, 5985 and 47001: {string.Join(", ", all)}");
        await Task.CompletedTask;
    }

    private static void RequireStandardUser()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        Require(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) && identity.Owner?.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) != true,
            $"The http.sys checks run as {identity.Name}, which is elevated; run them as a standard user.");
    }

    // Another process, of the same user or of the second standard user, that windows-httpsys-agent.ps1 runs: it tries to
    // register each URL with a listener of its own, reports which it holds, and records each request it receives.
    private sealed class HttpSysProcess : IDisposable
    {
        private readonly string _log;
        private readonly string _stop;

        private HttpSysProcess(string log, string stop)
        {
            _log = log;
            _stop = stop;
        }

        internal List<string> Registered => Lines("registered ");
        internal List<string> Requests => Lines("request ");
        internal string Outcome => string.Join("; ", Lines(""));

        internal static async Task<HttpSysProcess> StartAsync(bool sameUser, IReadOnlyList<string> prefixes)
        {
            var agents = Environment.GetEnvironmentVariable("RUNIC_TEST_HTTPSYS_AGENTS");
            Require(!string.IsNullOrEmpty(agents), "Set RUNIC_TEST_HTTPSYS_AGENTS to the folder of the other processes.");
            var folder = Path.Combine(agents!, sameUser ? "same" : "other");
            var id = Guid.NewGuid().ToString("N");
            var other = new HttpSysProcess(Path.Combine(folder, id + ".log"), Path.Combine(folder, id + ".stop"));
            var pending = Path.Combine(folder, id + ".pending");
            await File.WriteAllLinesAsync(pending, prefixes);
            File.Move(pending, Path.Combine(folder, id + ".prefixes"));
            var until = DateTime.UtcNow.AddSeconds(60);
            while (other.Lines("ready").Count == 0)
            {
                if (DateTime.UtcNow > until) throw new InvalidOperationException($"The other process did not answer in {folder}: {other.Outcome}");
                await Task.Delay(50);
            }
            return other;
        }

        private List<string> Lines(string prefix)
        {
            string[] lines;
            try
            {
                using var stream = new FileStream(_log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                lines = reader.ReadToEnd().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            }
            catch (Exception error) when (error is FileNotFoundException or IOException) { lines = []; }
            return lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }

        // Closes the other process's listeners, and waits until they are closed.
        public void Dispose()
        {
            try { File.WriteAllText(_stop, ""); }
            catch (IOException) { return; }
            var until = DateTime.UtcNow.AddSeconds(10);
            while (Lines("stopped").Count == 0 && DateTime.UtcNow < until) Thread.Sleep(50);
        }
    }
}
