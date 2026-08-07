using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CsWebUi;
using RunicToolkit.ApplicationBridge;
using RunicToolkit.Hosting.CsWebUi;
using RunicToolkit.Hosting.CsWebUi.ApplicationBridge;

namespace RunicToolkit.Hosting.CsWebUi.NativeE2E;

internal static class Program
{
    public static async Task<int> Main()
    {
        string? chromium = Environment.GetEnvironmentVariable("WEBUI_BROWSER_PATH");
        if (string.IsNullOrWhiteSpace(chromium) || !File.Exists(chromium))
        {
            Console.Error.WriteLine("FAIL: WEBUI_BROWSER_PATH does not name the pinned Chromium executable.");
            return 1;
        }

        string webRoot = Path.Combine(AppContext.BaseDirectory, "www");
        await using var session = new ApplicationBridgeSession(new NativeDispatcher());
        string browserProfile = Path.Combine(Path.GetTempPath(), "runic-toolkit-native-e2e-" + Guid.NewGuid().ToString("N"));
        WebUiWindow? window = null;
        CsWebUiApplicationBridge? bridge = null;
        WebUiBinding? desktopBinding = null;
        Process? browser = null;
        Task<string>? browserDiagnostics = null;
        bool browserStarted = false;
        List<Exception>? cleanupErrors = null;
        string diagnosticText = string.Empty;
        int exitCode = 1;

        try
        {
            window = new WebUiWindow();
            window.SetPublic(false);
            window.SetRootFolder(webRoot);
            bridge = CsWebUiApplicationBridge.Attach(window, session);
            var desktopMessages = Channel.CreateUnbounded<string>();
            desktopBinding = window.Bind("__runicToolkit_desktop_result", webUiEvent =>
            {
                if (webUiEvent.ArgumentCount == 1) desktopMessages.Writer.TryWrite(webUiEvent.GetString());
            });
            string url = window.StartServer("index.html");
            await WaitForServerAsync(new Uri(url), TimeSpan.FromSeconds(10));

            Directory.CreateDirectory(browserProfile);
            browser = new Process { StartInfo = { FileName = chromium, RedirectStandardError = true, UseShellExecute = false } };
            foreach (string argument in new[]
            {
                "--headless=new", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage",
                "--disable-background-networking", "--disable-component-update", "--no-first-run",
                "--remote-debugging-port=0", "--user-data-dir=" + browserProfile, url,
            }) browser.StartInfo.ArgumentList.Add(argument);
            browserStarted = browser.Start();
            if (!browserStarted) throw new InvalidOperationException("Chromium did not start.");
            browserDiagnostics = browser.StandardError.ReadToEndAsync();
            FileVersionInfo browserVersion = FileVersionInfo.GetVersionInfo(chromium);
            Console.WriteLine(
                $"Browser: {browserVersion.ProductName ?? Path.GetFileName(chromium)} " +
                $"{browserVersion.ProductVersion ?? "unknown"} ({chromium})");

            string result = string.Empty;
            WebUiException? lastScriptFailure = null;
            using var bridgeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                while (!bridgeTimeout.IsCancellationRequested)
                {
                    if (browser.HasExited)
                    {
                        throw new InvalidOperationException(
                            $"Chromium exited before the Application Bridge connected (exit code {browser.ExitCode}).");
                    }

                    try
                    {
                        result = window.ExecuteJavaScript(
                            "return document.body.dataset.result + '|' + document.querySelector('#count').textContent + '|' + (document.body.dataset.message || '');",
                            TimeSpan.FromSeconds(1), 512);
                        if (result.StartsWith("pass|", StringComparison.Ordinal) || result.StartsWith("fail|", StringComparison.Ordinal) || result.StartsWith("error|", StringComparison.Ordinal)) break;
                    }
                    catch (WebUiException exception)
                    {
                        lastScriptFailure = exception;
                    }
                    await Task.Delay(100, bridgeTimeout.Token);
                }
            }
            catch (OperationCanceledException) { }

            if (string.IsNullOrEmpty(result) && lastScriptFailure is not null)
            {
                Console.Error.WriteLine($"Last bridge probe error: {lastScriptFailure.Message}");
            }

            bool bridgePassed = result.StartsWith("pass|1|", StringComparison.Ordinal);
            bool desktopPassed = false;
            if (bridgePassed)
            {
                using var desktopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                window.RunJavaScript(CsWebUiDesktopBridgeScript.Bootstrap);
                window.RunJavaScript("""globalThis.__runicToolkitDesktop.invoke("native-write","storage.write",{"key":"native-e2e","value":"stored"});""");
                string write = await desktopMessages.Reader.ReadAsync(desktopTimeout.Token);
                window.RunJavaScript("""globalThis.__runicToolkitDesktop.invoke("native-read","storage.read",{"key":"native-e2e"});""");
                string read = await desktopMessages.Reader.ReadAsync(desktopTimeout.Token);
                desktopPassed = IsDesktopResult(write, "native-write", null) && IsDesktopResult(read, "native-read", "stored");
            }
            bool passed = bridgePassed && desktopPassed;
            Console.WriteLine(passed
                ? "PASS: real CsWebUi + Chromium exercised Application Bridge and desktop storage."
                : $"FAIL: native bridge result '{result}', desktop passed: {desktopPassed}, host identity: {bridge.ConnectionIdentity?.ToString() ?? "none"}.");
            exitCode = passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: native browser roundtrip threw {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            if (bridge is not null) try { await bridge.DisposeAsync(); } catch (Exception exception) { (cleanupErrors ??= []).Add(exception); }
            Capture(ref cleanupErrors, () => desktopBinding?.Dispose());
            if (window is not null)
            {
                try
                {
                    WebUiApplication.Exit();
                    using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await WebUiApplication.WaitAsync(shutdownTimeout.Token);
                }
                catch (Exception exception)
                {
                    (cleanupErrors ??= []).Add(exception);
                }
            }
            if (browserStarted && browser is not null) try { if (!browser.HasExited) { browser.Kill(true); await browser.WaitForExitAsync(); } } catch (Exception exception) { (cleanupErrors ??= []).Add(exception); }
            if (browserDiagnostics is not null) try { diagnosticText = await browserDiagnostics; } catch (Exception exception) { (cleanupErrors ??= []).Add(exception); }
            Capture(ref cleanupErrors, () => browser?.Dispose());
            Capture(ref cleanupErrors, () => window?.Dispose());
            if (window is not null) Capture(ref cleanupErrors, WebUiApplication.Clean);
            if (Directory.Exists(browserProfile))
            {
                try
                {
                    await DeleteBrowserProfileAsync(browserProfile).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Chrome crash helpers can briefly retain profile files on Windows.
                    // The hosted runner owns the temporary directory and will remove it.
                    Console.Error.WriteLine($"WARN: temporary browser profile cleanup was deferred: {exception.Message}");
                }
            }
        }

        if (exitCode != 0 && !string.IsNullOrWhiteSpace(diagnosticText))
        {
            const int maximumDiagnosticCharacters = 4000;
            string tail = diagnosticText.Length <= maximumDiagnosticCharacters
                ? diagnosticText
                : diagnosticText[^maximumDiagnosticCharacters..];
            Console.Error.WriteLine("Chromium stderr (tail):");
            Console.Error.WriteLine(tail.TrimEnd());
        }

        if (cleanupErrors is not null)
        {
            foreach (Exception error in cleanupErrors)
            {
                Console.Error.WriteLine(
                    $"WARN: native test teardown was deferred to the hosted runner: {error.GetType().Name}: {error.Message}");
            }
            exitCode = 1;
        }

        return exitCode;
    }

    private static async Task WaitForServerAsync(Uri uri, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        Exception? lastFailure = null;
        while (timer.Elapsed < timeout)
        {
            using var client = new TcpClient();
            using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await client.ConnectAsync(uri.Host, uri.Port, attempt.Token);
                return;
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                lastFailure = exception;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"The native WebUI server at '{uri}' did not accept connections within {timeout.TotalSeconds:F0} seconds.",
            lastFailure);
    }

    private static bool IsDesktopResult(string json, string id, string? expected)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.GetProperty("kind").GetString() != "result" || root.GetProperty("id").GetString() != id || !root.GetProperty("ok").GetBoolean()) return false;
        JsonElement value = root.GetProperty("value");
        return expected is null ? value.ValueKind == JsonValueKind.Null : value.GetString() == expected;
    }

    private static void Capture(ref List<Exception>? errors, Action cleanup)
    {
        try { cleanup(); } catch (Exception exception) { (errors ??= []).Add(exception); }
    }

    private static async Task DeleteBrowserProfileAsync(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (Exception exception) when (
                attempt < 9 && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }
}

internal sealed class NativeDispatcher : IApplicationBridgeDispatcher
{
    private int _count;
    public string ProtocolIdentity => "runic.artifex.native-e2e";
    public int ProtocolVersion => 1;
    public string ManifestFingerprint => new('a', 64);

    public ValueTask<BridgeDispatchResult> DispatchAsync(JsonElement command, BridgeCommandContext context, CancellationToken cancellationToken)
    {
        string tag = command.GetProperty("_tag").GetString()!;
        JsonElement receipt = tag switch
        {
            "InitializeApplication" => JsonDocument.Parse($"{{\"_tag\":\"ApplicationInitialized\",\"snapshot\":{{\"count\":{_count},\"revision\":{context.CurrentRevision}}}}}").RootElement.Clone(),
            "Increment" => JsonDocument.Parse($"{{\"_tag\":\"Incremented\",\"count\":{++_count},\"revision\":{context.CurrentRevision + 1}}}").RootElement.Clone(),
            _ => throw new JsonException("Unknown command."),
        };
        return ValueTask.FromResult(new BridgeDispatchResult(receipt, tag == "Increment"));
    }
}
