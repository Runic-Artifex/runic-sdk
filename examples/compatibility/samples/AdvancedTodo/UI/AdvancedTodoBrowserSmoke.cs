using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;
using WebUIToolkit.MVVM.Html.Htmx.CsWebUi;

namespace WebUIToolkit.Samples.AdvancedTodo.UI;

/// <summary>Drives the generated AdvancedTodo registration through a real Chromium process.</summary>
internal static class AdvancedTodoBrowserSmoke
{
    private const string TaskTitle = "Verify the advanced native roundtrip";

    internal static Task<int> RunAsync(AdvancedTodoApplication root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return RunAsync(root.WebRoot, root.ConfigureWindow, root.DisposeAsync);
    }

    internal static Task<int> RunAsync(AdvancedTodoCsharpMarkupApplication root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return RunAsync(root.WebRoot, root.ConfigureWindow, root.DisposeAsync);
    }

    private static async Task<int> RunAsync(
        string webRoot,
        Action<WebUiWindow> configureWindow,
        Func<ValueTask> disposeApplication)
    {
        string? chromium = Environment.GetEnvironmentVariable("WEBUI_BROWSER_PATH");
        if (string.IsNullOrWhiteSpace(chromium) || !File.Exists(chromium))
        {
            Console.Error.WriteLine(
                "FAIL: WEBUI_BROWSER_PATH does not name the pinned Chromium executable.");
            return 1;
        }

        string browserProfile = Path.Combine(
            Path.GetTempPath(),
            "webuitoolkit-advanced-todo-browser-" + Guid.NewGuid().ToString("N"));
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
            window.SetRootFolder(webRoot);
            configureWindow(window);
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

            bool passed = result.StartsWith($"pass|{TaskTitle}|", StringComparison.Ordinal);
            Console.WriteLine(passed
                ? "PASS: real CsWebUi server + Chromium + generated AdvancedTodo HTMX registration updated the DOM."
                : "FAIL: native AdvancedTodo browser-to-C# HTMX roundtrip.");
            if (!passed)
            {
                Console.Error.WriteLine(result.Length == 0 ? "(no DOM result)" : result);
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
                await disposeApplication().ConfigureAwait(false);
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
            Console.Error.WriteLine("FAIL: native AdvancedTodo browser cleanup failed.");
            foreach (Exception error in cleanupErrors)
            {
                Console.Error.WriteLine(error.Message);
            }

            return 1;
        }

        return exitCode;
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
            const application = document.querySelector('[data-webui-view="advanced-todo"]');
            const fragment = document.querySelector("#advanced_fragment");
            const form = document.querySelector("#add-form");
            const titleInput = document.querySelector("#new-title");
            const notesInput = document.querySelector("#new-notes");
            const priorityInput = document.querySelector("#new-priority");
            if (!body || !application || !fragment || !form || !titleInput ||
                !notesInput || !priorityInput ||
                typeof globalThis.htmx !== "object" ||
                typeof globalThis.webuitoolkitHtmx !== "function" ||
                !globalThis.WebUIToolkitHtmxCsWebUi) {
              return "waiting|browser assets";
            }

            const revision = application.getAttribute("data-webui-revision") ?? "";
            if (body.dataset.advancedTodoBrowserSubmitted !== "true") {
              body.dataset.advancedTodoBrowserSubmitted = "true";
              body.dataset.advancedTodoInitialRevision = revision;
              for (const name of ["htmx:beforeRequest", "htmx:afterRequest",
                  "htmx:responseError", "htmx:sendError"]) {
                body.addEventListener(name, event => {
                  const status = event.detail?.xhr?.status ?? "";
                  const error = event.detail?.xhr?._lastError ?? "";
                  body.dataset.advancedTodoLastEvent = name + ":" + status + ":" + error;
                });
              }
              globalThis.addEventListener("error", event => {
                body.dataset.advancedTodoBrowserError = String(event.message ?? event.error);
              });
              globalThis.addEventListener("unhandledrejection", event => {
                body.dataset.advancedTodoBrowserError = String(event.reason);
              });
              fragment.dataset.advancedTodoOriginalFragment = "true";
              titleInput.value = "Verify the advanced native roundtrip";
              notesInput.value = "Generated registration exercised by Chromium";
              priorityInput.value = "High";
              titleInput.dispatchEvent(new Event("input", { bubbles: true }));
              notesInput.dispatchEvent(new Event("input", { bubbles: true }));
              priorityInput.dispatchEvent(new Event("change", { bubbles: true }));
              form.requestSubmit();
              return "submitted|" + revision;
            }

            const title = Array.from(document.querySelectorAll("#tasks .todo-title"))
              .map(element => element.textContent?.trim() ?? "")
              .find(value => value === "Verify the advanced native roundtrip") ?? "";
            const current = document.querySelector('[data-webui-view="advanced-todo"]');
            const currentRevision =
              current?.getAttribute("data-webui-revision") ?? "";
            const fragmentWasReplaced =
              document.querySelector("#advanced_fragment")
                ?.dataset.advancedTodoOriginalFragment !== "true";
            const initialRevision = body.dataset.advancedTodoInitialRevision ?? "";
            if (title && fragmentWasReplaced &&
                currentRevision && currentRevision !== initialRevision) {
              return "pass|" + title + "|" + initialRevision + "->" + currentRevision;
            }

            return "waiting|" + title + "|" + initialRevision + "->" + currentRevision +
              "|" + (body.dataset.advancedTodoLastEvent ?? "no-event") +
              "|" + (body.dataset.advancedTodoBrowserError ?? "no-error");
          } catch (error) {
            return "error|" + String(error);
          }
        })();
        """;
}
