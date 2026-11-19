using System.Text;
using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

namespace CsWebUiWindowProbes;

// sdk#1221, --probe-debug-endpoint: under dotnet runic dev --debug-endpoint (DOTNET_ENVIRONMENT=Development,
// RUNIC_WEBUI_REMOTE_DEBUGGING_PORT=0), a CS-WebUI browser window's Chromium or Edge opens a DevTools Protocol endpoint
// on loopback, and the Window prints RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp <url>, which dev reads. Uses the
// browser that CSWEBUI_BROWSER_PATH names, or WebUI's choice; a browser that is not Chromium-based reports itself
// skipped. A trimmed or NativeAOT publish, such as CI's, turns the endpoint off: there the window must print no line.
// Not part of the package smoke, which builds Program.cs alone.
internal static class DebugEndpointProbe
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(45);

    internal static int Run()
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
        Environment.SetEnvironmentVariable("RUNIC_WEBUI_REMOTE_DEBUGGING_PORT", "0");
        var root = Directory.CreateTempSubdirectory("runic-cswebui-debug-").FullName;
        File.WriteAllText(Path.Combine(root, "index.html"),
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Probe</title><script src=\"/webui.js\"></script></head><body>Probe</body></html>");
        var output = new LineCapture(Console.Out);
        Console.SetOut(output);
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
                if (endpoint is not null)
                {
                    var url = endpoint["RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp ".Length..].Trim();
                    if (!url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
                        throw new InvalidOperationException($"The endpoint is not on 127.0.0.1: {endpoint}");
                    using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                    var version = await client.GetStringAsync(new Uri(new Uri(url), "json/version"));
                    if (!version.Contains("\"webSocketDebuggerUrl\"", StringComparison.Ordinal))
                        throw new InvalidOperationException($"{url}json/version answered without a debugger URL: {version}");
                }
                await window.DisposeAsync();
                return 7;
            });
            if (result != 7) throw new InvalidOperationException($"Run returned {result} instead of the application's result.");
        }
        finally
        {
            Console.SetOut(output.Inner);
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
        Console.WriteLine(trimmed ? "CSWEBUI_DEBUG_ENDPOINT_TRIMMED_OK: no endpoint in a trimmed build"
            : skipped ? "CSWEBUI_DEBUG_ENDPOINT_SKIPPED: the browser is not Chromium-based" : $"CSWEBUI_DEBUG_ENDPOINT_OK {endpoint}");
        return 0;
    }

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
