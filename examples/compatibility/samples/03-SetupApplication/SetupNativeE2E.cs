using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;
using RunicToolkit.ApplicationBridge;
using RunicToolkit.Examples.Setup.Contract;
using RunicToolkit.Hosting.CsWebUi.ApplicationBridge;

namespace RunicToolkit.Examples.Setup;

internal static class SetupNativeE2E
{
    internal static async Task<int> RunAsync()
    {
        string? browserPath = Environment.GetEnvironmentVariable("WEBUI_BROWSER_PATH");
        if (string.IsNullOrWhiteSpace(browserPath) || !File.Exists(browserPath))
        {
            Console.Error.WriteLine("WEBUI_BROWSER_PATH must identify Chrome, Edge, or Chromium.");
            return 1;
        }

        string webRoot = Path.Combine(AppContext.BaseDirectory, "native-e2e");
        string profile = Path.Combine(Path.GetTempPath(), "runic-setup-e2e-" + Guid.NewGuid().ToString("N"));
        await using var session = new ApplicationBridgeSession(
            new SetupBridgeDispatcher(new SetupBridgeHandler()));
        using var window = new WebUiWindow();
        window.SetPublic(false);
        window.SetRootFolder(webRoot);
        await using CsWebUiApplicationBridge bridge = CsWebUiApplicationBridge.Attach(window, session);
        string url = window.StartServer("index.html");
        Directory.CreateDirectory(profile);
        using var browser = new Process
        {
            StartInfo =
            {
                FileName = browserPath,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (string argument in new[]
        {
            "--headless=new", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage",
            "--disable-background-networking", "--disable-component-update", "--disable-crash-reporter",
            "--no-first-run", "--remote-debugging-port=0", "--user-data-dir=" + profile, url,
        }) browser.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!browser.Start()) throw new InvalidOperationException("The browser did not start.");
            Task<string> diagnostics = browser.StandardError.ReadToEndAsync();
            string result = string.Empty;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    try
                    {
                        result = window.ExecuteJavaScript(
                            "return document.body.dataset.result + '|' + document.body.dataset.view + '|' + document.body.dataset.progress;",
                            TimeSpan.FromSeconds(1),
                            128);
                        if (result.StartsWith("pass|", StringComparison.Ordinal) ||
                            result.StartsWith("fail|", StringComparison.Ordinal) ||
                            result.StartsWith("error|", StringComparison.Ordinal)) break;
                    }
                    catch (WebUiException) { }
                    await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }

            bool passed = result == "pass|Complete|5";
            Console.WriteLine(passed
                ? "PASS: native browser completed the package-only Setup Application Bridge vertical."
                : $"FAIL: native Setup result was '{result}'.");
            if (!browser.HasExited) browser.Kill(true);
            await browser.WaitForExitAsync().ConfigureAwait(false);
            _ = await diagnostics.ConfigureAwait(false);
            return passed ? 0 : 1;
        }
        finally
        {
            WebUiApplication.Exit();
            WebUiApplication.Clean();
            if (!browser.HasExited)
            {
                browser.Kill(true);
                await browser.WaitForExitAsync().ConfigureAwait(false);
            }
            await TryDeleteProfileAsync(profile).ConfigureAwait(false);
        }
    }

    private static async Task TryDeleteProfileAsync(string profile)
    {
        for (int attempt = 0; Directory.Exists(profile) && attempt < 10; attempt++)
        {
            try { Directory.Delete(profile, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }
}
