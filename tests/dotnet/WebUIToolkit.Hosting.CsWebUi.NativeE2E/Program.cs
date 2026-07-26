using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsWebUi;
using WebUIToolkit.Hosting.CsWebUi.Mvvm;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.CommunityToolkit;

namespace WebUIToolkit.Hosting.CsWebUi.NativeE2E;

internal static class Program
{
    private static readonly MvvmContract Contract = new("tests.native-cswebui-roundtrip");

    public static async Task<int> Main()
    {
        string? chromium = Environment.GetEnvironmentVariable("WEBUI_BROWSER_PATH");
        if (string.IsNullOrWhiteSpace(chromium) || !File.Exists(chromium))
        {
            Console.Error.WriteLine("FAIL: WEBUI_BROWSER_PATH does not name the pinned Chromium executable.");
            return 1;
        }

        string webRoot = Path.Combine(AppContext.BaseDirectory, "www");
        var registry = new MvvmSessionRegistry();
        registry.Map(Contract, static _ =>
        {
            var model = new CounterViewModel();
            CommunityToolkitMvvmBindingAdapter<CounterViewModel> adapter =
                new CommunityToolkitMvvmAdapterBuilder<CounterViewModel>(model)
                    .BindProperty(
                        1,
                        nameof(CounterViewModel.Count),
                        static state => state.Count,
                        static (state, value) => state.Count = value,
                        NativeE2EJsonContext.Default.Int32)
                    .BindCommand(
                        2,
                        nameof(CounterViewModel.IncrementCommand),
                        static state => state.IncrementCommand)
                    .Build();
            return ValueTask.FromResult(new MvvmSessionActivation(adapter));
        });

        await using IMvvmSessionFactory sessions = registry.Build();
        IMvvmSession session = await sessions.OpenAsync(Contract);
        string browserProfile = Path.Combine(
            Path.GetTempPath(),
            "webuitoolkit-native-e2e-" + Guid.NewGuid().ToString("N"));
        WebUiWindow? window = null;
        CsWebUiMvvmBridge? bridge = null;
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
            bridge = CsWebUiMvvmBridge.Attach(window, session);
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
                            "return document.body.dataset.result + '|' + document.querySelector('#count').textContent;",
                            TimeSpan.FromSeconds(1),
                            responseBufferSize: 128);
                        if (result.StartsWith("pass|", StringComparison.Ordinal) ||
                            result.StartsWith("fail|", StringComparison.Ordinal) ||
                            result.StartsWith("error|", StringComparison.Ordinal))
                        {
                            break;
                        }
                    }
                    catch (WebUiException)
                    {
                        // The browser has not completed its native connection yet.
                    }

                    await Task.Delay(100, timeout.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }

            bool passed = string.Equals(result, "pass|1", StringComparison.Ordinal);
            Console.WriteLine(passed
                ? "PASS: real CsWebUi server + Chromium + binary MVVM binding updated the DOM."
                : "FAIL: native browser-to-C# DOM roundtrip.");
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

            if (bridge is not null)
            {
                try
                {
                    await bridge.DisposeAsync();
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
                        await browser.WaitForExitAsync();
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
                    _ = await browserDiagnostics;
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
            Console.Error.WriteLine("FAIL: native end-to-end cleanup failed.");
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
}

internal sealed partial class CounterViewModel : ObservableObject
{
    [ObservableProperty]
    private int count;

    [RelayCommand]
    private void Increment() => Count++;
}

[JsonSerializable(typeof(int))]
internal sealed partial class NativeE2EJsonContext : JsonSerializerContext;
