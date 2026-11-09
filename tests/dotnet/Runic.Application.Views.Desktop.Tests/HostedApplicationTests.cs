using System.ComponentModel;
using DisplayNameAttribute = TUnit.Core.DisplayNameAttribute;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;

// The host of hosted applications (IRunicHostedApplications, runic-host/1 window mode) against real guest processes:
// this suite's executable in hosted mode (HostedGuestProgram). The host's windows are headless (HostedWindowHost); the
// native legs check the web views (tests/native/Runic.Desktop.WebViewSmoke --hosted). A guest refuses hosted mode when
// it runs elevated, as in a CI container running as root: each case then checks that refusal instead.
internal sealed class HostedApplicationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static bool Elevated => Environment.IsPrivilegedProcess;

    [Test, NotInParallel(Order = 50)]
    public async Task OpensTheGuestsWindowInAWindowOfTheHostAndClosesItThroughTheGuest()
    {
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var opened = fixture.Next(opened: true);
        var closed = fixture.Next(opened: false);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("main"));
        Check(application.Guest == new RunicGuestInfo("Hosted guest", "1.0.0", HostedGuestProgram.ApplicationId),
            $"The guest named itself {application.Guest}.");
        Check(application.Protocol == 1 && application.Features.Count == 0, "The channel's protocol or features are not the base protocol's.");
        Check(fixture.Applications.Applications.Contains(application), "The running guest is not listed.");

        var window = await opened.WaitScaledAsync(Timeout);
        Check(window.Application == application && window.Type == typeof(ConformanceMainWindow).FullName && !window.GuardsClose && window.Owner is null,
            $"The window does not describe the guest's Window: {window.Type}.");
        Check(application.Windows.Single() == window && window.IsOpen && window.Presentation.Browser == BrowserKind.Embedded,
            "The window is not the guest's one open embedded window.");
        // Window mode (host-protocol.md, Page admission): the guest's launch address, top-level, without the host's
        // credential or origins, in the session's own data store.
        var native = fixture.Factory.Hosts.Single();
        Check(HostedLaunchUrl(native.Url), $"The window did not load the guest's launch address: {native.Url}.");
        Check(native.Options!.EphemeralDataStore is { Length: > 0 }, "The window has no data store of its own.");
        Check(native.Options.DocumentStartScript is null && native.Options.AdditionalOrigins.Count == 0,
            "The window injects a credential or admits other origins.");
        Check(native.Options.CloseRequested is not null, "The window's close does not ask the guest.");

        Check(await window.RequestCloseAsync(), "The guest refused the close.");
        await window.Closed.WaitScaledAsync(Timeout);
        Check(await closed.WaitScaledAsync(Timeout) == window && !native.IsOpen, "The host's window did not close.");
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly after its Window closed.");
        Check(fixture.Applications.Applications.Count == 0, "The guest is still listed after it exited.");
    }

    // The title bar's close of a Window without a guard: the native close waits for the guest's Window to close
    // (close-request, then window-closed) and then completes.
    [Test, NotInParallel(Order = 50)]
    public async Task AUserCloseClosesTheWindowThroughTheGuest()
    {
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("main"));
        var window = await opened.WaitScaledAsync(Timeout);
        var native = fixture.Factory.Hosts.Single();
        native.UserClose();
        await window.Closed.WaitScaledAsync(Timeout);
        Check(!native.IsOpen && !window.Presentation.IsOpen, "The host's window did not close.");
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly after its Window closed.");
    }

    [Test, NotInParallel(Order = 51)]
    public async Task AUserCloseAsksTheGuestsGuardAndAForcedCloseSkipsIt()
    {
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("guarded"));
        var window = await opened.WaitScaledAsync(Timeout);
        Check(window.GuardsClose, "The guarded Window does not guard its close.");
        var native = fixture.Factory.Hosts.Single();

        // The title bar's close: the guest's guard refuses, so the window stays.
        native.UserClose();
        await Task.Delay(TestTimeouts.Scale(TimeSpan.FromMilliseconds(500)));
        Check(window.IsOpen && native.IsOpen, "The window closed although the guest's guard refused.");
        Check(!await window.RequestCloseAsync(), "RequestCloseAsync reported a close the guard refused.");

        Check(await window.RequestCloseAsync(force: true), "The guest refused a forced close.");
        await window.Closed.WaitScaledAsync(Timeout);
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly.");
    }

    [Test, NotInParallel(Order = 52)]
    public async Task ClosesTheWindowsAndReportsTheExitCodeWhenTheGuestCrashes()
    {
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var opened = fixture.Next(opened: true);
        var closed = fixture.Next(opened: false);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("crash"));
        var window = await opened.WaitScaledAsync(Timeout);
        Check(await application.Exited.WaitScaledAsync(Timeout) == HostedGuestProgram.CrashExitCode, "Exited did not report the crash's exit code.");
        Check(window.Closed.IsCompleted && !window.IsOpen && !fixture.Factory.Hosts.Single().IsOpen, "The guest's window is still open.");
        Check(await closed.WaitScaledAsync(Timeout) == window, "WindowClosed was not raised.");
        Check(application.Windows.Count == 0 && fixture.Applications.Applications.Count == 0, "The crashed guest is still listed.");
    }

    [Test, NotInParallel(Order = 53)]
    public async Task StopAsksTheGuestToShutDown()
    {
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("guarded"));
        var window = await opened.WaitScaledAsync(Timeout);
        // shutdown closes the Windows without asking their guards.
        Check(await application.StopAsync().AsTask().WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly after shutdown.");
        Check(window.Closed.IsCompleted, "The window is still open after the guest stopped.");
        Check(await application.StopAsync() == 0, "A second stop did not report the exit code.");
    }

    [Test, NotInParallel(Order = 54)]
    public async Task RequestStopAsksTheWindowsFirst()
    {
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("guarded"));
        var window = await opened.WaitScaledAsync(Timeout);
        Check(!await application.RequestStopAsync().AsTask().WaitScaledAsync(Timeout), "The guest stopped although its guard refused.");
        Check(window.IsOpen && !application.Exited.IsCompleted, "The guest went away although its guard refused.");
        Check(await application.KillAsync().AsTask().WaitScaledAsync(Timeout) != 0, "A killed guest reported a clean exit.");
        Check(window.Closed.IsCompleted, "The killed guest's window is still open.");
    }

    [Test, NotInParallel(Order = 55)]
    public async Task TheHostsShutdownStopsItsGuests()
    {
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("main"));
        var window = await opened.WaitScaledAsync(Timeout);
        fixture.Desktop.RequestShutdown("The test stops the host.");
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly when the host shut down.");
        Check(window.Closed.IsCompleted, "The guest's window is still open.");
        await Throws<InvalidOperationException>(() => fixture.Applications.StartAsync(HostedGuestProgram.Guest("main")).AsTask());
    }

    [Test, NotInParallel(Order = 56)]
    public async Task TheEndOfRunStopsTheGuests()
    {
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("main"));
        _ = await opened.WaitScaledAsync(Timeout);
        await fixture.Host.StopHostedApplicationsAsync().WaitScaledAsync(Timeout);
        Check(application.Exited.IsCompletedSuccessfully && application.Exited.Result == 0, "The guest did not stop with the host's Run.");
    }

    [Test, NotInParallel(Order = 57)]
    public async Task AnOwnedWindowIsOwnedByTheGuestsOtherWindow()
    {
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var windows = new List<RunicHostedWindow>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Applications.WindowOpened += (_, e) =>
        {
            lock (windows)
            {
                windows.Add(e.Window);
                if (windows.Count == 2) both.TrySetResult();
            }
        };
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("owned"));
        await both.Task.WaitScaledAsync(Timeout);
        var main = windows.Single(window => window.Owner is null);
        var owned = windows.Single(window => window.Owner is not null);
        Check(owned.Owner == main && owned.IsModal, "The owned window is not the main window's modal window.");
        var stores = fixture.Factory.Hosts.Select(host => host.Options!.EphemeralDataStore).Distinct().ToArray();
        Check(stores is [{ Length: > 0 }], "The guest's windows do not share the session's data store.");
        Check(await main.RequestCloseAsync(), "The guest refused the close.");
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly.");
        Check(owned.Closed.IsCompleted, "The owned window is still open.");
    }

    [Test, NotInParallel(Order = 58)]
    public async Task ThePolicyRefusesAWindow()
    {
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var policy = new RecordingPolicy { RefuseWindows = true };
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("refused"), new RunicHostedApplicationOptions { Policy = policy });
        Check(await application.Exited.WaitScaledAsync(Timeout) == HostedGuestProgram.RefusedExitCode, "The guest opened a window the policy refused.");
        Check(policy.Windows.Single() is { Type: var type, WindowOptions.Browser: BrowserKind.Embedded } && type == typeof(ConformanceMainWindow).FullName,
            "The policy did not see the guest's request.");
        Check(fixture.Factory.Hosts.Count == 0, "The host opened a window the policy refused.");
        Check(fixture.Logs.Entries.Any(entry => entry.EventId == 2021 && entry.Message.Contains("OPEN hosted-window-refused", StringComparison.Ordinal)),
            "The guest's open did not fail with hosted-window-refused.");
    }

    [Test, NotInParallel(Order = 59)]
    public async Task ThePolicyDecidesPresentationRequests()
    {
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var opened = fixture.Next(opened: true);
        var policy = new RecordingPolicy { Refused = RunicHostedPresentationKind.Minimize };
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("present"), new RunicHostedApplicationOptions { Policy = policy });
        var window = await opened.WaitScaledAsync(Timeout);
        Check(await TestTimeouts.UntilAsync(() => fixture.GuestErrors.Contains("PRESENTED"), Timeout), "The guest did not finish its requests.");
        var errors = fixture.GuestErrors;
        Check(errors.Contains("OUTCOME present success"), $"present did not succeed:\n{errors}");
        Check(errors.Contains("OUTCOME minimize failed PermissionDenied"), $"The policy's refusal did not reach the guest:\n{errors}");
        Check(errors.Contains("OUTCOME visible success"), $"set-visible did not succeed:\n{errors}");
        // The headless window cannot enter full screen, so its window-open reply did not list the feature.
        Check(errors.Contains("OUTCOME full-screen unavailable BackendUnavailable"), $"set-full-screen was not unavailable:\n{errors}");
        Check(policy.Presented.Contains(RunicHostedPresentationKind.Present) && policy.Presented.Contains(RunicHostedPresentationKind.SetVisible),
            "The policy did not see the requests.");
        Check(fixture.Factory.Hosts.Single().Focused > 0, "present did not focus the window.");
        Check(await window.RequestCloseAsync(), "The guest refused the close.");
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly.");
    }

    [Test, NotInParallel(Order = 60)]
    public async Task AGuestThatWritesNoChannelLineTimesOutAndIsEnded()
    {
        await using var fixture = await HostFixture.StartAsync();
        var watch = Stopwatch.StartNew();
        var failure = await StartFailureAsync(fixture, HostedGuestProgram.Guest("sleep"), new RunicHostedApplicationOptions { StartTimeout = TimeSpan.FromSeconds(2) });
        Check(failure.Code == "guest-start-timeout", $"The start failed with {failure.Code}: {failure.Message}");
        Check(watch.Elapsed < TestTimeouts.Scale(TimeSpan.FromSeconds(20)), $"The start took {watch.Elapsed}.");
        Check(fixture.Logs.Entries.Any(entry => entry.EventId == 2015), "The failed start was not logged.");
    }

    [Test, NotInParallel(Order = 61)]
    public async Task AGuestThatExitsBeforeItsChannelLineFailsTheStart()
    {
        await using var fixture = await HostFixture.StartAsync();
        var failure = await StartFailureAsync(fixture, HostedGuestProgram.Guest("exit"), null);
        Check(failure.Code == "guest-exited", $"The start failed with {failure.Code}: {failure.Message}");
    }

    [Test, NotInParallel(Order = 62)]
    public async Task AMissingExecutableFailsTheStart()
    {
        await using var fixture = await HostFixture.StartAsync();
        await Throws<Win32Exception>(() => fixture.Applications.StartAsync(
            new RunicGuestStartInfo(Path.Combine(AppContext.BaseDirectory, "no-such-guest"), [])).AsTask());
    }

    [Test, NotInParallel(Order = 63)]
    public async Task AHostedOrServeOnlyApplicationCannotHost()
    {
        await using var fixture = await HostFixture.StartAsync(serveOnly: true);
        await Throws<InvalidOperationException>(() => fixture.Applications.StartAsync(HostedGuestProgram.Guest("main")).AsTask());
    }

    // The guest leads a process group of its own on Linux (posix_spawn with POSIX_SPAWN_SETPGROUP), and the start
    // information's environment reaches it without the serve-only switches.
    [Test, NotInParallel(Order = 64)]
    public async Task TheGuestRunsInAProcessGroupOfItsOwn()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("main") with
        {
            Environment = new Dictionary<string, string?> { ["RUNIC_APPLICATION_SERVE_ONLY"] = "1", ["RUNIC_TEST_HOSTED"] = "yes" },
        });
        var window = await opened.WaitScaledAsync(Timeout);
        var stat = File.ReadAllText($"/proc/{application.ProcessId}/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        Check(int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture) == application.ProcessId,
            $"The guest's process group is {fields[2]}, not its own {application.ProcessId}.");
        var environment = File.ReadAllText($"/proc/{application.ProcessId}/environ").Split('\0');
        Check(environment.Contains("RUNIC_TEST_HOSTED=yes") && !environment.Any(variable => variable.StartsWith("RUNIC_APPLICATION_SERVE_ONLY=", StringComparison.Ordinal)),
            "The guest's environment is not the start information's without the serve-only switches.");
        Check(await window.RequestCloseAsync(), "The guest refused the close.");
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly.");
    }

    [Test, NotInParallel(Order = 65)]
    public void TheGuestsEnvironmentDropsTheServeOnlySwitches()
    {
        var environment = GuestProcess.Environment(new Dictionary<string, string?>
        {
            ["RUNIC_APPLICATION_CLOSE_AFTER_OPEN"] = "1",
            ["RUNIC_TEST_ADDED"] = "1",
            ["PATH"] = null,
        });
        Check(!environment.ContainsKey("RUNIC_APPLICATION_CLOSE_AFTER_OPEN") && !environment.ContainsKey("RUNIC_APPLICATION_SERVE_ONLY"),
            "The serve-only switches reach the guest.");
        Check(environment["RUNIC_TEST_ADDED"] == "1" && !environment.ContainsKey("PATH"), "The start information's changes were not applied.");
    }

    [Test, NotInParallel(Order = 66)]
    [Arguments("http://127.0.0.1:41000/_runic_launch/abc", true)]
    [Arguments("http://[::1]:41000/surface-1/_runic_launch/abc", true)]
    [Arguments("http://localhost:41000/_runic_launch/abc", false)]
    [Arguments("http://192.168.1.2:41000/_runic_launch/abc", false)]
    [Arguments("https://127.0.0.1:41000/_runic_launch/abc", false)]
    [Arguments("http://127.0.0.1/_runic_launch/abc", false)]
    [Arguments("http://127.0.0.1:41000/_runic_launch/", false)]
    [Arguments("http://127.0.0.1:41000/page?_runic_launch/abc", false)]
    [Arguments("http://user@127.0.0.1:41000/_runic_launch/abc", false)]
    [DisplayName("LaunchUrl($url, $valid)")]
    public void OnlyALoopbackLaunchAddressOpens(string url, bool valid) =>
        Check((RunicHostedApplication.LaunchUrl(url) is not null) == valid, $"{url} was {(valid ? "refused" : "accepted")}.");

    internal static bool HostedLaunchUrl(Uri? url) =>
        url is { Scheme: "http", Host: "127.0.0.1" or "[::1]", IsDefaultPort: false } &&
        url.AbsolutePath.Contains("/_runic_launch/", StringComparison.Ordinal);

    private static async Task<RunicHostedApplicationException> StartFailureAsync(HostFixture fixture, RunicGuestStartInfo start,
        RunicHostedApplicationOptions? options)
    {
        try
        {
            await using var application = await fixture.Applications.StartAsync(start, options);
        }
        catch (RunicHostedApplicationException error) { return error; }
        throw new InvalidOperationException("The guest was hosted.");
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"No {typeof(T).Name} was thrown.");
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecordingPolicy : IRunicHostedWindowPolicy
    {
        public bool RefuseWindows { get; init; }
        public RunicHostedPresentationKind? Refused { get; init; }
        public List<RunicHostedWindowRequest> Windows { get; } = [];
        public List<RunicHostedPresentationKind> Presented { get; } = [];

        public ValueTask<RunicHostedWindowDecision> OpenWindowAsync(RunicHostedWindowRequest request, CancellationToken cancellationToken)
        {
            lock (Windows) Windows.Add(request);
            return ValueTask.FromResult(RefuseWindows ? RunicHostedWindowDecision.Refuse("The test policy refuses windows.") : RunicHostedWindowDecision.Allow());
        }

        public RunicHostedPresentationDecision Present(RunicHostedPresentationRequest request)
        {
            lock (Presented) Presented.Add(request.Kind);
            return request.Kind == Refused ? RunicHostedPresentationDecision.Refuse("The test policy refuses it.") : RunicHostedPresentationDecision.Allow();
        }
    }
}

// A host application on a headless Desktop host.
internal sealed class HostFixture : IAsyncDisposable
{
    private HostFixture(ServiceProvider provider, DesktopHost desktop, RunicDesktopHost host, HostedWindowFactory factory, RecordingLoggerFactory logs)
    {
        Provider = provider;
        Desktop = desktop;
        Host = host;
        Factory = factory;
        Logs = logs;
    }

    public ServiceProvider Provider { get; }
    public DesktopHost Desktop { get; }
    public RunicDesktopHost Host { get; }
    public HostedWindowFactory Factory { get; }
    public RecordingLoggerFactory Logs { get; }
    public IRunicHostedApplications Applications => Host.HostedApplications;

    // What the guests wrote to standard error, which the host logs (event 2021).
    public string GuestErrors => string.Join('\n', Logs.Entries.Where(static entry => entry.EventId == 2021).Select(static entry => entry.Message));

    public static async Task<HostFixture> StartAsync(bool serveOnly = false)
    {
        var logs = new RecordingLoggerFactory();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(logs);
        services.AddRunicHostedApplications();
        var provider = services.BuildServiceProvider();
        var factory = new HostedWindowFactory();
        var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        var host = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions { ServeOnly = serveOnly });
        return new HostFixture(provider, desktop, host, factory, logs);
    }

    // The next window that opens or closes.
    public Task<RunicHostedWindow> Next(bool opened)
    {
        var next = new TaskCompletionSource<RunicHostedWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<RunicHostedWindowEventArgs> handler = (_, e) => next.TrySetResult(e.Window);
        if (opened) Applications.WindowOpened += handler;
        else Applications.WindowClosed += handler;
        return next.Task;
    }

    // An elevated test process starts elevated guests, which refuse hosted mode (host-protocol.md, Refusal line).
    public async Task<bool> RefusedAsElevatedAsync()
    {
        if (!Environment.IsPrivilegedProcess) return false;
        try
        {
            await using var application = await Applications.StartAsync(HostedGuestProgram.Guest("main"));
        }
        catch (RunicHostedApplicationException error) when (error.Code == "guest-refused" && error.GuestCode == "guest-elevated")
        {
            return true;
        }
        throw new InvalidOperationException("An elevated guest was hosted.");
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopHostedApplicationsAsync();
        await Desktop.DisposeAsync();
        await Provider.DisposeAsync();
    }
}

internal sealed class HostedWindowFactory : IDesktopWindowHostFactory
{
    private readonly List<HostedWindowHost> _hosts = [];

    public IReadOnlyList<HostedWindowHost> Hosts
    {
        get { lock (_hosts) return [.. _hosts]; }
    }

    public bool IsSupported => true;
    public DesktopWindowCapabilities Capabilities => HostedWindowHost.SupportedCapabilities;

    public IDesktopWindowHost Create()
    {
        var host = new HostedWindowHost();
        lock (_hosts) _hosts.Add(host);
        return host;
    }
}

// A headless embedded window host that honours the ephemeral data store of hosted windows and records how it opened.
// UserClose is the title-bar close button.
internal sealed class HostedWindowHost : IDesktopWindowHost, IDesktopEphemeralDataStoreWindowHost
{
    internal const DesktopWindowCapabilities SupportedCapabilities = DesktopWindowCapabilities.Focus | DesktopWindowCapabilities.Minimize |
        DesktopWindowCapabilities.Maximize | DesktopWindowCapabilities.Resize | DesktopWindowCapabilities.Move |
        DesktopWindowCapabilities.Visibility | DesktopWindowCapabilities.CloseConfirmation;

    private volatile bool _open;
    private int _focused;

    public Uri? Url { get; private set; }
    public DesktopWindowHostOptions? Options { get; private set; }
    public int Focused => Volatile.Read(ref _focused);
    public DesktopWindowCapabilities Capabilities => SupportedCapabilities;
    public bool SupportsCloseConfirmation => true;
    public event EventHandler? Closed;
    public bool IsOpen => _open;
    public nint NativeHandle => 0;

    public void UserClose()
    {
        if (Options?.CloseRequested is { } closeRequested) closeRequested();
        else CloseAsync().AsTask().GetAwaiter().GetResult();
    }

    public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
    {
        Url = url;
        Options = options;
        _open = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_open)
        {
            _open = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask FocusAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _focused);
        return ValueTask.CompletedTask;
    }

    public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _open = false;
        return ValueTask.CompletedTask;
    }
}
