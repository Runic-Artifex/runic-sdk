using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CsWebUi;
using RunicToolkit.ApplicationBridge;
using RunicToolkit.Examples.Setup.Contract;
using RunicToolkit.Hosting.CsWebUi.ApplicationBridge;

namespace RunicToolkit.Examples.Setup;

internal static class SvelteKitNativeE2E
{
    internal static async Task<int> RunAsync()
    {
        string? browserPath = Environment.GetEnvironmentVariable("WEBUI_BROWSER_PATH");
        if (string.IsNullOrWhiteSpace(browserPath) || !File.Exists(browserPath))
        {
            Console.Error.WriteLine("WEBUI_BROWSER_PATH must identify Chrome, Edge, or Chromium.");
            return 1;
        }

        string webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        string profile = Path.Combine(
            Path.GetTempPath(),
            "runic-sveltekit-e2e-" + Guid.NewGuid().ToString("N"));
        await using var session = new ApplicationBridgeSession(
            new SetupBridgeDispatcher(new SetupBridgeHandler()));
        using var window = new WebUiWindow();
        window.SetPublic(false);
        window.SetRootFolder(webRoot);
        await using CsWebUiApplicationBridge bridge = CsWebUiApplicationBridge.Attach(window, session);
        var resultSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using WebUiBinding resultBinding = window.Bind(
            "__runicToolkit_sveltekit_e2e_result",
            webUiEvent => resultSource.TrySetResult(
                webUiEvent.ArgumentCount == 1 ? webUiEvent.GetString() : "error|binding|invalid result"));
        string url = window.StartServer("index.html");
        Directory.CreateDirectory(profile);
        using var browser = CreateBrowser(browserPath, profile, url + "#runic-e2e");

        try
        {
            if (!browser.Start()) throw new InvalidOperationException("The browser did not start.");
            Task<string> diagnostics = browser.StandardError.ReadToEndAsync();
            string result;
            try
            {
                result = await resultSource.Task.WaitAsync(TimeSpan.FromSeconds(35)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                try
                {
                    string browserState = window.ExecuteJavaScript(
                        "return `${location.href}|${document.querySelector('[data-e2e-view]')?.getAttribute('data-e2e-view') ?? 'unmounted'}|${document.querySelector('.status')?.textContent ?? 'no-status'}|result-binding:${typeof globalThis.__runicToolkit_sveltekit_e2e_result}|${globalThis.__runicBootError || ''}`;",
                        TimeSpan.FromSeconds(2),
                        1024);
                    result = "error|host-timeout|" + browserState;
                }
                catch (WebUiException exception)
                {
                    result = "error|host-timeout|diagnostics unavailable: " + exception.Message;
                }
            }

            bool passed = string.Equals(result, "pass|Complete|5", StringComparison.Ordinal);
            Console.WriteLine(passed
                ? "PASS: native browser completed the package-only SvelteKit vertical."
                : $"FAIL: native SvelteKit result was '{result}' (host identity: {bridge.ConnectionIdentity?.ToString() ?? "none"}).");
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

    private static Process CreateBrowser(string browserPath, string profile, string url)
    {
        var browser = new Process
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
        return browser;
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
