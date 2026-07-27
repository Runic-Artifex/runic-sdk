using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;
using WebUIToolkit.MVVM.Html.Htmx.CsWebUi;

namespace WebUIToolkit.Samples.SimpleTodo;

/// <summary>Drives the production native transport through a real pinned Chromium process.</summary>
internal static class TodoBrowserSmoke
{
    private const string TaskTitle = "Verify the native browser roundtrip";

    internal static async Task<int> RunAsync(TodoApplicationRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        string? chromium = Environment.GetEnvironmentVariable("WEBUI_BROWSER_PATH");
        if (string.IsNullOrWhiteSpace(chromium) || !File.Exists(chromium))
        {
            Console.Error.WriteLine(
                "FAIL: WEBUI_BROWSER_PATH does not name the pinned Chromium executable.");
            return 1;
        }

        string browserProfile = Path.Combine(
            Path.GetTempPath(),
            "webuitoolkit-simple-todo-browser-" + Guid.NewGuid().ToString("N"));
        WebUiWindow? window = null;
        Process? browser = null;
        Task<string>? browserDiagnostics = null;
        bool serverStarted = false;
        bool browserStarted = false;
        int exitCode = 1;
        List<Exception>? cleanupErrors = null;

        try
        {
            window = new WebUiWindow();
            window.SetPublic(false);
            window.SetRootFolder(root.WebRoot);
            root.ConfigureWindow(window);
            string url = window.StartServer("index.html");
            serverStarted = true;

            Directory.CreateDirectory(browserProfile);
            browser = new Process
            {
                StartInfo =
                {
                    FileName = chromium,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            browser.StartInfo.ArgumentList.Add("--headless=new");
            browser.StartInfo.ArgumentList.Add("--no-sandbox");
            browser.StartInfo.ArgumentList.Add("--disable-gpu");
            browser.StartInfo.ArgumentList.Add("--disable-dev-shm-usage");
            browser.StartInfo.ArgumentList.Add("--disable-background-networking");
            browser.StartInfo.ArgumentList.Add("--disable-component-update");
            browser.StartInfo.ArgumentList.Add("--no-first-run");
            browser.StartInfo.ArgumentList.Add("--remote-debugging-port=0");
            browser.StartInfo.ArgumentList.Add("--user-data-dir=" + browserProfile);
            browser.StartInfo.ArgumentList.Add(url);

            browserStarted = browser.Start();
            if (!browserStarted)
            {
                throw new InvalidOperationException("Chromium did not start.");
            }

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
                            BrowserRoundtripScript,
                            TimeSpan.FromSeconds(1),
                            responseBufferSize: 2048);
                        if (result.StartsWith("pass|", StringComparison.Ordinal) ||
                            result.StartsWith("fail|", StringComparison.Ordinal) ||
                            result.StartsWith("error|", StringComparison.Ordinal))
                        {
                            break;
                        }
                    }
                    catch (WebUiException)
                    {
                        // Chromium has not completed its native connection yet.
                    }

                    if (browser.HasExited)
                    {
                        result = "error|Chromium exited before the native roundtrip completed.";
                        break;
                    }

                    await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }

            bool passed = result.StartsWith(
                $"pass|{TaskTitle}|",
                StringComparison.Ordinal);
            Console.WriteLine(passed
                ? "PASS: real CsWebUi server + Chromium + native HTMX transport updated the compiled todo DOM."
                : "FAIL: native SimpleTodo browser-to-C# HTMX roundtrip.");
            if (!passed)
            {
                Console.Error.WriteLine(result.Length == 0 ? "(no DOM result)" : result);
            }
            else if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                FrontendDevelopmentAssets.ViteServerEnvironmentVariable)))
            {
                passed = await VerifyViteHmrAsync(window, browser).ConfigureAwait(false);
            }

            exitCode = passed ? 0 : 1;
        }
        finally
        {
            if (serverStarted)
            {
                CaptureCleanupError(ref cleanupErrors, WebUiApplication.Exit);
            }

            try
            {
                await root.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (cleanupErrors ??= []).Add(exception);
            }

            if (window is not null)
            {
                CaptureCleanupError(ref cleanupErrors, window.Dispose);
                CaptureCleanupError(ref cleanupErrors, WebUiApplication.Clean);
            }

            if (browserStarted && browser is not null)
            {
                try
                {
                    if (!browser.HasExited)
                    {
                        browser.Kill(entireProcessTree: true);
                        await browser.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    (cleanupErrors ??= []).Add(exception);
                }
            }

            if (browserDiagnostics is not null)
            {
                try
                {
                    string diagnostics = await browserDiagnostics.ConfigureAwait(false);
                    if (exitCode != 0 && diagnostics.Length != 0)
                    {
                        Console.Error.WriteLine(diagnostics);
                    }
                }
                catch (Exception exception)
                {
                    (cleanupErrors ??= []).Add(exception);
                }
            }

            if (browser is not null)
            {
                CaptureCleanupError(ref cleanupErrors, browser.Dispose);
            }

            if (Directory.Exists(browserProfile))
            {
                CaptureCleanupError(
                    ref cleanupErrors,
                    () => Directory.Delete(browserProfile, recursive: true));
            }
        }

        if (cleanupErrors is not null)
        {
            Console.Error.WriteLine("FAIL: native SimpleTodo browser cleanup failed.");
            foreach (Exception error in cleanupErrors)
            {
                Console.Error.WriteLine(error.Message);
            }

            return 1;
        }

        return exitCode;
    }

    private static async Task<bool> VerifyViteHmrAsync(
        WebUiWindow window,
        Process browser)
    {
        string packageDirectory = Environment.GetEnvironmentVariable(
            FrontendDevelopmentAssets.VitePackageDirectoryEnvironmentVariable)
            ?? throw new InvalidOperationException(
                "The supervised Vite package directory was not supplied.");
        string packageRoot = Path.GetFullPath(packageDirectory);
        string sourceRoot = Path.Combine(packageRoot, "src");
        string scriptPath = Path.GetFullPath(Path.Combine(sourceRoot, "hmr-probe.js"));
        string stylesheetPath = Path.GetFullPath(Path.Combine(sourceRoot, "hmr-probe.css"));
        string requiredPrefix = packageRoot.EndsWith(Path.DirectorySeparatorChar)
            ? packageRoot
            : packageRoot + Path.DirectorySeparatorChar;
        if (!scriptPath.StartsWith(requiredPrefix, StringComparison.Ordinal)
            || !stylesheetPath.StartsWith(requiredPrefix, StringComparison.Ordinal)
            || !File.Exists(scriptPath)
            || !File.Exists(stylesheetPath))
        {
            throw new InvalidOperationException(
                "The Vite HMR probe files are not anchored below the configured package.");
        }

        string originalScript = await File.ReadAllTextAsync(scriptPath).ConfigureAwait(false);
        string originalStylesheet = await File
            .ReadAllTextAsync(stylesheetPath)
            .ConfigureAwait(false);
        string token = "updated-" + Guid.NewGuid().ToString("N");
        string updatedScript = originalScript.Replace(
            "\"baseline\"",
            $"\"{token}\"",
            StringComparison.Ordinal);
        string updatedStylesheet = originalStylesheet.Replace(
            "\"baseline\"",
            $"\"{token}\"",
            StringComparison.Ordinal);
        if (StringComparer.Ordinal.Equals(originalScript, updatedScript)
            || StringComparer.Ordinal.Equals(originalStylesheet, updatedStylesheet))
        {
            throw new InvalidOperationException("The Vite HMR probe baseline is invalid.");
        }

        try
        {
            string prepared = window.ExecuteJavaScript(
                $$"""
                document.body.dataset.webuitoolkitHmrDocument = "{{token}}";
                return document.documentElement.dataset.webuitoolkitHmrProbe ?? "";
                """,
                TimeSpan.FromSeconds(2),
                responseBufferSize: 512);
            if (!StringComparer.Ordinal.Equals(prepared, "baseline"))
            {
                Console.Error.WriteLine(
                    $"FAIL: the Vite HMR probe did not initialize: '{prepared}'.");
                return false;
            }

            await File.WriteAllTextAsync(stylesheetPath, updatedStylesheet)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(scriptPath, updatedScript)
                .ConfigureAwait(false);

            string result = string.Empty;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    result = window.ExecuteJavaScript(
                        $$"""
                        return (() => {
                          const title = Array.from(document.querySelectorAll("#tasks .task-title"))
                            .some(element =>
                              element.textContent?.trim() === "{{TaskTitle}}");
                          const sameDocument =
                            document.body.dataset.webuitoolkitHmrDocument === "{{token}}";
                          const javascript =
                            document.documentElement.dataset.webuitoolkitHmrProbe === "{{token}}";
                          const css = getComputedStyle(document.documentElement)
                            .getPropertyValue("--webuitoolkit-hmr-probe")
                            .includes("{{token}}");
                          return title && sameDocument && javascript && css
                            ? "pass|{{token}}"
                            : "waiting|" + [title, sameDocument, javascript, css].join("|");
                        })();
                        """,
                        TimeSpan.FromSeconds(1),
                        responseBufferSize: 512);
                    if (result.StartsWith("pass|", StringComparison.Ordinal))
                    {
                        break;
                    }

                    if (browser.HasExited)
                    {
                        result = "error|Chromium exited during Vite HMR.";
                        break;
                    }

                    await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }

            bool passed = StringComparer.Ordinal.Equals(result, $"pass|{token}");
            Console.WriteLine(passed
                ? "PASS: Vite applied CSS and JavaScript HMR in the native window without " +
                    "restarting .NET, reloading the document, losing ViewModel state, or " +
                    "rerouting the private HTMX binding."
                : "FAIL: native-window Vite CSS/JavaScript HMR.");
            if (!passed)
            {
                Console.Error.WriteLine(result.Length == 0 ? "(no HMR result)" : result);
            }

            return passed;
        }
        finally
        {
            await File.WriteAllTextAsync(scriptPath, originalScript).ConfigureAwait(false);
            await File.WriteAllTextAsync(stylesheetPath, originalStylesheet).ConfigureAwait(false);
        }
    }

    private static void CaptureCleanupError(
        ref List<Exception>? errors,
        Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            (errors ??= []).Add(exception);
        }
    }

    private const string BrowserRoundtripScript =
        """
        return (() => {
          try {
            const body = document.body;
            const application = document.querySelector("#todo-app");
            const fragment = document.querySelector("#todo_fragment");
            const composer = document.querySelector("#composer");
            const input = document.querySelector("#new-title");
            if (!body || !application || !fragment || !composer || !input ||
                typeof globalThis.htmx !== "object" ||
                typeof globalThis.webuitoolkitHtmx !== "function" ||
                !globalThis.WebUIToolkitHtmxCsWebUi) {
              return "waiting|browser assets";
            }

            const revision = application.getAttribute("data-webui-revision") ?? "";
            if (body.dataset.simpleTodoBrowserSubmitted !== "true") {
              body.dataset.simpleTodoBrowserSubmitted = "true";
              body.dataset.simpleTodoInitialRevision = revision;
              fragment.dataset.simpleTodoOriginalFragment = "true";
              input.value = "Verify the native browser roundtrip";
              input.dispatchEvent(new Event("input", { bubbles: true }));
              composer.requestSubmit();
              return "submitted|" + revision;
            }

            const title = Array.from(document.querySelectorAll("#tasks .task-title"))
              .map(element => element.textContent?.trim() ?? "")
              .find(value => value === "Verify the native browser roundtrip") ?? "";
            const current = document.querySelector("#todo-app");
            const currentRevision =
              current?.getAttribute("data-webui-revision") ?? "";
            const fragmentWasReplaced =
              document.querySelector("#todo_fragment")
                ?.dataset.simpleTodoOriginalFragment !== "true";
            const initialRevision = body.dataset.simpleTodoInitialRevision ?? "";
            if (title && fragmentWasReplaced &&
                currentRevision && currentRevision !== initialRevision) {
              return "pass|" + title + "|" + initialRevision + "->" + currentRevision;
            }

            return "waiting|" + title + "|" + initialRevision + "->" + currentRevision;
          } catch (error) {
            return "error|" + String(error);
          }
        })();
        """;
}
