using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;
using WebUIToolkit.Hosting.WebUi;

namespace WebUIToolkit.Samples.Todo.FrontendHost;

internal static class TodoFrontendBrowserSmoke
{
    internal static async Task<int> RunAsync(
        TodoFrontendRoot root,
        string webRoot,
        string entryPoint,
        string frontend,
        TodoDemo demo)
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
            $"webuitoolkit-{frontend.ToLowerInvariant()}-{demo.ToString().ToLowerInvariant()}-" +
            Guid.NewGuid().ToString("N"));
        string taskTitle = $"Verify {frontend} {demo} {Guid.NewGuid():N}";
        WebUiWindow? window = null;
        IRootSession? session = null;
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
            root.ConfigureWindow(window);
            session = await root.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            await session.ActivateAsync(CancellationToken.None).ConfigureAwait(false);
            string url = window.StartServer(entryPoint);
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
            string script = CreateRoundtripScript(taskTitle, demo);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    try
                    {
                        result = window.ExecuteJavaScript(
                            script,
                            TimeSpan.FromSeconds(1),
                            responseBufferSize: 4096);
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
                        result = "error|Chromium exited before the framework roundtrip completed.";
                        break;
                    }

                    await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }

            bool passed = result.StartsWith($"pass|{taskTitle}|", StringComparison.Ordinal);
            Console.WriteLine(passed
                ? $"PASS: {frontend} {demo} completed a real CsWebUi browser-to-C# MVVM round trip."
                : $"FAIL: {frontend} {demo} native browser round trip.");
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

            if (session is not null)
            {
                try
                {
                    await session.DeactivateAsync(CancellationToken.None).ConfigureAwait(false);
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (cleanupErrors ??= []).Add(exception);
                }
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
            Console.Error.WriteLine($"FAIL: {frontend} {demo} browser cleanup failed.");
            foreach (Exception error in cleanupErrors)
            {
                Console.Error.WriteLine(error.Message);
            }

            return 1;
        }

        return exitCode;
    }

    private static string CreateRoundtripScript(string taskTitle, TodoDemo demo)
    {
        string expected = JsonSerializer.Serialize(taskTitle);
        string selector = demo == TodoDemo.Simple
            ? "\"#new-title\""
            : "\"input[placeholder='Task title']\"";
        return $$"""
            return (() => {
              try {
                const expected = {{expected}};
                const body = document.body;
                const input = document.querySelector({{selector}});
                const form = input?.closest("form");
                const status = Array.from(document.querySelectorAll(".badge"))
                  .some(element => element.textContent?.includes("Connected"));
                const startupFailure = document.querySelector(".alert-danger")?.textContent?.trim();
                if (startupFailure) {
                  return "fail|" + startupFailure +
                    "|binding=" + typeof globalThis.__webuitoolkit_mvvm_send +
                    "|webui=" + typeof globalThis.webui;
                }
                if (!body || !input || !form || !status) {
                  return "waiting|framework startup";
                }

                if (body.dataset.todoBrowserInput !== "true") {
                  body.dataset.todoBrowserInput = "true";
                  const valueSetter = Object.getOwnPropertyDescriptor(
                    HTMLInputElement.prototype,
                    "value")?.set;
                  valueSetter?.call(input, expected);
                  input.dispatchEvent(new Event("input", { bubbles: true }));
                  input.dispatchEvent(new Event("change", { bubbles: true }));
                  return "input|" + expected;
                }

                if (body.dataset.todoBrowserSubmitted !== "true") {
                  body.dataset.todoBrowserSubmitted = "true";
                  form.requestSubmit();
                  return "submitted|" + expected;
                }

                const found = Array.from(document.querySelectorAll(".todo-title"))
                  .map(element => element.textContent?.trim() ?? "")
                  .find(value => value === expected) ?? "";
                const stalePlaceholder = body.textContent
                  ?.includes("Connecting to the native C# ViewModel") === true;
                if (found === expected && stalePlaceholder) {
                  return "fail|stale connection placeholder|" +
                    (document.querySelector("#app")?.innerHTML ?? "").slice(0, 512);
                }
                if (found !== expected) {
                  const titles = Array.from(document.querySelectorAll(".todo-title"))
                    .map(element => element.textContent?.trim() ?? "")
                    .join(",");
                  const submit = form.querySelector("button");
                  return "waiting|" + expected +
                    "|input=" + input.value +
                    "|disabled=" + submit?.disabled +
                    "|status=" + Array.from(document.querySelectorAll(".badge"))
                      .map(element => element.textContent?.trim() ?? "")
                      .join(",") +
                    "|titles=" + titles;
                }
                if ({{(demo == TodoDemo.Simple ? "true" : "false")}}) {
                  return "pass|" + expected + "|rendered";
                }

                const importButton = Array.from(document.querySelectorAll("button"))
                  .find(element =>
                    element.textContent?.includes("Import starter tasks") ||
                    element.textContent?.includes("Importing"));
                if (!importButton) {
                  return "fail|advanced import command is unavailable";
                }
                if (body.dataset.todoBrowserImportStarted !== "true") {
                  body.dataset.todoBrowserImportStarted = "true";
                  importButton.click();
                  return "import-started|" + expected;
                }
                if (importButton.textContent?.includes("Importing")) {
                  body.dataset.todoBrowserImportingSeen = "true";
                  return "importing|" + expected;
                }
                const imported = Array.from(document.querySelectorAll(".todo-title"))
                  .some(element =>
                    element.textContent?.trim() === "Explore the guided creation flow");
                return body.dataset.todoBrowserImportingSeen === "true" && imported
                  ? "pass|" + expected + "|rendered-and-host-pushed"
                  : "waiting|host push";
              } catch (error) {
                return "error|" + String(error);
              }
            })();
            """;
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
}
