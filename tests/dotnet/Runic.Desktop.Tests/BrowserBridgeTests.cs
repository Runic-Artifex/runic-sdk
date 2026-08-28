using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Runic.Desktop;

namespace Runic.Desktop.Tests;

public sealed class BrowserBridgeTests
{
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
            profile.Delete(recursive: true);
        }
    }

    private static string? FindChrome()
    {
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
