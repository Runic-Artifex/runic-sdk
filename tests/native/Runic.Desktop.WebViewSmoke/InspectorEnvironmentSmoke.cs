using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Runic.Desktop;

// --inspector-environment (#1028, D066.38 (3)): WebKitGTK's remote inspector, which developer extras do not gate, opens
// only in Development. The smoke starts itself four times, with WEBKIT_INSPECTOR_SERVER or WEBKIT_INSPECTOR_HTTP_SERVER on
// a free loopback port, in Production and in Development: in Production the window opens no port and neither the native
// nor .NET's environment has the variable any more; in Development the port opens, and the HTTP inspector lists the page.
// One variable at a time: with both set, WebKitGTK 2.54 starts only the HTTP inspector. Linked into
// Runic.Desktop.Gtk4.Smoke for GTK 4.
//
// #1257: the port is free when the parent picks it, but the child binds its own listeners, the presentation server first,
// on port 0. A port from the ephemeral range could go to one of them, which then answered in Production ("opened port")
// and kept WebKit's inspector from binding in Development. On Linux the parent therefore picks a port outside
// ip_local_port_range, which neither bind(0) nor connect() hands out, and the child asks /proc who listens on it: in
// Production nothing of the child's process tree, in Development the child's tree, and a failed check names the holder.
// Elsewhere (the smoke runs on Linux only) it falls back to a connection attempt.
internal static partial class InspectorEnvironmentSmoke
{
    internal const string Argument = "--inspector-environment";
    private const string ChildArgument = "--inspector-environment-child";
    private const string Title = "Runic inspector environment";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private const string Page = $$"""
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>{{Title}}</title>
        </head><body>inspector environment</body></html>
        """;

    internal static int Run(string[] args, DesktopHostOptions hostOptions)
    {
        if (args.Contains(ChildArgument, StringComparer.Ordinal))
        {
            // Read before the host starts, which clears it outside Development.
            var name = Variables.Single(variable => Environment.GetEnvironmentVariable(variable) is not null);
            var port = int.Parse(Environment.GetEnvironmentVariable(name)!.Split(':')[^1], System.Globalization.CultureInfo.InvariantCulture);
            var development = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Development";
            return DesktopEventLoop.Run(hostOptions, host => RunChildAsync(host, development, name, port));
        }
        foreach (var environment in new[] { "Production", "Development" })
        foreach (var name in Variables)
        {
            var start = ChildStart(args);
            start.Environment["DOTNET_ENVIRONMENT"] = environment;
            start.Environment.Remove("ASPNETCORE_ENVIRONMENT");
            foreach (var other in Variables) start.Environment.Remove(other);
            start.Environment[name] = $"127.0.0.1:{InspectorPort()}";
            using var child = Process.Start(start) ?? throw new InvalidOperationException("The inspector smoke could not start itself.");
            if (!child.WaitForExit(TimeSpan.FromSeconds(120)))
            {
                child.Kill(entireProcessTree: true);
                throw new TimeoutException($"Inspector environment: the {environment} window with {name} did not finish.");
            }
            if (child.ExitCode != 0) throw new InvalidOperationException($"Inspector environment: the {environment} window with {name} failed ({child.ExitCode}).");
        }
        Console.WriteLine("PASS inspector environment: the remote inspector opens in Development only.");
        return 0;
    }

    private static readonly string[] Variables = ["WEBKIT_INSPECTOR_SERVER", "WEBKIT_INSPECTOR_HTTP_SERVER"];

    private static async Task<int> RunChildAsync(DesktopHost host, bool development, string name, int port)
    {
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 480, Height = 320 });
        await surface.ExecuteJavaScriptAsync("return document.readyState;", Deadline);
        var environment = development ? "Development" : "Production";
        Require(surface.Url.Port != port, $"{environment}: the presentation server listens on the inspector's port {port}");
        if (development)
        {
            var opened = OperatingSystem.IsLinux()
                ? await TreeListensAsync(port, TimeSpan.FromSeconds(10))
                : await AcceptsAsync(port, TimeSpan.FromSeconds(10));
            Require(opened, $"Development: {name} opened no port {port} in this process's tree ({Holders(port)})");
            if (name == "WEBKIT_INSPECTOR_HTTP_SERVER")
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var list = await GetTargetsAsync(client, port);
                Require(list.Contains(Title, StringComparison.Ordinal),
                    $"Development: the HTTP inspector on {port} did not list the page: {list} ({Holders(port)})");
            }
        }
        else
        {
            // The server starts with the first web context, before the page loads; a late start gets two seconds anyway.
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (OperatingSystem.IsLinux())
            {
                var listeners = PortListeners.Find(port);
                Require(!listeners.Any(listener => listener.InTree), $"Production: {name} opened port {port} ({Holders(port)})");
                // Another process on the port would also have kept WebKit from binding it: then the check proves nothing.
                Require(listeners.Count == 0, $"Production: port {port} is held outside this process's tree, so {name} cannot be checked ({Holders(port)})");
            }
            else
            {
                Require(!await AcceptsAsync(port, TimeSpan.Zero), $"Production: {name} opened port {port}");
            }
        }
        // The port first, the environment it came from second: the native copy for WebKit, .NET's for child processes.
        var native = NativeVariable(name);
        Require(development == native is not null, $"{environment}: the native environment's {name} is {native ?? "unset"}");
        Require(development == Environment.GetEnvironmentVariable(name) is not null, $"{environment}: .NET's {name} is {Environment.GetEnvironmentVariable(name) ?? "unset"}");
        Console.WriteLine($"Inspector environment: {environment}, {name}: {(development ? "open, variable kept" : "closed, variable cleared")} (port {port}).");
        return 0;
    }

    private static async Task<string> GetTargetsAsync(HttpClient client, int port)
    {
        // The page registers as a target once it has loaded; the list can trail it briefly.
        var elapsed = Stopwatch.StartNew();
        var last = "";
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                last = await client.GetStringAsync(new Uri($"http://127.0.0.1:{port}/"));
                if (last.Contains(Title, StringComparison.Ordinal)) return last;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(200);
        }
        return last;
    }

    private static async Task<bool> AcceptsAsync(int port, TimeSpan wait)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port);
                return true;
            }
            catch (SocketException) when (elapsed.Elapsed < wait)
            {
                await Task.Delay(200);
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }

    private static ProcessStartInfo ChildStart(string[] args)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The smoke has no process path.");
        var start = new ProcessStartInfo(processPath) { UseShellExecute = false };
        // As CloseDrainSmoke: under `dotnet <dll>` the child needs the smoke's assembly; an apphost, single-file or native
        // AOT smoke is its own process path (Assembly.Location is empty in a single-file app).
        if (Path.GetFileNameWithoutExtension(processPath) is "dotnet")
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(InspectorEnvironmentSmoke).Assembly.GetName().Name + ".dll"));
        foreach (var argument in args) start.ArgumentList.Add(argument);
        start.ArgumentList.Add(ChildArgument);
        return start;
    }

    // A free loopback port that no bind(0) or connect() can be handed meanwhile: a leased port outside the ephemeral range
    // that no socket uses yet (TestPorts), released for the child.
    private static int InspectorPort() => TestPorts.Lease().Release();

    private static async Task<bool> TreeListensAsync(int port, TimeSpan wait)
    {
        var elapsed = Stopwatch.StartNew();
        while (!PortListeners.Find(port).Any(listener => listener.InTree))
        {
            if (elapsed.Elapsed >= wait) return false;
            await Task.Delay(200);
        }
        return true;
    }

    private static string Holders(int port) => !OperatingSystem.IsLinux() ? "no socket table here"
        : PortListeners.Find(port) is { Count: > 0 } listeners ? $"listening on {port}: {string.Join(", ", listeners)}"
        : $"nothing listens on {port}";

    // Linux: the TCP sockets listening on a port (/proc/net/tcp and tcp6, any address), each with the processes holding it
    // (/proc/<pid>/fd links socket:[inode]) and whether they belong to this process's tree (the process itself and its
    // descendants, such as WebKit's web processes).
    private static class PortListeners
    {
        private const string Listen = "0A";

        internal sealed record Listener(string Address, uint Uid, IReadOnlyList<(int Pid, string Name, bool InTree)> Processes)
        {
            internal bool InTree => Processes.Any(process => process.InTree);

            public override string ToString() => $"{Address} (uid {Uid}) " + (Processes.Count == 0
                ? "held by no process this user can see"
                : "held by " + string.Join(", ", Processes.Select(process =>
                    $"pid {process.Pid} {process.Name}{(process.Pid == Environment.ProcessId ? " (this process)" : process.InTree ? " (this process's tree)" : "")}")));
        }

        internal static IReadOnlyList<Listener> Find(int port)
        {
            var sockets = Rows().Where(row => row.Port == port && row.State == Listen).ToList();
            if (sockets.Count == 0) return [];
            var holders = SocketHolders(sockets.Select(row => row.Inode).ToHashSet());
            return [.. sockets.Select(row => new Listener(row.Address, row.Uid,
                holders.TryGetValue(row.Inode, out var pids) ? [.. pids.Select(pid => (pid, Name(pid), InTree(pid)))] : []))];
        }

        private static IEnumerable<(string Address, int Port, string State, uint Uid, string Inode)> Rows()
        {
            foreach (var table in (string[])["/proc/net/tcp", "/proc/net/tcp6"])
            {
                string[] lines;
                try { lines = File.ReadAllLines(table); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                // sl local_address rem_address st tx_queue:rx_queue tr:tm->when retrnsmt uid timeout inode ...
                foreach (var line in lines.Skip(1))
                {
                    var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 10) continue;
                    var colon = fields[1].LastIndexOf(':');
                    if (colon < 0) continue;
                    if (!int.TryParse(fields[1].AsSpan(colon + 1), System.Globalization.NumberStyles.HexNumber, null, out var port)) continue;
                    if (!uint.TryParse(fields[7], System.Globalization.CultureInfo.InvariantCulture, out var uid)) continue;
                    yield return (FormatAddress(fields[1][..colon]) + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        port, fields[3], uid, fields[9]);
                }
            }
        }

        // The table's address is the kernel's in-memory word order: four bytes per 32-bit word, each word little-endian.
        private static string FormatAddress(string hex)
        {
            var bytes = Convert.FromHexString(hex);
            for (var word = 0; word + 4 <= bytes.Length; word += 4) Array.Reverse(bytes, word, 4);
            var address = new IPAddress(bytes);
            return address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        }

        private static Dictionary<string, List<int>> SocketHolders(HashSet<string> inodes)
        {
            var holders = new Dictionary<string, List<int>>();
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), System.Globalization.CultureInfo.InvariantCulture, out var pid)) continue;
                try
                {
                    foreach (var descriptor in Directory.EnumerateFileSystemEntries(Path.Combine(directory, "fd")))
                    {
                        // socket:[12345]
                        if (new FileInfo(descriptor).LinkTarget is not { } target || !target.StartsWith("socket:[", StringComparison.Ordinal)) continue;
                        var inode = target["socket:[".Length..^1];
                        if (!inodes.Contains(inode)) continue;
                        if (!holders.TryGetValue(inode, out var pids)) holders[inode] = pids = [];
                        if (!pids.Contains(pid)) pids.Add(pid);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Another user's process, or one that has exited.
                }
            }
            return holders;
        }

        private static string Name(int pid)
        {
            try { return File.ReadAllText($"/proc/{pid}/comm").Trim(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return "?"; }
        }

        private static bool InTree(int pid)
        {
            for (var depth = 0; pid > 1 && depth < 64; depth++)
            {
                if (pid == Environment.ProcessId) return true;
                if (Parent(pid) is not { } parent) return false;
                pid = parent;
            }
            return false;
        }

        // /proc/<pid>/stat: pid (comm) state ppid ...; comm may contain spaces and parentheses.
        private static int? Parent(int pid)
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                return int.TryParse(fields[1], System.Globalization.CultureInfo.InvariantCulture, out var parent) ? parent : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static string? NativeVariable(string name) => Marshal.PtrToStringUTF8(Getenv(name));

    [LibraryImport("libc", EntryPoint = "getenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Getenv(string name);

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Inspector environment: " + what + ".");
    }
}
