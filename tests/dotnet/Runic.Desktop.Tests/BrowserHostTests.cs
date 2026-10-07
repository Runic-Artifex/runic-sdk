using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Runic.Desktop;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

public sealed class BrowserHostTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void DiscoversBrowsersFromTheConfiguredFolder()
    {
        var folder = Directory.CreateTempSubdirectory("runic-desktop-browser-");
        try
        {
            var executable = Path.Combine(folder.FullName, ChromeExecutableName());
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, string.Empty);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var installation = WebUiBrowserDiscovery.Find(WebUiBrowser.Chrome, folder.FullName);
            Assert.NotNull(installation);
            Assert.Equal(WebUiBrowser.Chrome, installation.Browser);
            Assert.Equal(Path.GetFullPath(executable), installation.ExecutablePath);
            Assert.True(installation.IsChromiumBased);

            WebUiApplication.SetBrowserFolder(folder.FullName);
            Assert.True(WebUiApplication.BrowserExists(WebUiBrowser.Chrome));
            Assert.True(WebUiApplication.BrowserExists(WebUiBrowser.AnyBrowser));
            using var window = new WebUiWindow();
            Assert.Equal(WebUiBrowser.Chrome, window.BestBrowser);
        }
        finally
        {
            WebUiApplication.SetBrowserFolder(string.Empty);
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void BuildsWebUiCompatibleChromiumAndFirefoxArguments()
    {
        var url = new Uri("http://127.0.0.1:12345/index.html");
        var chromium = new WebUiBrowserInstallation(WebUiBrowser.Chrome, "/browser", IsChromiumBased: true);
        var chromiumArguments = WebUiBrowserHost.BuildArguments(
            chromium,
            url,
            new WebUiBrowserLaunchOptions(
                null,
                "/profile with spaces",
                "http://proxy.test:8080",
                [],
                Kiosk: true,
                Hidden: true,
                Width: 800,
                Height: 600,
                X: 12,
                Y: 34));

        Assert.Contains("--user-data-dir=/profile with spaces", chromiumArguments);
        Assert.Contains("--no-first-run", chromiumArguments);
        Assert.Contains("--kiosk", chromiumArguments);
        Assert.Contains("--headless=new", chromiumArguments);
        Assert.Contains("--window-size=800,600", chromiumArguments);
        Assert.Contains("--window-position=12,34", chromiumArguments);
        Assert.Contains("--proxy-server=http://proxy.test:8080", chromiumArguments);
        Assert.DoesNotContain("--no-proxy-server", chromiumArguments);
        Assert.Contains("--deny-permission-prompts", chromiumArguments);
        Assert.DoesNotContain("--auto-accept-camera-and-microphone-capture", chromiumArguments);
        Assert.Equal($"--app={url.AbsoluteUri}", chromiumArguments[^1]);

        var mediaArguments = WebUiBrowserHost.BuildArguments(
            chromium,
            url,
            new WebUiBrowserLaunchOptions(
                null, null, null, [], false, false, null, null, null, null,
                DesktopPermissionGrant.MediaCapture));
        Assert.Contains("--auto-accept-camera-and-microphone-capture", mediaArguments);
        Assert.DoesNotContain("--deny-permission-prompts", mediaArguments);

        var customArguments = WebUiBrowserHost.BuildArguments(
            chromium,
            url,
            new WebUiBrowserLaunchOptions(null, "/profile", null, ["--custom=value"], false, false, null, null, null, null));
        Assert.Contains("--custom=value", customArguments);
        Assert.Contains("--deny-permission-prompts", customArguments);
        Assert.DoesNotContain("--no-first-run", customArguments);
        Assert.DoesNotContain("--no-proxy-server", customArguments);

        var firefox = new WebUiBrowserInstallation(WebUiBrowser.Firefox, "/firefox", IsChromiumBased: false);
        var firefoxArguments = WebUiBrowserHost.BuildArguments(
            firefox,
            url,
            new WebUiBrowserLaunchOptions("WebUI", "/firefox profile", null, [], false, true, 640, 480, null, null));
        Assert.Equal("--profile", firefoxArguments[0]);
        Assert.Equal("/firefox profile", firefoxArguments[1]);
        Assert.Contains("--new-instance", firefoxArguments);
        Assert.Contains("--headless", firefoxArguments);
        Assert.Equal(url.AbsoluteUri, firefoxArguments[^1]);
    }

    [Fact]
    public void ParsesQuotedCustomParametersWithoutUsingAShell()
    {
        Assert.Equal(
            ["--flag", "value with spaces", @"C:\browser\profile", string.Empty, "quoted\"value"],
            WebUiCommandLine.Split("--flag 'value with spaces' C:\\browser\\profile \"\" \"quoted\\\"value\""));
        Assert.Throws<ArgumentException>(() => WebUiCommandLine.Split("--flag \"unterminated"));
    }

    [Fact]
    public void CreatesButDoesNotOwnAnExplicitProfileFolder()
    {
        var parent = Directory.CreateTempSubdirectory("runic-desktop-profile-");
        try
        {
            var profile = Path.Combine(parent.FullName, "custom");
            using (var window = new WebUiWindow())
            {
                window.SetProfile("Custom", profile);
                Assert.True(Directory.Exists(profile));
            }

            Assert.True(Directory.Exists(profile));
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task OwnsChromiumProcessProfileRestartAndNaturalExit()
    {
        var browser = FindChromiumBrowser();
        if (browser is null)
        {
            return;
        }

        // Each launch starts Chromium with a fresh profile. On Windows runners a
        // fresh Chromium occasionally keeps running without requesting anything
        // (#35); a 45-second wait did not help. Such a launch is relaunched once
        // with a fresh profile after 30 seconds (the default two attempts). The
        // whole test stays inside CI's two-minute hang timeout, so a timeout
        // still reports its stage and attempt. Relaunches are written to the
        // test output.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(110));
        WebUiApplication.SetConnectionTimeout(30);
        WebUiApplication.SetBrowserLaunchAttempts(2);
        using var relaunches = new StalledLaunchListener(output);
        Trace.Listeners.Add(relaunches);
        await using var window = new WebUiWindow();
        window.SetHidden(true);
        window.SetCustomParameters("--no-first-run --no-sandbox --disable-gpu --disable-dev-shm-usage");
        try
        {
            var serverOnlyUrl = await window.StartServerAsync(Page("server-only"), timeout.Token);
            var shownUrl = await window.ShowInBrowserAsync(Page("first"), browser.Value, timeout.Token);
            Assert.Equal(serverOnlyUrl, shownUrl);
            var firstProcessId = checked((int)window.BrowserProcessId);
            var firstProfile = window.GeneratedProfilePath;
            Assert.True(firstProcessId > 0);
            Assert.NotNull(firstProfile);
            Assert.True(Directory.Exists(firstProfile));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(firstProfile));
            }
            Assert.True(window.IsShown);

            await window.ShowInBrowserAsync(Page("updated"), browser.Value, timeout.Token);
            string? title = null;
            // A navigation tears down the authenticated session before Chromium
            // reconnects. Bound readiness by time, not 100 fast disconnected polls
            // (only 2.5 seconds on a busy Windows runner).
            var navigationDeadline = DateTime.UtcNow.AddSeconds(20);
            while (title != "updated" && DateTime.UtcNow < navigationDeadline)
            {
                try
                {
                    title = await window.ExecuteJavaScriptAsync(
                        "return document.title;",
                        TimeSpan.FromSeconds(2),
                        cancellationToken: timeout.Token);
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                }
                if (title != "updated")
                {
                    await Task.Delay(25, timeout.Token);
                }
            }
            Assert.Equal("updated", title);

            await window.CloseAsync(timeout.Token);
            await WaitForProcessExitAsync(firstProcessId, timeout.Token);
            AssertProfileDeleted(window, firstProfile);
            Assert.Equal((nuint)0, window.BrowserProcessId);

            await window.ShowInBrowserAsync(Page("second"), browser.Value, timeout.Token);
            var secondProcessId = checked((int)window.BrowserProcessId);
            var secondProfile = window.GeneratedProfilePath;
            Assert.NotEqual(firstProfile, secondProfile);
            using (var secondProcess = Process.GetProcessById(secondProcessId))
            {
                // Exercise parent exit without macOS's unsafe recursive kill path.
                secondProcess.Kill(entireProcessTree: !OperatingSystem.IsMacOS());
            }

            await WebUiApplication.WaitAsync(timeout.Token);
            Assert.Null(window.Url);
            AssertProfileDeleted(window, secondProfile);
        }
        finally
        {
            Trace.Listeners.Remove(relaunches);
            WebUiApplication.SetConnectionTimeout(15);
        }
    }

    // Records each browser-launch-stalled warning and writes it to the test output.
    private sealed class StalledLaunchListener(Xunit.Abstractions.ITestOutputHelper output) : TraceListener
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (message?.StartsWith(WebUiWindow.BrowserLaunchStalledCode, StringComparison.Ordinal) == true)
            {
                Messages.Enqueue(message);
                output.WriteLine(message);
            }
        }
    }

    [Fact]
    public async Task RelaunchesABrowserThatStallsBeforeItsFirstRequestWithAFreshProfile()
    {
        if (OperatingSystem.IsWindows()) return;

        var folder = Directory.CreateTempSubdirectory("runic-desktop-stall-");
        var launches = Path.Combine(folder.FullName, "launches");
        var window = new WebUiWindow();
        using var relaunches = new StalledLaunchListener(output);
        Trace.Listeners.Add(relaunches);
        try
        {
            await WriteStallingBrowserAsync(folder.FullName, launches);
            WebUiApplication.SetBrowserFolder(folder.FullName);
            WebUiApplication.SetConnectionTimeout(1);
            WebUiApplication.SetBrowserLaunchAttempts(3);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
                window.ShowInBrowserAsync(Page("stalled"), WebUiBrowser.Chrome, timeout.Token));

            Assert.Contains(
                "within 1 seconds on launch attempt 3 of 3: no request reached the server; the browser process is still running",
                exception.Message);
            var profiles = await File.ReadAllLinesAsync(launches, timeout.Token);
            Assert.Equal(3, profiles.Length);
            Assert.Equal(3, profiles.Distinct(StringComparer.Ordinal).Count());
            Assert.All(profiles, profile => Assert.False(Directory.Exists(profile), profile));
            Assert.Equal((nuint)0, window.BrowserProcessId);
            // Without a Desktop logger, each relaunch is traced with the attempt that stalled.
            Assert.Collection(relaunches.Messages,
                message => Assert.Contains("on launch attempt 1 of 3: no request reached the server", message),
                message => Assert.Contains("on launch attempt 2 of 3: no request reached the server", message));
        }
        finally
        {
            Trace.Listeners.Remove(relaunches);
            await window.DisposeAsync();
            ResetBrowserLaunch();
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DoesNotRelaunchABrowserWhosePageWasRequested()
    {
        if (OperatingSystem.IsWindows()) return;

        var folder = Directory.CreateTempSubdirectory("runic-desktop-stall-");
        var launches = Path.Combine(folder.FullName, "launches");
        var window = new WebUiWindow();
        try
        {
            await WriteStallingBrowserAsync(folder.FullName, launches);
            WebUiApplication.SetBrowserFolder(folder.FullName);
            WebUiApplication.SetConnectionTimeout(2);
            WebUiApplication.SetBrowserLaunchAttempts(2);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var show = window.ShowInBrowserAsync(Page("requested"), WebUiBrowser.Chrome, timeout.Token);
            while (!File.Exists(launches)) await Task.Delay(10, timeout.Token);
            // The page request stands in for a browser that started, then stalled later.
            using (var client = new HttpClient())
            {
                await client.GetStringAsync(window.Url, timeout.Token);
            }
            var exception = await Assert.ThrowsAsync<TimeoutException>(() => show);

            Assert.Contains("on launch attempt 1 of 2: the page was requested", exception.Message);
            Assert.Single(await File.ReadAllLinesAsync(launches, timeout.Token));
        }
        finally
        {
            await window.DisposeAsync();
            ResetBrowserLaunch();
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DesktopHostReportsAndLogsEachStalledLaunch()
    {
        if (OperatingSystem.IsWindows()) return;

        var folder = Directory.CreateTempSubdirectory("runic-desktop-stall-");
        var launches = Path.Combine(folder.FullName, "launches");
        try
        {
            await WriteStallingBrowserAsync(folder.FullName, launches);
            var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
            var loggers = new ConfigurationValidationTests.RecordingLoggerFactory();
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                BrowserFolder = folder.FullName,
                ConnectionTimeout = TimeSpan.FromMilliseconds(500),
                BrowserLaunchAttempts = 2,
                DiagnosticSink = diagnostics.Enqueue,
                LoggerFactory = loggers,
            });
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Html("never requested"),
            });

            var exception = await Assert.ThrowsAsync<DesktopException>(async () =>
                await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Chrome }));

            Assert.Equal("presentation-connection-timeout", exception.Code);
            Assert.Contains("on launch attempt 2 of 2: no request reached the server",
                Assert.IsType<TimeoutException>(exception.InnerException).Message);
            var stalled = Assert.Single(diagnostics, static item => item.Code == "browser-launch-stalled");
            Assert.Equal(DesktopDiagnosticSeverity.Warning, stalled.Severity);
            Assert.Contains("on launch attempt 1 of 2", stalled.Message);
            Assert.Contains("with a fresh profile (attempt 2 of 2)", stalled.Message);
            Assert.Contains(loggers.Entries, static entry =>
                entry.Category == "Runic.Desktop" && entry.Level == LogLevel.Warning && entry.EventId.Id == 3004
                && entry.Message.Contains("attempt 1 of 2", StringComparison.Ordinal));
            var profiles = await File.ReadAllLinesAsync(launches);
            Assert.Equal(2, profiles.Distinct(StringComparer.Ordinal).Count());
            Assert.All(profiles, profile => Assert.False(Directory.Exists(profile), profile));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DesktopHostLaunchesAStalledBrowserOnceWhenRelaunchingIsDisabled()
    {
        if (OperatingSystem.IsWindows()) return;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await DesktopHost.StartAsync(new DesktopHostOptions { BrowserLaunchAttempts = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await DesktopHost.StartAsync(new DesktopHostOptions { BrowserLaunchAttempts = 6 }));

        var folder = Directory.CreateTempSubdirectory("runic-desktop-stall-");
        var launches = Path.Combine(folder.FullName, "launches");
        try
        {
            await WriteStallingBrowserAsync(folder.FullName, launches);
            var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                BrowserFolder = folder.FullName,
                ConnectionTimeout = TimeSpan.FromMilliseconds(500),
                BrowserLaunchAttempts = 1,
                DiagnosticSink = diagnostics.Enqueue,
            });
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Html("never requested"),
            });

            var exception = await Assert.ThrowsAsync<DesktopException>(async () =>
                await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Chrome }));

            var timeout = Assert.IsType<TimeoutException>(exception.InnerException);
            Assert.Contains("no request reached the server", timeout.Message);
            Assert.DoesNotContain("attempt", timeout.Message);
            Assert.DoesNotContain(diagnostics, static item => item.Code == "browser-launch-stalled");
            Assert.Single(await File.ReadAllLinesAsync(launches));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    // A Chrome stand-in that records its profile, then runs without requesting anything.
    [UnsupportedOSPlatform("windows")]
    private static async Task WriteStallingBrowserAsync(string folder, string launches)
    {
        var executable = Path.Combine(folder, ChromeExecutableName());
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        await File.WriteAllTextAsync(executable, $$"""
            #!/bin/sh
            for arg do
              case "$arg" in
                --user-data-dir=*) profile=${arg#*=} ;;
              esac
            done
            printf '%s\n' "$profile" >> '{{launches}}'
            exec sleep 60
            """);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void ResetBrowserLaunch()
    {
        WebUiApplication.SetBrowserFolder(string.Empty);
        WebUiApplication.SetConnectionTimeout(15);
        WebUiApplication.SetBrowserLaunchAttempts(2);
    }

    [Fact]
    public async Task ClosingBrowserWaitsForInheritedPipesBeforeDeletingProfile()
    {
        if (OperatingSystem.IsWindows()) return;

        var folder = Directory.CreateTempSubdirectory("runic-desktop-helper-");
        var releaseHelper = Path.Combine(folder.FullName, "release-helper");
        var exitParent = Path.Combine(folder.FullName, "exit-parent");
        var window = new WebUiWindow();
        try
        {
            var executable = Path.Combine(folder.FullName, ChromeExecutableName());
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            await File.WriteAllTextAsync(executable, """
                #!/bin/sh
                for arg do
                  case "$arg" in
                    --user-data-dir=*) profile=${arg#*=} ;;
                    --gate=*) gate=${arg#*=} ;;
                  esac
                done
                (
                  while [ ! -f "$gate/release-helper" ]; do sleep 0.02; done
                  mkdir -p "$profile/Default"
                  printf late > "$profile/Default/helper-write"
                  touch "$gate/helper-finished"
                ) &
                touch "$gate/ready"
                while [ ! -f "$gate/exit-parent" ]; do sleep 0.02; done
                exit 0
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            WebUiApplication.SetBrowserFolder(folder.FullName);
            WebUiApplication.SetConfiguration(WebUiConfiguration.ShowWaitConnection, false);
            window.SetCustomParameters($"--gate=\"{folder.FullName}\"");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await window.ShowInBrowserAsync(Page("helper"), WebUiBrowser.Chrome, timeout.Token);
            var profile = window.GeneratedProfilePath;
            var processId = checked((int)window.BrowserProcessId);
            while (!File.Exists(Path.Combine(folder.FullName, "ready")))
                await Task.Delay(10, timeout.Token);

            await File.WriteAllTextAsync(exitParent, string.Empty, timeout.Token);
            await WaitForProcessExitAsync(processId, timeout.Token);
            var close = window.CloseAsync(timeout.Token);
            // The parent has exited, but its helper still owns the output pipes
            // and can write to the profile. Closing must join that helper first.
            await Assert.ThrowsAsync<TimeoutException>(() => close.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.True(Directory.Exists(profile));
            await File.WriteAllTextAsync(releaseHelper, string.Empty, timeout.Token);
            await close;
            AssertProfileDeleted(window, profile);
        }
        finally
        {
            await File.WriteAllTextAsync(exitParent, string.Empty);
            await File.WriteAllTextAsync(releaseHelper, string.Empty);
            try
            {
                if (File.Exists(Path.Combine(folder.FullName, "ready")))
                {
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    while (!File.Exists(Path.Combine(folder.FullName, "helper-finished")))
                        await Task.Delay(10, cleanupTimeout.Token);
                }
            }
            finally
            {
                await window.DisposeAsync();
                WebUiApplication.SetBrowserFolder(string.Empty);
                WebUiApplication.SetConfiguration(WebUiConfiguration.ShowWaitConnection, true);
                folder.Delete(recursive: true);
            }
        }
    }

    [Fact]
    public async Task CanDisposeOwnedBrowserFromItsBindingCallback()
    {
        var browser = FindChromiumBrowser();
        if (browser is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        WebUiApplication.SetConnectionTimeout(30);
        var destroyed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new WebUiWindow();
        window.SetHidden(true);
        window.SetCustomParameters("--no-first-run --no-sandbox --disable-gpu --disable-dev-shm-usage");
        window.Bind("destroy", e =>
        {
            e.Window.Dispose();
            destroyed.TrySetResult();
        });
        try
        {
            await window.ShowInBrowserAsync("""
                <!doctype html><html><head><script src="webui.js"></script></head><body>
                <script>(async()=>{await webui.connected;await destroy();})();</script>
                </body></html>
                """, browser.Value, timeout.Token);
            await destroyed.Task.WaitAsync(timeout.Token);
            await WebUiApplication.ExitAsync(timeout.Token);
            await WebUiApplication.WaitAsync(timeout.Token);
            Assert.Null(window.Url);
            Assert.Equal((nuint)0, window.BrowserProcessId);
        }
        finally
        {
            await window.DisposeAsync();
            WebUiApplication.SetConnectionTimeout(15);
        }
    }

    private static void AssertProfileDeleted(WebUiWindow window, string? profile)
    {
        if (!Directory.Exists(profile)) return;

        // Only inspect our generated test profile, never browser contents or a user profile.
        string entries;
        try
        {
            entries = string.Join(", ", Directory.EnumerateFileSystemEntries(profile)
                .Take(30).Select(path => $"{Path.GetFileName(path)} ({File.GetAttributes(path)})"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            entries = exception.Message;
        }
        Assert.Fail($"Generated profile remains: {profile}. Entries: {entries}. " +
            $"Last deletion failure: {window.LastProfileCleanupFailure?.ToString() ?? "none; directory may have been recreated"}");
    }

    private static WebUiBrowser? FindChromiumBrowser()
    {
        foreach (var browser in new[]
        {
            WebUiBrowser.Chrome,
            WebUiBrowser.Edge,
            WebUiBrowser.Chromium,
            WebUiBrowser.Epic,
            WebUiBrowser.Vivaldi,
            WebUiBrowser.Brave,
            WebUiBrowser.Yandex,
        })
        {
            if (WebUiApplication.BrowserExists(browser))
            {
                return browser;
            }
        }

        return null;
    }

    private static async Task WaitForProcessExitAsync(int processId, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }
    }

    private static string Page(string value) => $$"""
        <!doctype html><html><head><script src="webui.js"></script><title>{{value}}</title></head><body>{{value}}</body></html>
        """;

    private static string ChromeExecutableName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "chrome.exe";
        }
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine("Google Chrome.app", "Contents", "MacOS", "Google Chrome");
        }
        return "google-chrome";
    }
}
