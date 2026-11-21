using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Runic.Desktop;

// --webview2-environment (D067.15), Windows: WebView2 opens a DevTools Protocol endpoint for
// WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<port> in any process, so the host clears the variable
// outside Development before it creates its WebView2 environment. The smoke starts itself in Production and in
// Development with the variable on a free loopback port. Production: the window opens no port, and the variable is gone
// from the process environment. Development: the variable stays, and the port opens. An elevated process does not open
// it either: WebView2 ignores the variable there (Runtime 150 and later), and CI's Windows runner is elevated, so there
// the smoke checks the variable only and says so.
internal static class WebView2EnvironmentSmoke
{
    internal const string Argument = "--webview2-environment";
    private const string ChildArgument = "--webview2-environment-child";
    private const string Variable = "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic WebView2 environment</title>
        </head><body>webview2 environment</body></html>
        """;

    internal static int Run(string[] args, DesktopHostOptions hostOptions)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP webview2 environment: Windows only.");
            return 0;
        }
        if (args.Contains(ChildArgument, StringComparer.Ordinal))
        {
            // Read before the host starts, which clears it outside Development.
            var port = int.Parse(Environment.GetEnvironmentVariable(Variable)!.Split('=')[^1], System.Globalization.CultureInfo.InvariantCulture);
            var development = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Development";
            return DesktopEventLoop.Run(hostOptions, host => RunChildAsync(host, development, port));
        }
        foreach (var environment in new[] { "Production", "Development" })
        {
            var start = ChildStart(args);
            start.Environment["DOTNET_ENVIRONMENT"] = environment;
            start.Environment.Remove("ASPNETCORE_ENVIRONMENT");
            start.Environment[Variable] = $"--remote-debugging-port={FreePort()}";
            using var child = Process.Start(start) ?? throw new InvalidOperationException("The WebView2 environment smoke could not start itself.");
            if (!child.WaitForExit(TimeSpan.FromSeconds(120)))
            {
                child.Kill(entireProcessTree: true);
                throw new TimeoutException($"WebView2 environment: the {environment} window did not finish.");
            }
            if (child.ExitCode != 0) throw new InvalidOperationException($"WebView2 environment: the {environment} window failed ({child.ExitCode}).");
        }
        Console.WriteLine("PASS webview2 environment: WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS reaches WebView2 in Development only.");
        return 0;
    }

    private static async Task<int> RunChildAsync(DesktopHost host, bool development, int port)
    {
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 480, Height = 320 });
        await surface.ExecuteJavaScriptAsync("return document.readyState;", Deadline);
        var environment = development ? "Development" : "Production";
        var value = Environment.GetEnvironmentVariable(Variable);
        Require(development == value is not null, $"{environment}: {Variable} is {value ?? "unset"}");
        string endpoint;
        if (development && Environment.IsPrivilegedProcess)
        {
            endpoint = "port not checked: WebView2 ignores the variable in an elevated process";
        }
        else if (development)
        {
            Require(await AcceptsAsync(port, TimeSpan.FromSeconds(10)), $"Development: no DevTools Protocol endpoint on port {port}");
            endpoint = "endpoint open";
        }
        else
        {
            // The browser process starts with the environment, before the page loads; a late listener gets two seconds.
            await Task.Delay(TimeSpan.FromSeconds(2));
            Require(!await AcceptsAsync(port, TimeSpan.Zero), $"Production: a DevTools Protocol endpoint opened on port {port}");
            endpoint = "no endpoint";
        }
        Console.WriteLine($"WebView2 environment: {environment}: variable {(development ? "kept" : "cleared")}, {endpoint}.");
        return 0;
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
        // As InspectorEnvironmentSmoke: under `dotnet <dll>` the child needs the smoke's assembly.
        if (Path.GetFileNameWithoutExtension(processPath) is "dotnet")
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(WebView2EnvironmentSmoke).Assembly.GetName().Name + ".dll"));
        foreach (var argument in args) start.ArgumentList.Add(argument);
        start.ArgumentList.Add(ChildArgument);
        return start;
    }

    // A port that no bind(0) or connection takes before WebView2's browser binds it (TestPorts).
    private static int FreePort() => TestPorts.Lease().Release();

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("WebView2 environment: " + what + ".");
    }
}
