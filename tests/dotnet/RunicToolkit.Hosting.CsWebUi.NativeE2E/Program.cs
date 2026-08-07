using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

            string result = string.Empty;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    try
                    {
                        result = window.ExecuteJavaScript(
                            "return document.body.dataset.result + '|' + document.querySelector('#count').textContent;",
                            TimeSpan.FromSeconds(1), 128);
                        if (result.StartsWith("pass|", StringComparison.Ordinal) || result.StartsWith("fail|", StringComparison.Ordinal) || result.StartsWith("error|", StringComparison.Ordinal)) break;
                    }
                    catch (WebUiException) { }
                    await Task.Delay(100, timeout.Token);
                }
            }
            catch (OperationCanceledException) { }

            bool bridgePassed = result == "pass|1";
            bool desktopPassed = false;
            if (bridgePassed)
            {
                window.RunJavaScript(CsWebUiDesktopBridgeScript.Bootstrap);
                window.RunJavaScript("""globalThis.__runicToolkitDesktop.invoke("native-write","storage.write",{"key":"native-e2e","value":"stored"});""");
                string write = await desktopMessages.Reader.ReadAsync(timeout.Token);
                window.RunJavaScript("""globalThis.__runicToolkitDesktop.invoke("native-read","storage.read",{"key":"native-e2e"});""");
                string read = await desktopMessages.Reader.ReadAsync(timeout.Token);
                desktopPassed = IsDesktopResult(write, "native-write", null) && IsDesktopResult(read, "native-read", "stored");
            }
            bool passed = bridgePassed && desktopPassed;
            Console.WriteLine(passed
                ? "PASS: real CsWebUi + Chromium exercised Application Bridge and desktop storage."
                : "FAIL: native browser-to-C# Application Bridge or desktop roundtrip.");
            exitCode = passed ? 0 : 1;

            if (passed && OperatingSystem.IsWindows())
            {
                // WebUI's process-wide Windows shutdown can replace a completed test result while
                // its native callback threads are being torn down. Stop the child browser, publish
                // the authoritative result, and let the isolated test process own that boundary.
                try
                {
                    if (browserStarted && browser is not null && !browser.HasExited)
                    {
                        browser.Kill(true);
                        await browser.WaitForExitAsync();
                    }
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"WARN: Chromium cleanup was deferred to the hosted runner: {exception.Message}");
                }

                Console.Out.Flush();
                Console.Error.Flush();
                Environment.Exit(0);
            }
        }
        finally
        {
            if (browserStarted && browser is not null) try { if (!browser.HasExited) { browser.Kill(true); await browser.WaitForExitAsync(); } } catch (Exception exception) { (cleanupErrors ??= []).Add(exception); }
            if (browserDiagnostics is not null) try { _ = await browserDiagnostics; } catch (Exception exception) { (cleanupErrors ??= []).Add(exception); }
            Capture(ref cleanupErrors, () => browser?.Dispose());
            if (bridge is not null) try { await bridge.DisposeAsync(); } catch (Exception exception) { (cleanupErrors ??= []).Add(exception); }
            Capture(ref cleanupErrors, () => desktopBinding?.Dispose());
            Capture(ref cleanupErrors, () => window?.Dispose());
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

        if (cleanupErrors is not null)
        {
            foreach (Exception error in cleanupErrors)
            {
                Console.Error.WriteLine(
                    $"WARN: native test teardown was deferred to the hosted runner: {error.GetType().Name}: {error.Message}");
            }
        }

        // The isolated hosted runner owns any process or profile resources that native cleanup
        // could not release immediately. Preserve the observed functional result after teardown.
        return exitCode;
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
