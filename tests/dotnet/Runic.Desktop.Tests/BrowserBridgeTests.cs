using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Runic.Desktop;

namespace Runic.Desktop.Tests;

public sealed class BrowserBridgeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string ApplicationBridgeCapability = "runic.desktop.application-bridge/1";
    private const string ApplicationBridgeReceiver = "__runicDesktopReceiveApplicationBridgeFrame";

    [Fact]
    public async Task UpstreamStyleCallbackAndJavaScriptRoundTripRunsInChromium()
    {
        var chrome = FindChrome();
        if (chrome is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var window = new WebUiWindow();
        window.Bind("exercise", e =>
        {
            var result = e.Window.ExecuteJavaScript("return 6 * 7;", TimeSpan.FromSeconds(5));
            e.SendRaw("acceptBytes", [1, 0, 2]);
            return result;
        });
        window.Bind("clicker", e =>
        {
            if (e.EventType == WebUiEventType.MouseClick)
            {
                e.RunJavaScript("globalThis.clickResult = 'clicked';");
            }
        });
        window.Bind("large", static e => (long)e.GetBytes().Length);

        var url = await window.StartServerAsync("""
            <!doctype html>
            <html><head><script src="webui.js"></script><title>WAIT</title></head>
            <body data-result="waiting"><button id="exercise">Exercise</button><button id="clicker">Click</button>
            <script>
              globalThis.acceptBytes = data => globalThis.rawResult = Array.from(data).join(',');
              (async () => {
                await webui.connected;
                const value = await exercise();
                const largeSize = await large('A'.repeat(150000));
                document.querySelector('#clicker').click();
                for (let index = 0; index < 100 &&
                     (globalThis.rawResult === undefined || globalThis.clickResult === undefined); index++) {
                  await new Promise(resolve => setTimeout(resolve, 10));
                }
                document.body.dataset.result = `${value}:${globalThis.rawResult}:${globalThis.clickResult}:${largeSize}`;
                document.title = 'PASS';
              })();
            </script></body></html>
            """, timeout.Token);

        var profile = Directory.CreateTempSubdirectory("runic-desktop-chrome-");
        var startInfo = new ProcessStartInfo(chrome)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--headless=new");
        startInfo.ArgumentList.Add("--no-sandbox");
        startInfo.ArgumentList.Add("--disable-gpu");
        startInfo.ArgumentList.Add("--disable-dev-shm-usage");
        startInfo.ArgumentList.Add("--remote-debugging-port=0");
        startInfo.ArgumentList.Add($"--user-data-dir={profile.FullName}");
        startInfo.ArgumentList.Add(url.AbsoluteUri);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Chromium.");
        Task<string> browserErrors = ReadBrowserErrorsAsync(process.StandardError);
        try
        {
            var debuggerPort = await ReadDebuggerPortAsync(profile.FullName, timeout.Token);
            var debuggerUrl = await FindPageDebuggerUrlAsync(debuggerPort, url, timeout.Token);
            using var devTools = new ClientWebSocket();
            await devTools.ConnectAsync(debuggerUrl, timeout.Token);

            string? result = null;
            for (var attempt = 0; attempt < 100 && result != "42:1,0,2:clicked:150000"; attempt++)
            {
                result = await EvaluateAsync(devTools, attempt + 1, "document.body?.dataset.result", timeout.Token);
                if (result != "42:1,0,2:clicked:150000")
                {
                    await Task.Delay(25, timeout.Token);
                }
            }

            var diagnostic = await EvaluateAsync(
                devTools,
                102,
                "JSON.stringify({webui:typeof webui,exercise:typeof exercise,connected:webui?.isConnected?.(),title:document.title})",
                timeout.Token);
            Assert.True(result == "42:1,0,2:clicked:150000", $"Actual result: {result}; browser state: {diagnostic}");
            Assert.Equal("PASS", await EvaluateAsync(devTools, 101, "document.title", timeout.Token));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            output.WriteLine(await browserErrors);
            await BrowserTestProfile.DeleteAsync(profile.FullName, TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task TypeScriptEffectPackageRoundTripsThroughPublicDesktopApiInChromium()
    {
        var chrome = FindChrome();
        var bundlePath = Path.Combine(AppContext.BaseDirectory, "runic-desktop-browser-test.js");
        if (chrome is null || !File.Exists(bundlePath))
        {
            return;
        }

        var bundle = await File.ReadAllBytesAsync(bundlePath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = """
                <!doctype html>
                <html><head><script src="runic-desktop.js"></script><title>WAIT</title></head>
                <body data-result="waiting"><script type="module" src="transport-test.js"></script></body></html>
                """,
            ContentHandler = (request, _) => ValueTask.FromResult<ContentResponse?>(
                request.Path == "/transport-test.js"
                    ? new ContentResponse(bundle, "text/javascript; charset=utf-8")
                    : null),
        });
        using var registration = surface.RegisterCapability(
            ApplicationBridgeCapability,
            static async (invocation, cancellationToken) =>
            {
                if (invocation.Kind == PresentationEventKind.Invocation && invocation.ArgumentCount == 1)
                {
                    await invocation.Session.SendAsync(
                        ApplicationBridgeReceiver,
                        invocation.GetBytes(),
                        cancellationToken);
                }
                return PresentationResult.None;
            });

        var profile = Directory.CreateTempSubdirectory("runic-desktop-effect-chrome-");
        using var process = StartChrome(chrome, profile.FullName, surface.Url);
        Task<string> browserErrors = ReadBrowserErrorsAsync(process.StandardError);
        try
        {
            var debuggerPort = await ReadDebuggerPortAsync(profile.FullName, timeout.Token);
            var debuggerUrl = await FindPageDebuggerUrlAsync(debuggerPort, surface.Url, timeout.Token);
            using var devTools = new ClientWebSocket();
            await devTools.ConnectAsync(debuggerUrl, timeout.Token);

            string? result = null;
            for (var attempt = 0; attempt < 100 && result != "1,0,2"; attempt++)
            {
                result = await EvaluateAsync(devTools, attempt + 1, "document.body?.dataset.result", timeout.Token);
                if (result != "1,0,2")
                {
                    await Task.Delay(25, timeout.Token);
                }
            }

            var diagnostic = await EvaluateAsync(
                devTools,
                102,
                "JSON.stringify({runicDesktop:globalThis.runicDesktop?.product,webui:typeof globalThis.webui,title:document.title})",
                timeout.Token);
            Assert.True(result == "1,0,2", $"Actual result: {result}; browser state: {diagnostic}");
            Assert.Equal("PASS", await EvaluateAsync(devTools, 101, "document.title", timeout.Token));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            output.WriteLine(await browserErrors);
            await BrowserTestProfile.DeleteAsync(profile.FullName, TimeSpan.FromSeconds(10));
        }
    }

    private static Process StartChrome(string chrome, string profile, Uri url)
    {
        var startInfo = new ProcessStartInfo(chrome)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--headless=new");
        startInfo.ArgumentList.Add("--no-sandbox");
        startInfo.ArgumentList.Add("--disable-gpu");
        startInfo.ArgumentList.Add("--disable-dev-shm-usage");
        startInfo.ArgumentList.Add("--remote-debugging-port=0");
        startInfo.ArgumentList.Add($"--user-data-dir={profile}");
        startInfo.ArgumentList.Add(url.AbsoluteUri);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Chromium.");
    }

    private static async Task<string> ReadBrowserErrorsAsync(StreamReader reader)
    {
        // Always drain the pipe so Chromium cannot block on diagnostic output.
        var retained = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            int take = Math.Min(count, 64 * 1024 - retained.Length);
            if (take > 0) retained.Append(buffer, 0, take);
        }
        return retained.ToString();
    }

    private static string? FindChrome()
    {
        if (Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH") is { Length: > 0 } configured)
        {
            if (!File.Exists(configured)) throw new FileNotFoundException("Configured Chromium executable is missing.", configured);
            return configured;
        }
        var names = OperatingSystem.IsWindows()
            ? new[] { "chrome.exe", "msedge.exe" }
            : new[] { "google-chrome", "chromium", "chromium-browser" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static async Task<int> ReadDebuggerPortAsync(string profile, CancellationToken cancellationToken)
    {
        var path = Path.Combine(profile, "DevToolsActivePort");
        while (!File.Exists(path))
        {
            await Task.Delay(25, cancellationToken);
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        return int.Parse(lines[0], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<Uri> FindPageDebuggerUrlAsync(
        int debuggerPort,
        Uri pageUrl,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{debuggerPort}") };
        while (true)
        {
            using var document = JsonDocument.Parse(await client.GetStringAsync("/json/list", cancellationToken));
            foreach (var target in document.RootElement.EnumerateArray())
            {
                if (target.GetProperty("url").GetString() == pageUrl.AbsoluteUri)
                {
                    return new Uri(target.GetProperty("webSocketDebuggerUrl").GetString()!, UriKind.Absolute);
                }
            }

            await Task.Delay(25, cancellationToken);
        }
    }

    private static async Task<string?> EvaluateAsync(
        ClientWebSocket socket,
        int id,
        string expression,
        CancellationToken cancellationToken)
    {
        var request = Encoding.UTF8.GetBytes(
            $"{{\"id\":{id},\"method\":\"Runtime.evaluate\",\"params\":{{\"expression\":{JsonSerializer.Serialize(expression)},\"returnByValue\":true}}}}");
        await socket.SendAsync(request, WebSocketMessageType.Text, true, cancellationToken);

        var buffer = new byte[16 * 1024];
        while (true)
        {
            var message = new MemoryStream();
            WebSocketReceiveResult response;
            do
            {
                response = await socket.ReceiveAsync(buffer, cancellationToken);
                message.Write(buffer, 0, response.Count);
            }
            while (!response.EndOfMessage);

            using var document = JsonDocument.Parse(message.ToArray());
            if (document.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
            {
                var result = document.RootElement.GetProperty("result").GetProperty("result");
                return result.TryGetProperty("value", out var value) ? value.GetString() : null;
            }
        }
    }
}
