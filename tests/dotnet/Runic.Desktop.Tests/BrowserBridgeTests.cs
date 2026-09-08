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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpstreamStyleCallbackAndJavaScriptRoundTripRunsInChromium(bool delayedPageLoad)
    {
        var chrome = FindChrome();
        if (chrome is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var window = new WebUiWindow();
        var pageObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.SetFileHandler(async (path, cancellationToken) =>
        {
            if (path != "/startup.js") return null;
            if (delayedPageLoad)
            {
                // Hold the parser before <body> until CDP has observed the loading page.
                // The former 100 x 25 ms result poll expired during this valid navigation.
                await pageObserved.Task.WaitAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
            return new WebUiContent(ReadOnlyMemory<byte>.Empty, "text/javascript; charset=utf-8");
        });
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
            <html><head><script src="startup.js"></script><script src="webui.js"></script><title>WAIT</title></head>
            <body data-result="waiting"><button id="exercise">Exercise</button><button id="clicker">Click</button>
            <script>
              globalThis.acceptBytes = data => globalThis.rawResult = Array.from(data).join(',');
              (async () => {
                await webui.connected;
                const value = await exercise();
                const largeSize = await large('A'.repeat(150000));
                document.querySelector('#clicker').click();
                while (globalThis.rawResult === undefined || globalThis.clickResult === undefined) {
                  await new Promise(resolve => setTimeout(resolve, 10));
                }
                document.body.dataset.result = `${value}:${globalThis.rawResult}:${globalThis.clickResult}:${largeSize}`;
                document.title = 'PASS';
              })().catch(error => {
                globalThis.testError = String(error?.stack ?? error);
                document.title = 'FAIL';
              });
            </script></body></html>
            """, timeout.Token);

        var profile = Directory.CreateTempSubdirectory("runic-desktop-chrome-");
        using var process = StartChrome(chrome, profile.FullName, url);
        using var browserOutputLifetime = new CancellationTokenSource();
        Task<string> browserErrors = ReadBrowserErrorsAsync(process.StandardError, browserOutputLifetime.Token);
        try
        {
            output.WriteLine($"Chromium process {process.Id}: waiting for DevTools.");
            var debuggerPort = await ReadDebuggerPortAsync(profile.FullName, timeout.Token);
            var debuggerUrl = await FindPageDebuggerUrlAsync(debuggerPort, url, timeout.Token);
            output.WriteLine("Chromium page discovered; connecting and exercising the bridge.");
            using var devTools = new ClientWebSocket();
            await devTools.ConnectAsync(debuggerUrl, timeout.Token);

            await WaitForBridgeResultAsync(devTools, "42:1,0,2:clicked:150000", timeout.Token, pageObserved);

            if (delayedPageLoad)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    EvaluateAsync(devTools, int.MaxValue, "throw new Error('diagnostic probe')", timeout.Token));
                Assert.Contains("diagnostic probe", error.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            output.WriteLine("Stopping Chromium.");
            try { await StopChromeAsync(process, profile.FullName); }
            finally
            {
                await browserOutputLifetime.CancelAsync();
                output.WriteLine(await browserErrors.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            output.WriteLine("Chromium stopped; removing its profile.");
            await BrowserTestProfile.DeleteAsync(profile.FullName, TimeSpan.FromSeconds(10));
            output.WriteLine("Browser cleanup complete; disposing the application host.");
        }
    }

    [Fact]
    public async Task TypeScriptEffectPackageRoundTripsThroughPublicDesktopApiInChromium()
    {
        var chrome = FindChrome();
        var bundlePath = Path.Combine(AppContext.BaseDirectory, "runic-desktop-browser-test.js");
        if (Environment.GetEnvironmentVariable("CI") is "true")
            Assert.True(File.Exists(bundlePath), "Build the TypeScript browser fixture before running Desktop conformance in CI.");
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
        using var browserOutputLifetime = new CancellationTokenSource();
        Task<string> browserErrors = ReadBrowserErrorsAsync(process.StandardError, browserOutputLifetime.Token);
        try
        {
            output.WriteLine($"Chromium process {process.Id}: waiting for DevTools.");
            var debuggerPort = await ReadDebuggerPortAsync(profile.FullName, timeout.Token);
            var debuggerUrl = await FindPageDebuggerUrlAsync(debuggerPort, surface.Url, timeout.Token);
            output.WriteLine("Chromium page discovered; connecting and exercising the bridge.");
            using var devTools = new ClientWebSocket();
            await devTools.ConnectAsync(debuggerUrl, timeout.Token);

            await WaitForBridgeResultAsync(devTools, "1,0,2", timeout.Token);
        }
        finally
        {
            output.WriteLine("Stopping Chromium.");
            try { await StopChromeAsync(process, profile.FullName); }
            finally
            {
                await browserOutputLifetime.CancelAsync();
                output.WriteLine(await browserErrors.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            output.WriteLine("Chromium stopped; removing its profile.");
            await BrowserTestProfile.DeleteAsync(profile.FullName, TimeSpan.FromSeconds(10));
            output.WriteLine("Browser cleanup complete; disposing the application host.");
        }
    }

    private static async Task WaitForBridgeResultAsync(
        ClientWebSocket socket,
        string expected,
        CancellationToken cancellationToken,
        TaskCompletionSource? pageObserved = null)
    {
        const string expression = """
            JSON.stringify({
              url: location.href, readyState: document.readyState, title: document.title,
              result: document.body?.dataset.result,
              error: globalThis.testError,
              webui: typeof globalThis.webui, exercise: typeof globalThis.exercise,
              connected: globalThis.webui?.isConnected?.(),
              runicDesktop: globalThis.runicDesktop?.product,
              rawResult: globalThis.rawResult, clickResult: globalThis.clickResult
            })
            """;
        string? state = null;
        try
        {
            // Discovering a page target does not mean its document or bridge is ready.
            // Use the test's deadline, preserving the last observed state on timeout.
            for (var id = 1; ; id++)
            {
                state = await EvaluateAsync(socket, id, expression, cancellationToken);
                pageObserved?.TrySetResult();
                using var document = JsonDocument.Parse(state ?? throw new InvalidOperationException("Chromium returned no page state."));
                var root = document.RootElement;
                var title = root.GetProperty("title").GetString();
                var result = root.TryGetProperty("result", out var value) ? value.GetString() : null;
                Assert.True(title != "FAIL" && !root.TryGetProperty("error", out _), $"Bridge fixture failed; browser state: {state}");
                if (title == "PASS")
                {
                    Assert.True(result == expected, $"Expected result: {expected}; browser state: {state}");
                    return;
                }
                await Task.Delay(25, cancellationToken);
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Bridge did not complete before the test deadline. Expected result: {expected}; last browser state: {state}", exception);
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

    private async Task StopChromeAsync(Process process, string profile)
    {
        if (process.HasExited) return;
        try
        {
            // Ask the browser to close its own renderer/utility processes first.
            // Killing the process tree while Chromium is spawning children is
            // platform-dependent and can prevent test cleanup from returning.
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var lines = await File.ReadAllLinesAsync(Path.Combine(profile, "DevToolsActivePort"), shutdown.Token);
            if (lines.Length < 2) throw new IOException("Chromium has not published its browser endpoint.");
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{lines[0]}{lines[1]}"), shutdown.Token);
            await socket.SendAsync("{\"id\":1,\"method\":\"Browser.close\"}"u8.ToArray(),
                WebSocketMessageType.Text, true, shutdown.Token);
            await process.WaitForExitAsync(shutdown.Token);
            return;
        }
        catch (Exception error) when (error is IOException or WebSocketException or OperationCanceledException)
        {
            output.WriteLine($"Chromium graceful shutdown failed: {error.Message}");
        }

        if (!process.HasExited)
        {
            // Avoid synchronous process-tree enumeration on macOS. Chromium's
            // children also monitor the browser process for parent termination.
            process.Kill(entireProcessTree: !OperatingSystem.IsMacOS());
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<string> ReadBrowserErrorsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        // Always drain the pipe so Chromium cannot block on diagnostic output.
        var retained = new StringBuilder();
        var buffer = new char[4096];
        int count;
        try
        {
            while ((count = await reader.ReadAsync(buffer, cancellationToken)) != 0)
            {
                int take = Math.Min(count, 64 * 1024 - retained.Length);
                if (take > 0) retained.Append(buffer, 0, take);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
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

    internal static async Task<int> ReadDebuggerPortAsync(string profile, CancellationToken cancellationToken)
    {
        var path = Path.Combine(profile, "DevToolsActivePort");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Creation is not publication: Chromium can still hold a Windows
                // sharing lock or be writing the port. A newline completes the port
                // record; EOF alone could be a valid-looking prefix of that number.
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                using var reader = new StreamReader(file);
                var content = await reader.ReadToEndAsync(cancellationToken);
                var newline = content.IndexOf('\n');
                if (newline >= 0 && int.TryParse(content.AsSpan(0, newline).TrimEnd('\r'),
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                    out var port) && port is > 0 and <= 65535)
                {
                    return port;
                }
            }
            catch (FileNotFoundException) { }
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) is 32 or 33)
            {
                // Retry only sharing/lock violations; other I/O failures remain errors.
            }
            await Task.Delay(25, cancellationToken);
        }
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
            using var message = new MemoryStream();
            WebSocketReceiveResult response;
            do
            {
                response = await socket.ReceiveAsync(buffer, cancellationToken);
                if (response.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException($"Chromium closed DevTools while evaluating {expression}: {socket.CloseStatusDescription}");
                message.Write(buffer, 0, response.Count);
            }
            while (!response.EndOfMessage);

            using var document = JsonDocument.Parse(message.ToArray());
            if (document.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
            {
                if (document.RootElement.TryGetProperty("error", out var protocolError))
                    throw new InvalidOperationException($"Chromium DevTools failed evaluating {expression}: {protocolError}");
                var evaluation = document.RootElement.GetProperty("result");
                if (evaluation.TryGetProperty("exceptionDetails", out var exception))
                    throw new InvalidOperationException($"JavaScript failed evaluating {expression}: {exception}");
                var result = evaluation.GetProperty("result");
                return result.TryGetProperty("value", out var value) ? value.GetString() : null;
            }
        }
    }
}
