using System.Diagnostics;
using Runic.Desktop;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

public sealed class BrowserHostTests
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

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        WebUiApplication.SetConnectionTimeout(30);
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
            Assert.True(window.IsShown);

            await window.ShowInBrowserAsync(Page("updated"), browser.Value, timeout.Token);
            string? title = null;
            for (var attempt = 0; attempt < 100 && title != "updated"; attempt++)
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
            WebUiApplication.SetConnectionTimeout(15);
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
