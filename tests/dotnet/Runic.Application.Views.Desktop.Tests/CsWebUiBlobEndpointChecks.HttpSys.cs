extern alias cswebui;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using BlobEndpoint = cswebui::Runic.Application.Views.CsWebUi.CsWebUiBlobEndpoint;
using UrlReservations = cswebui::Runic.Application.Views.CsWebUi.CsWebUiUrlReservations;

// On Windows the endpoint is an http.sys registration of http://localhost:{port}/, and http.sys routes a request to the
// longest registered URL that matches it, whichever process registered it. These checks start the endpoint as a standard
// user and have another process, of the same user and of a second standard user, register URLs under it, which http.sys
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
        using var other = await HttpSysProcess.StartAsync(sameUser ? null : OtherUser(), PrefixesUnder(endpoint.Port));
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
            using var other = await HttpSysProcess.StartAsync(OtherUser(), [$"http://localhost:{reserved[0]}/_runic/"]);
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
        using var late = await HttpSysProcess.StartAsync(OtherUser(), PrefixesUnder(endpoint.Port));
        Require(late.Registered.Count == 0, $"Another process registered {string.Join(", ", late.Registered)} under the endpoint's port.");
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

    private static (string User, string Password) OtherUser()
    {
        var user = Environment.GetEnvironmentVariable("RUNIC_TEST_HTTPSYS_OTHER_USER");
        var password = Environment.GetEnvironmentVariable("RUNIC_TEST_HTTPSYS_OTHER_PASSWORD");
        Require(!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(password),
            "Set RUNIC_TEST_HTTPSYS_OTHER_USER and RUNIC_TEST_HTTPSYS_OTHER_PASSWORD to a second standard user.");
        return (user!, password!);
    }

    // Another process (PowerShell) that tries to register each URL with a listener of its own, reports which it holds, and
    // records each request it receives.
    private sealed class HttpSysProcess : IDisposable
    {
        private const string Script = """
            $ErrorActionPreference = 'Stop'
            $listeners = @()
            foreach ($prefix in $args) {
                $listener = [Net.HttpListener]::new()
                $listener.Prefixes.Add($prefix)
                try { $listener.Start(); $listeners += $listener; [Console]::WriteLine("registered $prefix") }
                catch {
                    $e = $_.Exception; while ($e -and $e -isnot [Net.HttpListenerException]) { $e = $e.InnerException }
                    [Console]::WriteLine("refused $prefix $($e.ErrorCode)")
                }
            }
            [Console]::WriteLine("user $([Security.Principal.WindowsIdentity]::GetCurrent().Name)")
            [Console]::WriteLine('ready')
            $pending = @($listeners | ForEach-Object { $_.GetContextAsync() })
            while ($true) {
                for ($i = 0; $i -lt $listeners.Count; $i++) {
                    if (-not $pending[$i].IsCompleted) { continue }
                    $context = $pending[$i].Result
                    $pending[$i] = $listeners[$i].GetContextAsync()
                    [Console]::WriteLine("request $($context.Request.HttpMethod) $($context.Request.Url.AbsolutePath)")
                    $context.Response.StatusCode = 200
                    $context.Response.Close()
                }
                Start-Sleep -Milliseconds 20
            }
            """;

        private readonly Process _process;
        private readonly List<string> _lines = [];

        private HttpSysProcess(Process process) => _process = process;

        internal List<string> Registered => Lines("registered ");
        internal List<string> Requests => Lines("request ");
        internal string Outcome => string.Join("; ", Lines(""));

        internal static async Task<HttpSysProcess> StartAsync((string User, string Password)? user, IReadOnlyList<string> prefixes)
        {
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.SystemDirectory,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)), "-args" })
                start.ArgumentList.Add(argument);
            foreach (var prefix in prefixes) start.ArgumentList.Add(prefix);
            if (user is var (name, password) && OperatingSystem.IsWindows())
            {
                start.UserName = name;
                start.Domain = ".";
                start.LoadUserProfile = true;
                var secure = new SecureString();
                foreach (var character in password) secure.AppendChar(character);
                secure.MakeReadOnly();
                start.Password = secure;
            }
            var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
            var other = new HttpSysProcess(process);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, line) =>
            {
                if (line.Data is null) { ready.TrySetResult(); return; }
                lock (other._lines) other._lines.Add(line.Data);
                if (line.Data == "ready") ready.TrySetResult();
            };
            process.ErrorDataReceived += (_, line) =>
            {
                if (line.Data is not null) lock (other._lines) other._lines.Add("stderr " + line.Data);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(60)); }
            catch (TimeoutException) { other.Dispose(); throw new InvalidOperationException($"The other process did not get ready: {other.Outcome}"); }
            Require(other.Lines("ready").Count == 1, $"The other process ended: {other.Outcome}");
            return other;
        }

        private List<string> Lines(string prefix)
        {
            lock (_lines) return _lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }

        public void Dispose()
        {
            try { _process.Kill(); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _process.WaitForExit(5000);
            _process.Dispose();
        }
    }
}
