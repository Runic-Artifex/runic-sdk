using TUnit.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// #1097: a browser can restart itself at launch. Edge does when its environment carries a compatibility layer
// (__COMPAT_LAYER, which Explorer sets for some starts) and to shed elevation; the process Runic started then exits
// before the new copy requests the launch address. Runic keeps the compatibility layer from Edge, and treats such an exit
// as a hand-off, not as the window closing.
public sealed class BrowserHandOffTests
{
    private static readonly Uri Url = new("http://127.0.0.1:12345/surface-1/_runic_launch/code");

    [Test]
    public void RemovesTheCompatibilityLayerFromTheBrowserEnvironmentOnWindows()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["__COMPAT_LAYER"] = "Installer",
            ["__compat_layer"] = "RunAsInvoker",
            ["PATH"] = "/bin",
        };

        WebUiBrowserHost.PrepareEnvironment(environment, windows: false);
        Assert.Equal(3, environment.Count);

        WebUiBrowserHost.PrepareEnvironment(environment, windows: true);
        Assert.Equal("/bin", Assert.Single(environment).Value);
    }

    [Test]
    public void LeavesTheApplicationsOwnEnvironmentAlone()
    {
        var saved = Environment.GetEnvironmentVariable(WebUiBrowserHost.CompatLayerVariable);
        Environment.SetEnvironmentVariable(WebUiBrowserHost.CompatLayerVariable, "Installer");
        try
        {
            var startInfo = new ProcessStartInfo("browser");
            Assert.True(startInfo.Environment.ContainsKey(WebUiBrowserHost.CompatLayerVariable));

            WebUiBrowserHost.PrepareEnvironment(startInfo.Environment, windows: true);

            Assert.False(startInfo.Environment.ContainsKey(WebUiBrowserHost.CompatLayerVariable));
            Assert.Equal("Installer", Environment.GetEnvironmentVariable(WebUiBrowserHost.CompatLayerVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(WebUiBrowserHost.CompatLayerVariable, saved);
        }
    }

    [Test]
    public void PassesEdgeTheSwitchThatSkipsItsCompatibilityLayerRelaunch()
    {
        const string skip = WebUiBrowserHost.EdgeSkipCompatLayerRelaunch;
        var edge = new WebUiBrowserInstallation(WebUiBrowser.Edge, @"C:\Edge\msedge.exe", IsChromiumBased: true);
        var chrome = new WebUiBrowserInstallation(WebUiBrowser.Chrome, @"C:\Chrome\chrome.exe", IsChromiumBased: true);

        var defaults = WebUiBrowserHost.BuildArguments(edge, Url, Options([]), windows: true);
        Assert.Single(defaults, static argument => argument == skip);
        Assert.Contains("--no-first-run", defaults);
        Assert.Equal($"--app={Url.AbsoluteUri}", defaults[^1]);

        // The application's own arguments replace the isolation defaults (unchanged), not the switch.
        var own = WebUiBrowserHost.BuildArguments(edge, Url, Options(["--start-maximized"]), windows: true);
        Assert.Contains(skip, own);
        Assert.Contains("--start-maximized", own);
        Assert.DoesNotContain("--no-first-run", own);

        // An application that names it already passes it once.
        Assert.Single(WebUiBrowserHost.BuildArguments(edge, Url, Options([skip]), windows: true), static argument => argument == skip);

        // Only Edge on Windows relaunches for a compatibility layer.
        Assert.DoesNotContain(skip, WebUiBrowserHost.BuildArguments(chrome, Url, Options([]), windows: true));
        Assert.DoesNotContain(skip, WebUiBrowserHost.BuildArguments(edge, Url, Options([]), windows: false));
    }

    [Test]
    public void AnUnusedLaunchCodeIsWithdrawnButABoundClientStays()
    {
        var binding = new BrowserLaunchBinding();
        var code = binding.Begin(Url);
        Assert.True(binding.IsPending);
        binding.Withdraw();
        Assert.False(binding.IsPending);
        Assert.Equal(BrowserLaunchBinding.Outcome.Refused, binding.Consume(code, 7, out _));

        code = binding.Begin(Url);
        Assert.Equal(BrowserLaunchBinding.Outcome.Bound, binding.Consume(code, 7, out _));
        binding.Withdraw();
        Assert.True(binding.Admits(7));
        Assert.Equal(BrowserLaunchBinding.Outcome.AlreadyBound, binding.Consume(code, 7, out _));
    }

    // The process exits before any request: the launch code stays valid for the copy it handed off to, still once.
    [Test]
    public async Task KeepsTheLaunchCodeForTheCopyABrowserHandsOffTo()
    {
        if (OperatingSystem.IsWindows()) return;

        await using var fixture = await HandOffFixture.StartAsync(exitAfterGate: false);
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(30));
        await fixture.WaitForHandOffAsync(timeout.Token);
        Assert.False(fixture.Opening.IsCompleted);

        using var copy = Client();
        using (var launched = await copy.GetAsync(fixture.LaunchUrl, timeout.Token))
        {
            Assert.Equal(HttpStatusCode.SeeOther, launched.StatusCode);
        }
        using var other = Client();
        using (var again = await other.GetAsync(fixture.LaunchUrl, timeout.Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        }
    }

    // The deadline passes after a hand-off: a distinct diagnostic, no fallback, and the code is withdrawn.
    [Test]
    public async Task ReportsAHandOffWhoseWindowNeverConnected()
    {
        if (OperatingSystem.IsWindows()) return;

        await using var fixture = await HandOffFixture.StartAsync(exitAfterGate: false);
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(30));
        await fixture.WaitForHandOffAsync(timeout.Token);
        await Task.WhenAny(fixture.Time.WaitForTimerAsync(HandOffFixture.Timeout, timeout.Token), fixture.Opening).WaitAsync(timeout.Token);
        Assert.False(fixture.Opening.IsCompleted);
        fixture.Time.Advance(HandOffFixture.Timeout);

        var exception = await Assert.ThrowsAsync<DesktopException>(() => fixture.Opening.WaitAsync(timeout.Token));
        Assert.Equal("presentation-browser-handed-off", exception.Code);
        Assert.Equal(DesktopErrorCategory.TimedOut, exception.Category);
        var inner = Assert.IsType<BrowserHandOffTimeoutException>(exception.InnerException);
        Assert.Contains(
            "The browser handed off to another process and no window connected within 10 seconds: no request reached the server; the browser process Runic started exited with code 0 before its first request",
            inner.Message);
        Assert.DoesNotContain("closed before bridge authentication", inner.Message);
        Assert.Contains(fixture.Diagnostics, static diagnostic => diagnostic.Code == "presentation-browser-handed-off");
        // One launch: an exited browser is not relaunched, as its copy may still open a window.
        Assert.Equal(1, fixture.Launches);

        using var late = Client();
        using var refused = await late.GetAsync(fixture.LaunchUrl, timeout.Token);
        Assert.NotEqual(HttpStatusCode.SeeOther, refused.StatusCode);
    }

    // A process that exits after the launch request arrived closed its window, as before: no hand-off, no waiting.
    [Test]
    public async Task AnExitAfterTheLaunchRequestClosesTheWindow()
    {
        if (OperatingSystem.IsWindows()) return;

        await using var fixture = await HandOffFixture.StartAsync(exitAfterGate: true);
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(30));
        using var page = Client();
        using (var launched = await page.GetAsync(fixture.LaunchUrl, timeout.Token))
        {
            Assert.Equal(HttpStatusCode.SeeOther, launched.StatusCode);
        }
        fixture.ReleaseGate();

        // The clock never moves: only the exit can end the opening.
        var exception = await Assert.ThrowsAsync<DesktopException>(() => fixture.Opening.WaitAsync(timeout.Token));
        Assert.Equal("presentation-start-failed", exception.Code);
        Assert.Contains("closed before bridge authentication", Assert.IsType<IOException>(exception.InnerException).Message);
    }

    // A real Chromium behind a launcher that starts it and exits at once, as Edge's relaunch does. The window opens
    // and stays open on its connection; when the browser goes away, the window closes and nothing hangs.
    [Test]
    public async Task AWindowHandedOffToAnotherProcessOpensAndClosesWithItsPage()
    {
        if (!OperatingSystem.IsLinux() || BrowserBridgeTests.FindChrome() is not { } chrome) return;

        var folder = Directory.CreateTempSubdirectory("runic-desktop-handoff-");
        var pidFile = Path.Combine(folder.FullName, "copy.pid");
        try
        {
            var launcher = Path.Combine(folder.FullName, "google-chrome");
            await File.WriteAllTextAsync(launcher, $$"""
                #!/bin/sh
                '{{chrome}}' "$@" &
                printf '%s\n' "$!" > '{{pidFile}}.tmp'
                mv '{{pidFile}}.tmp' '{{pidFile}}'
                exit 0
                """);
            File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                BrowserFolder = folder.FullName,
                ConnectionTimeout = TestTimeouts.Scale(TimeSpan.FromSeconds(30)),
                DiagnosticSink = diagnostics.Enqueue,
            });
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Html("<!doctype html><title>handed off</title><script src=\"webui.js\"></script>"),
            });

            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Chrome,
                Hidden = true,
                // As BrowserBridgeTests runs Chromium on CI runners, where its sandbox is unavailable.
                BrowserArguments = "--no-sandbox --disable-gpu --disable-dev-shm-usage",
            });

            using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(60));
            var copyId = int.Parse(await File.ReadAllTextAsync(pidFile, timeout.Token), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(window.IsOpen);
            Assert.Equal(0, window.ProcessId);
            // Longer than the reconnect grace: the launcher's exit is not the window's close.
            await Task.Delay(WebUiWindow.HandOffReconnectGrace + TimeSpan.FromSeconds(1), timeout.Token);
            Assert.True(window.IsOpen);
            Assert.Equal(1, surface.ConnectionCount);

            using (var copy = Process.GetProcessById(copyId))
            {
                copy.Kill(entireProcessTree: true);
            }
            await window.WaitForCloseAsync(timeout.Token);
            Assert.False(window.IsOpen);
            Assert.DoesNotContain(diagnostics, static diagnostic =>
                diagnostic.Code is "client-not-bound" or "browser-launch-conflict" or "presentation-browser-handed-off");
        }
        finally
        {
            await BrowserTestProfile.DeleteAsync(folder.FullName, TimeSpan.FromSeconds(10));
        }
    }

    // #1097 on a Windows runner: with __COMPAT_LAYER=Installer inherited, Edge opens the window itself instead of
    // handing off, and the window connects.
    [Test]
    public async Task EdgeOpensAWindowWithACompatibilityLayerInTheEnvironment()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64
            || !WebUiApplication.BrowserExists(WebUiBrowser.Edge))
        {
            return;
        }

        var saved = Environment.GetEnvironmentVariable(WebUiBrowserHost.CompatLayerVariable);
        Environment.SetEnvironmentVariable(WebUiBrowserHost.CompatLayerVariable, "Installer");
        try
        {
            var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                ConnectionTimeout = TestTimeouts.Scale(TimeSpan.FromSeconds(30)),
                DiagnosticSink = diagnostics.Enqueue,
            });
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Html("<!doctype html><title>edge</title><script src=\"webui.js\"></script>"),
            });

            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Edge });

            using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(30));
            while (surface.ConnectionCount == 0) await Task.Delay(20, timeout.Token);
            Assert.True(window.IsOpen);
            // Edge kept the process Runic started: it did not hand off.
            Assert.True(window.ProcessId > 0);
            Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Code == "presentation-browser-handed-off");
            Assert.Equal("Installer", Environment.GetEnvironmentVariable(WebUiBrowserHost.CompatLayerVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(WebUiBrowserHost.CompatLayerVariable, saved);
        }
    }

    // A browser profile of its own: its cookie jar, without following the launch address's redirect.
    private static HttpClient Client() =>
        new(new SocketsHttpHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false });

    private static WebUiBrowserLaunchOptions Options(IReadOnlyList<string> browserArguments) =>
        new(null, @"C:\profile", null, browserArguments, false, false, null, null, null, null);

    // A DesktopHost surface opening a window in a Chrome stand-in on a manual clock. The stand-in records its command
    // line and exits at once (a hand-off), or once the test releases it (an exit after the launch request).
    private sealed class HandOffFixture : IAsyncDisposable
    {
        internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private readonly DirectoryInfo _folder;
        private readonly DesktopHost _host;
        private readonly DesktopSurface _surface;

        private HandOffFixture(DirectoryInfo folder, DesktopHost host, DesktopSurface surface, Task<DesktopWindow> opening,
            Uri launchUrl, ManualTime time, ConcurrentQueue<DesktopDiagnostic> diagnostics)
        {
            _folder = folder;
            _host = host;
            _surface = surface;
            Opening = opening;
            LaunchUrl = launchUrl;
            Time = time;
            Diagnostics = diagnostics;
        }

        public Task<DesktopWindow> Opening { get; }

        public Uri LaunchUrl { get; }

        public ManualTime Time { get; }

        public ConcurrentQueue<DesktopDiagnostic> Diagnostics { get; }

        public int Launches => File.ReadAllLines(Path.Combine(_folder.FullName, "launches")).Length;

        public void ReleaseGate() => File.WriteAllText(Path.Combine(_folder.FullName, "gate"), string.Empty);

        [UnsupportedOSPlatform("windows")]
        public static async Task<HandOffFixture> StartAsync(bool exitAfterGate)
        {
            var folder = Directory.CreateTempSubdirectory("runic-desktop-handoff-");
            var arguments = Path.Combine(folder.FullName, "arguments");
            var launches = Path.Combine(folder.FullName, "launches");
            var gate = Path.Combine(folder.FullName, "gate");
            var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
            var time = new ManualTime();
            DesktopHost? host = null;
            try
            {
                var executable = Path.Combine(folder.FullName, OperatingSystem.IsMacOS()
                    ? Path.Combine("Google Chrome.app", "Contents", "MacOS", "Google Chrome")
                    : "google-chrome");
                Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
                var wait = exitAfterGate ? $"while [ ! -f '{gate}' ]; do sleep 0.02; done" : string.Empty;
                await File.WriteAllTextAsync(executable, $$"""
                    #!/bin/sh
                    printf '%s\n' "$@" > '{{arguments}}.tmp'
                    mv '{{arguments}}.tmp' '{{arguments}}'
                    printf 'launch\n' >> '{{launches}}'
                    {{wait}}
                    exit 0
                    """);
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                host = await DesktopHost.StartAsync(new DesktopHostOptions
                {
                    BrowserFolder = folder.FullName,
                    ConnectionTimeout = Timeout,
                    BrowserLaunchAttempts = 2,
                    DiagnosticSink = diagnostics.Enqueue,
                    TimeProvider = time,
                });
                var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
                {
                    Content = new DesktopContent.Html("<!doctype html><script src=\"webui.js\"></script>"),
                });
                var opening = surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Chrome }).AsTask();
                using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
                while (!File.Exists(arguments))
                {
                    await Task.Delay(20, timeout.Token);
                }
                var app = (await File.ReadAllLinesAsync(arguments, timeout.Token))
                    .Single(static line => line.StartsWith("--app=", StringComparison.Ordinal));
                return new HandOffFixture(folder, host, surface, opening, new Uri(app["--app=".Length..]), time, diagnostics);
            }
            catch
            {
                if (host is not null) await host.DisposeAsync();
                folder.Delete(recursive: true);
                throw;
            }
        }

        // The window's engine recorded that it took the exit for a hand-off.
        public async Task WaitForHandOffAsync(CancellationToken cancellationToken)
        {
            var engine = (WebUiWindow)typeof(DesktopSurface)
                .GetField("_engine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(_surface)!;
            while (!engine.HandshakeLog.Describe().Contains("waiting for the copy it handed off to", StringComparison.Ordinal))
            {
                if (Opening.IsCompleted) await Opening;
                await Task.Delay(20, cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _surface.DisposeAsync();
                await _host.DisposeAsync();
                try
                {
                    await Opening;
                }
                catch (Exception exception) when (exception is DesktopException or ObjectDisposedException or OperationCanceledException)
                {
                }
            }
            finally
            {
                _folder.Delete(recursive: true);
            }
        }
    }
}
