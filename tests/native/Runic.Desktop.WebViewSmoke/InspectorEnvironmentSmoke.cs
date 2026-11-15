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
            start.Environment[name] = $"127.0.0.1:{FreePort()}";
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
        if (development)
        {
            Require(await AcceptsAsync(port, TimeSpan.FromSeconds(10)), $"Development: {name} opened no port {port}");
            if (name == "WEBKIT_INSPECTOR_HTTP_SERVER")
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var list = await GetTargetsAsync(client, port);
                Require(list.Contains(Title, StringComparison.Ordinal), $"Development: the HTTP inspector on {port} did not list the page: {list}");
            }
        }
        else
        {
            // The server starts with the first web context, before the page loads; a late start gets two seconds anyway.
            await Task.Delay(TimeSpan.FromSeconds(2));
            Require(!await AcceptsAsync(port, TimeSpan.Zero), $"Production: {name} opened port {port}");
        }
        // The port first, the environment it came from second: the native copy for WebKit, .NET's for child processes.
        var native = NativeVariable(name);
        Require(development == native is not null, $"{environment}: the native environment's {name} is {native ?? "unset"}");
        Require(development == Environment.GetEnvironmentVariable(name) is not null, $"{environment}: .NET's {name} is {Environment.GetEnvironmentVariable(name) ?? "unset"}");
        Console.WriteLine($"Inspector environment: {environment}, {name}: {(development ? "open, variable kept" : "closed, variable cleared")}.");
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

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static string? NativeVariable(string name) => Marshal.PtrToStringUTF8(Getenv(name));

    [LibraryImport("libc", EntryPoint = "getenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Getenv(string name);

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Inspector environment: " + what + ".");
    }
}
