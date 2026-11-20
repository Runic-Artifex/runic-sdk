using System.Text;
using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

namespace CsWebUiWindowProbes;

// sdk#1221, --probe-debug-endpoint [--fixed-port]: under dotnet runic dev --debug-endpoint (DOTNET_ENVIRONMENT=Development,
// RUNIC_WEBUI_REMOTE_DEBUGGING_PORT=0, or a free fixed port with --fixed-port), a CS-WebUI browser window's Chromium or
// Edge opens a DevTools Protocol endpoint on loopback, and the Window prints RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp
// <url>, which dev reads. Untrimmed, the probe also checks DEV-SEC-84/85: the browser runs in a new runic-cswebui-devtools-*
// profile, a second window joins that browser (its page is on the same endpoint, and it prints no second line), and once
// both windows closed the Window prints the closed line and the profile is gone. Uses the browser that
// CSWEBUI_BROWSER_PATH names, or WebUI's choice; a browser that is not Chromium-based reports itself skipped. A trimmed
// or NativeAOT publish, such as CI's, turns the endpoint off: there the window must print no line.
// Not part of the package smoke, which builds Program.cs alone.
internal static class DebugEndpointProbe
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(45);

    internal static int Run(bool fixedPort)
    {
        new Thread(static () =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(3));
            Console.Error.WriteLine("The debug endpoint probe did not finish within 3 minutes.");
            Environment.Exit(3);
        })
        { IsBackground = true }.Start();
        Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", null);
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
        var requested = 0;
        if (fixedPort)
        {
            using var free = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            free.Start();
            requested = ((System.Net.IPEndPoint)free.LocalEndpoint).Port;
        }
        Environment.SetEnvironmentVariable("RUNIC_WEBUI_REMOTE_DEBUGGING_PORT", requested.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var startedAt = DateTime.UtcNow - TimeSpan.FromSeconds(1);
        var root = Directory.CreateTempSubdirectory("runic-cswebui-debug-").FullName;
        File.WriteAllText(Path.Combine(root, "index.html"),
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Probe</title><script src=\"/webui.js\"></script></head><body>Probe</body></html>");
        var output = new LineCapture(Console.Out);
        Console.SetOut(output);
        var errors = new LineCapture(Console.Error);
        Console.SetError(errors);
        var services = new ServiceCollection();
        services.AddScoped<ProbeViewModel>();
        services.AddRunicViews();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        string? endpoint = null;
        var skipped = false;
        var trimmed = AppContext.TryGetSwitch("Runic.Desktop.WebUiRemoteDebugging", out var enabled) && !enabled;
        try
        {
            var result = RunicCsWebUiHost.Run(provider, async host =>
            {
                var window = await host.OpenWindowAsync<ProbeWindow>(new RunicWindowOptions { ContentRoot = root, Width = 640, Height = 480 });
                var native = CsWebUiPlatformServices.GetWebUiWindow(window) ?? throw new InvalidOperationException("The Window has no WebUI window.");
                if (native.BrowserProcessId == 0 && !OperatingSystem.IsMacOS())
                    Console.Error.WriteLine("WebUI reports no browser process for the window.");
                skipped = Environment.GetEnvironmentVariable(WebUiApplication.BrowserPathEnvironmentVariable) is null
                    && native.BestBrowser is not (WebUiBrowser.Chrome or WebUiBrowser.Edge or WebUiBrowser.Chromium or WebUiBrowser.Brave
                        or WebUiBrowser.Vivaldi or WebUiBrowser.Epic or WebUiBrowser.Yandex);
                if (trimmed && !skipped)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    if (output.Find("RUNIC_APPLICATION_DEBUG_ENDPOINT=") is { } line)
                        throw new InvalidOperationException($"A trimmed build opened a DevTools Protocol endpoint: {line}");
                    skipped = true;
                }
                var deadline = DateTime.UtcNow + Limit;
                while (!skipped && (endpoint = output.Find("RUNIC_APPLICATION_DEBUG_ENDPOINT=")) is null)
                {
                    if (DateTime.UtcNow > deadline)
                        throw new InvalidOperationException("The CS-WebUI window printed no RUNIC_APPLICATION_DEBUG_ENDPOINT line. Output:\n" + output);
                    await Task.Delay(100);
                }
                if (endpoint is null)
                {
                    await window.DisposeAsync();
                    return 7;
                }
                var url = endpoint["RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp ".Length..].Trim();
                if (!url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
                    throw new InvalidOperationException($"The endpoint is not on 127.0.0.1: {endpoint}");
                if (fixedPort && url != $"http://127.0.0.1:{requested}/")
                    throw new InvalidOperationException($"Port {requested} was asked for, but the endpoint is {url}.");
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                var version = await client.GetStringAsync(new Uri(new Uri(url), "json/version"));
                if (!version.Contains("\"webSocketDebuggerUrl\"", StringComparison.Ordinal))
                    throw new InvalidOperationException($"{url}json/version answered without a debugger URL: {version}");
                // DEV-SEC-85: a profile of this run's own, not WebUI's shared .WebUI one.
                var profile = new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories("runic-cswebui-devtools-*")
                    .Where(directory => directory.CreationTimeUtc >= startedAt && File.Exists(Path.Combine(directory.FullName, "Local State")))
                    .OrderByDescending(static directory => directory.CreationTimeUtc).FirstOrDefault()?.FullName
                    ?? throw new InvalidOperationException("The browser runs in no runic-cswebui-devtools-* profile of this run.");
                // A second window joins the browser: its page is on the first endpoint, and no second endpoint is reported.
                var second = await host.OpenWindowAsync<ProbeWindow>(new RunicWindowOptions { ContentRoot = root, Width = 480, Height = 360 });
                var pages = 0;
                while ((pages = CountPages(await client.GetStringAsync(new Uri(new Uri(url), "json/list")))) < 2)
                {
                    if (DateTime.UtcNow > deadline + Limit)
                        throw new InvalidOperationException($"The second window's page is not on {url} ({pages} page).");
                    await Task.Delay(100);
                }
                if (output.Count("RUNIC_APPLICATION_DEBUG_ENDPOINT=") != 1 || errors.ToString().Contains("warning: Runic:", StringComparison.Ordinal))
                    throw new InvalidOperationException($"The second window reported an endpoint or a warning of its own. Output:\n{output}\nErrors:\n{errors}");
                await second.DisposeAsync();
                await window.DisposeAsync();
                // The browser exits with its last window: the closed line, and the profile is deleted.
                var closedDeadline = DateTime.UtcNow + Limit;
                while (output.Find("RUNIC_APPLICATION_DEBUG_ENDPOINT_CLOSED=") is null || Directory.Exists(profile))
                {
                    if (DateTime.UtcNow > closedDeadline)
                        throw new InvalidOperationException($"After both windows closed: closed line {output.Find("RUNIC_APPLICATION_DEBUG_ENDPOINT_CLOSED=") ?? "missing"}, profile {(Directory.Exists(profile) ? "kept" : "deleted")}.");
                    await Task.Delay(100);
                }
                return 7;
            });
            if (result != 7) throw new InvalidOperationException($"Run returned {result} instead of the application's result.");
        }
        finally
        {
            Console.SetOut(output.Inner);
            Console.SetError(errors.Inner);
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
        Console.WriteLine(trimmed ? "CSWEBUI_DEBUG_ENDPOINT_TRIMMED_OK: no endpoint in a trimmed build"
            : skipped ? "CSWEBUI_DEBUG_ENDPOINT_SKIPPED: the browser is not Chromium-based" : $"CSWEBUI_DEBUG_ENDPOINT_OK {endpoint}");
        return 0;
    }

    private static int CountPages(string list) =>
        list.Split("\"type\": \"page\"", StringSplitOptions.None).Length - 1;

    // Standard output, passed on and kept, so the probe reads the Window's protocol lines.
    private sealed class LineCapture(TextWriter inner) : TextWriter
    {
        private readonly StringBuilder _text = new();

        internal TextWriter Inner => inner;

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            lock (_text) _text.Append(value);
            inner.Write(value);
        }

        public override void Write(string? value)
        {
            lock (_text) _text.Append(value);
            inner.Write(value);
        }

        public override void Flush() => inner.Flush();

        internal int Count(string prefix)
        {
            lock (_text)
                return _text.ToString().Split('\n').Count(line => line.StartsWith(prefix, StringComparison.Ordinal));
        }

        internal string? Find(string prefix)
        {
            lock (_text)
                return _text.ToString().Split('\n').Select(static line => line.TrimEnd('\r'))
                    .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
        }

        public override string ToString()
        {
            lock (_text) return _text.ToString();
        }
    }
}
