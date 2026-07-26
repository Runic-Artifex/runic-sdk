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
        if (await TodoFrontendQualityGates.RunManagedAsync(demo).ConfigureAwait(false) != 0)
        {
            Console.Error.WriteLine($"FAIL: {frontend} {demo} managed quality gates.");
            return 1;
        }

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
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    try
                    {
                        result = window.ExecuteJavaScript(
                            script,
                            TimeSpan.FromSeconds(1),
                            responseBufferSize: 8192);
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
                ? $"PASS: {frontend} {demo} passed roundtrip, validation, reconnect, " +
                  "cancellation, accessibility, and lifecycle gates."
                : $"FAIL: {frontend} {demo} native browser quality gate.");
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
        string expected = JsonSerializer.Serialize(
            taskTitle,
            WebUIToolkit.Samples.SimpleTodo.TodoJsonContext.Default.String);
        string selector = demo == TodoDemo.Simple
            ? "\"#new-title\""
            : "\"input[placeholder='Task title']\"";
        return $$"""
            return (() => {
              try {
                const expected = {{expected}};
                const advanced = {{(demo == TodoDemo.Advanced ? "true" : "false")}};
                const body = document.body;
                const input = document.querySelector({{selector}});
                const form = input?.closest("form");
                const buttons = () => Array.from(document.querySelectorAll("button"));
                const findButton = text => buttons().find(element =>
                  element.textContent?.includes(text));
                const titles = () => Array.from(document.querySelectorAll(".todo-title"))
                  .map(element => element.textContent?.trim() ?? "");
                const connected = () => Array.from(document.querySelectorAll(".badge"))
                  .some(element => element.textContent?.includes("Connected"));
                const setInput = value => {
                  const valueSetter = Object.getOwnPropertyDescriptor(
                    HTMLInputElement.prototype,
                    "value")?.set;
                  valueSetter?.call(input, value);
                  input.dispatchEvent(new Event("input", { bubbles: true }));
                  input.dispatchEvent(new Event("change", { bubbles: true }));
                };
                const auditAccessibility = () => {
                  const issues = [];
                  const ids = new Set();
                  for (const element of document.querySelectorAll("[id]")) {
                    if (ids.has(element.id)) issues.push("duplicate-id:" + element.id);
                    ids.add(element.id);
                  }
                  if (document.querySelectorAll("h1").length !== 1) {
                    issues.push("expected-one-h1");
                  }
                  for (const control of document.querySelectorAll("input,select,textarea")) {
                    const labelledBy = control.getAttribute("aria-labelledby");
                    const hasLabel = (control.labels?.length ?? 0) > 0 ||
                      (control.getAttribute("aria-label")?.trim().length ?? 0) > 0 ||
                      (labelledBy?.split(/\s+/).every(id => document.getElementById(id)) ?? false);
                    if (!hasLabel) {
                      issues.push("unlabelled-control:" +
                        (control.id || control.getAttribute("placeholder") || control.tagName));
                    }
                  }
                  for (const button of buttons()) {
                    const name = button.getAttribute("aria-label")?.trim() ||
                      button.getAttribute("title")?.trim() ||
                      button.textContent?.trim();
                    if (!name) issues.push("unlabelled-button");
                  }
                  for (const reference of document.querySelectorAll("[aria-describedby]")) {
                    for (const id of reference.getAttribute("aria-describedby").split(/\s+/)) {
                      if (id && !document.getElementById(id)) {
                        issues.push("missing-description:" + id);
                      }
                    }
                  }
                  return issues;
                };
                const startupFailure = document.querySelector(".alert-danger")?.textContent?.trim();
                if (startupFailure) {
                  return "fail|" + startupFailure +
                    "|binding=" + typeof globalThis.__webuitoolkit_mvvm_send +
                    "|webui=" + typeof globalThis.webui;
                }
                if (!body || !input || !form || !connected()) {
                  return "waiting|framework startup";
                }

                if (body.dataset.todoBrowserAccessibility !== "true") {
                  const issues = auditAccessibility();
                  if (issues.length) return "fail|accessibility|" + issues.join(",");
                  body.dataset.todoBrowserAccessibility = "true";
                }

                if (body.dataset.todoBrowserValidationStarted !== "true") {
                  body.dataset.todoBrowserValidationStarted = "true";
                  setInput("x");
                  form.requestSubmit();
                  return "validation-started";
                }
                if (body.dataset.todoBrowserValidationPassed !== "true") {
                  if (advanced) {
                    const feedback = document.querySelector(".invalid-feedback");
                    if (!input.classList.contains("is-invalid") ||
                        !feedback?.textContent?.trim()) {
                      return "waiting|validation projection";
                    }
                  } else {
                    const submit = form.querySelector("button");
                    if (!submit?.disabled || titles().includes("x")) {
                      return "fail|simple validation admitted an invalid title";
                    }
                  }
                  body.dataset.todoBrowserValidationPassed = "true";
                  setInput(expected);
                  return "input|" + expected;
                }

                if (body.dataset.todoBrowserSubmitted !== "true") {
                  body.dataset.todoBrowserSubmitted = "true";
                  form.requestSubmit();
                  return "submitted|" + expected;
                }

                const expectedCount = titles().filter(value => value === expected).length;
                const stalePlaceholder = body.textContent
                  ?.includes("Connecting to the native C# ViewModel") === true;
                if (expectedCount === 1 && stalePlaceholder) {
                  return "fail|stale connection placeholder|" +
                    (document.querySelector("#app")?.innerHTML ?? "").slice(0, 512);
                }
                if (expectedCount !== 1) {
                  return "waiting|" + expected +
                    "|input=" + input.value +
                    "|status=" + Array.from(document.querySelectorAll(".badge"))
                      .map(element => element.textContent?.trim() ?? "")
                      .join(",") +
                    "|titles=" + titles().join(",");
                }

                if (advanced && body.dataset.todoBrowserCancellationPassed !== "true") {
                  if (body.dataset.todoBrowserCancellationStarted !== "true") {
                    const importButton = findButton("Import starter tasks");
                    if (!importButton) return "fail|advanced import command is unavailable";
                    body.dataset.todoBrowserCancellationStarted = "true";
                    importButton.click();
                    return "cancellation-started";
                  }
                  if (body.dataset.todoBrowserCancellationRequested !== "true") {
                    const cancelButton = findButton("Cancel import");
                    if (!cancelButton) return "waiting|cancellable import";
                    body.dataset.todoBrowserCancellationRequested = "true";
                    cancelButton.click();
                    return "cancellation-requested";
                  }
                  const cancellationRecorded = body.textContent
                    ?.includes("Starter-task import was cancelled before persistence.") === true;
                  if (!cancellationRecorded || findButton("Cancel import")) {
                    return "waiting|cancellation completion";
                  }
                  body.dataset.todoBrowserCancellationPassed = "true";
                }

                const reconnectCount = Number(body.dataset.todoBrowserReconnectCount ?? "0");
                if (reconnectCount < 3) {
                  if (body.dataset.todoBrowserReconnectPending === "true") {
                    const failure = body.dataset.todoBrowserReconnectFailure;
                    if (failure) return "fail|reconnect|" + failure;
                    return "waiting|reconnect-" + (reconnectCount + 1);
                  }
                  if (typeof globalThis.__webuitoolkitTodoReconnect !== "function") {
                    return "fail|reconnect diagnostic is unavailable";
                  }
                  body.dataset.todoBrowserReconnectPending = "true";
                  globalThis.__webuitoolkitTodoReconnect().then(() => {
                    body.dataset.todoBrowserReconnectCount = String(reconnectCount + 1);
                    delete body.dataset.todoBrowserReconnectPending;
                  }).catch(error => {
                    body.dataset.todoBrowserReconnectFailure = String(error);
                  });
                  return "reconnect-started|" + (reconnectCount + 1);
                }
                if (!connected() || titles().filter(value => value === expected).length !== 1) {
                  return "waiting|authoritative reconnect snapshot";
                }

                const finalAccessibilityIssues = auditAccessibility();
                if (finalAccessibilityIssues.length) {
                  return "fail|dynamic-accessibility|" + finalAccessibilityIssues.join(",");
                }
                if (!advanced) {
                  return "pass|" + expected + "|validated-reconnected-accessible";
                }

                const importButton = findButton("Import starter tasks") ??
                  findButton("Importing");
                if (!importButton) return "fail|advanced import command disappeared";
                if (body.dataset.todoBrowserImportStarted !== "true") {
                  body.dataset.todoBrowserImportStarted = "true";
                  importButton.click();
                  return "import-started|" + expected;
                }
                if (importButton.textContent?.includes("Importing")) {
                  body.dataset.todoBrowserImportingSeen = "true";
                  return "importing|" + expected;
                }
                const imported = titles().includes("Explore the guided creation flow");
                return body.dataset.todoBrowserImportingSeen === "true" && imported
                  ? "pass|" + expected +
                    "|validated-cancelled-reconnected-accessible-host-pushed"
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
