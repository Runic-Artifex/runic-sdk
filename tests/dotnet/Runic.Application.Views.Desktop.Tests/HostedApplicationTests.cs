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
        Check(fixture.Logs.Entries.Any(entry => entry.EventId == 2022 && entry.Message.Contains("OPEN hosted-window-refused", StringComparison.Ordinal)),
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
        Check(fixture.Logs.Entries.Any(entry => entry.EventId == 2016), "The failed start was not logged.");
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
        var failure = await StartFailureAsync(fixture, new RunicGuestStartInfo(Path.Combine(AppContext.BaseDirectory, "no-such-guest"), []), null);
        Check(failure.Code == "guest-start-failed" && failure.InnerException is Win32Exception, $"The start failed with {failure.Code}: {failure.Message}");
        failure = await StartFailureAsync(fixture, new RunicGuestStartInfo("runic-no-such-guest-in-path", []), null);
        Check(failure.Code == "guest-start-failed", $"A name not in PATH failed with {failure.Code}: {failure.Message}");
        Check(fixture.Logs.Entries.Any(entry => entry.EventId == 2016), "The failed start was not logged.");
    }

    // CreateProcess runs a batch file through cmd.exe, whose parsing no argument quoting protects (BatBadBut).
    [Test, NotInParallel(Order = 62)]
    public async Task ABatchFileGuestIsRefusedOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await HostFixture.StartAsync();
        var directory = Directory.CreateTempSubdirectory("runic-hosted-batch-");
        try
        {
            foreach (var name in new[] { "guest.bat", "guest.CMD" })
            {
                var path = Path.Combine(directory.FullName, name);
                await File.WriteAllTextAsync(path, "@echo off\r\n");
                await Throws<ArgumentException>(() => fixture.Applications.StartAsync(new RunicGuestStartInfo(path, ["a&b"])).AsTask());
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    // M1: the guest goes away while the host opens its window. The window that opened after the guest's end closes at
    // once, without WindowOpened or WindowClosed, and the application ends.
    [Test, NotInParallel(Order = 67)]
    public async Task AWindowThatOpensAfterTheGuestEndedIsClosed()
    {
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var events = new List<string>();
        fixture.Applications.WindowOpened += (_, e) => { lock (events) events.Add("opened " + e.Window.Id); };
        fixture.Applications.WindowClosed += (_, e) => { lock (events) events.Add("closed " + e.Window.Id); };
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Factory.OpenGate = () =>
        {
            opening.TrySetResult();
            return release.Task;
        };
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("main"));
        await opening.Task.WaitScaledAsync(Timeout);
        // The guest ends while its window is opening; the host has seen it go before the open completes.
        Check(await application.KillAsync().AsTask().WaitScaledAsync(Timeout) != 0, "A killed guest reported a clean exit.");
        release.TrySetResult();
        var native = fixture.Factory.Hosts.Single();
        Check(await TestTimeouts.UntilAsync(() => !native.IsOpen && native.Options is not null, Timeout), "The window that opened after the guest ended is open.");
        await Task.Delay(TestTimeouts.Scale(TimeSpan.FromMilliseconds(500)));
        Check(application.Windows.Count == 0, "The guest that ended lists a window.");
        lock (events) Check(events.Count == 0, $"Events were raised for a window that never opened for the host: {string.Join(", ", events)}.");
    }

    // L5: a Window that closes as soon as it opened raises WindowOpened, then WindowClosed.
    [Test, NotInParallel(Order = 68)]
    public async Task WindowClosedFollowsWindowOpened()
    {
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var events = new List<string>();
        var closed = fixture.Next(opened: false);
        fixture.Applications.WindowOpened += (_, e) =>
        {
            // A slow handler: WindowClosed still waits for it.
            Thread.Sleep(200);
            lock (events) events.Add("opened " + e.Window.Id);
        };
        fixture.Applications.WindowClosed += (_, e) => { lock (events) events.Add("closed " + e.Window.Id); };
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("flash"));
        var window = await closed.WaitScaledAsync(Timeout);
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly.");
        await Task.Delay(TestTimeouts.Scale(TimeSpan.FromMilliseconds(300)));
        lock (events)
            Check(events.SequenceEqual(["opened " + window.Id, "closed " + window.Id]), $"The events were {string.Join(", ", events)}.");
    }

    // M2: a guest's media-capture request is granted only as far as the host grants its own windows, and a policy can
    // grant more.
    [Test, NotInParallel(Order = 69)]
    public async Task AGuestGetsOnlyThePermissionsTheHostGrants()
    {
        // A host whose windows are granted nothing: the request is not granted.
        await using (var fixture = await HostFixture.StartAsync())
        {
            if (await fixture.RefusedAsElevatedAsync()) return;
            var policy = new RecordingPolicy();
            var opened = fixture.Next(opened: true);
            var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("media"), new RunicHostedApplicationOptions { Policy = policy });
            var window = await opened.WaitScaledAsync(Timeout);
            var request = policy.Windows.Single();
            Check(request.Permissions == DesktopPermissionGrant.MediaCapture, $"The guest asked for {request.Permissions}.");
            Check(request.WindowOptions.AllowedPermissions == DesktopPermissionGrant.None, $"The request grants {request.WindowOptions.AllowedPermissions}.");
            Check(fixture.Factory.Hosts.Single().Options!.AllowedPermissions == DesktopPermissionGrant.None, "The window was granted the camera and microphone.");
            Check(await window.RequestCloseAsync(), "The guest refused the close.");
            Check(await application.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly.");

            // The policy grants it explicitly.
            policy = new RecordingPolicy { Grant = DesktopPermissionGrant.MediaCapture };
            opened = fixture.Next(opened: true);
            application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("media"), new RunicHostedApplicationOptions { Policy = policy });
            window = await opened.WaitScaledAsync(Timeout);
            Check(fixture.Factory.Hosts[^1].Options!.AllowedPermissions == DesktopPermissionGrant.MediaCapture, "The policy's grant did not apply.");
            Check(await window.RequestCloseAsync(), "The guest refused the close.");
            Check(await application.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly.");
        }

        // A host that grants its own windows the camera and microphone grants the guest's request.
        await using (var fixture = await HostFixture.StartAsync(windowOptions: RunicDesktopHost.DefaultWindowOptions with { AllowedPermissions = DesktopPermissionGrant.MediaCapture }))
        {
            var opened = fixture.Next(opened: true);
            var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("main"));
            var window = await opened.WaitScaledAsync(Timeout);
            Check(fixture.Factory.Hosts.Single().Options!.AllowedPermissions == DesktopPermissionGrant.None, "A guest that asked for nothing was granted a permission.");
            Check(await window.RequestCloseAsync(), "The guest refused the close.");
            Check(await application.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly.");
            opened = fixture.Next(opened: true);
            application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("media"));
            window = await opened.WaitScaledAsync(Timeout);
            Check(fixture.Factory.Hosts[^1].Options!.AllowedPermissions == DesktopPermissionGrant.MediaCapture, "The host's own grant did not reach the guest.");
            Check(await window.RequestCloseAsync(), "The guest refused the close.");
            Check(await application.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly.");
        }
    }

    // M5: disposing the host's applications, as the end of Run does, or the host's shutdown, while a start waits for
    // the guest's handshake fails the start at once and ends the guest.
    [Test, NotInParallel(Order = 70)]
    [Arguments(false)]
    [Arguments(true)]
    [DisplayName("A pending start ends with the host (shutdown: $shutdown)")]
    public async Task APendingStartEndsWithTheHost(bool shutdown)
    {
        await using var fixture = await HostFixture.StartAsync();
        var watch = Stopwatch.StartNew();
        var start = fixture.Applications.StartAsync(HostedGuestProgram.Guest("sleep"), new RunicHostedApplicationOptions { StartTimeout = TimeSpan.FromMinutes(2) }).AsTask();
        Check(await TestTimeouts.UntilAsync(() => GuestPid(fixture) is not null, Timeout), "The guest did not start.");
        var pid = GuestPid(fixture)!.Value;
        if (shutdown) fixture.Desktop.RequestShutdown("The test stops the host.");
        else await fixture.Host.StopHostedApplicationsAsync().WaitScaledAsync(Timeout);
        await Throws<InvalidOperationException>(() => start.WaitScaledAsync(Timeout));
        Check(watch.Elapsed < TestTimeouts.Scale(TimeSpan.FromSeconds(30)), $"The start took {watch.Elapsed}.");
        Check(await TestTimeouts.UntilAsync(() => ProcessGone(pid), Timeout), $"The guest {pid} still runs.");
        Check(fixture.Applications.Applications.Count == 0, "A guest is listed.");
    }

    // The guest's own processes end with it: through its job object on Windows, its process group elsewhere.
    [Test, NotInParallel(Order = 71)]
    public async Task KillingTheGuestEndsTheProcessesItStarted()
    {
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("grandchild"));
        _ = await opened.WaitScaledAsync(Timeout);
        Check(await TestTimeouts.UntilAsync(() => Grandchild(fixture) is not null, Timeout), "The guest did not report its child.");
        var grandchild = Grandchild(fixture)!.Value;
        Check(!ProcessGone(grandchild), "The guest's child is not running.");
        _ = await application.KillAsync().AsTask().WaitScaledAsync(Timeout);
        Check(await TestTimeouts.UntilAsync(() => ProcessGone(grandchild), Timeout), $"The guest's child {grandchild} outlived it.");
    }

    // H1: the same-user check against this test process, the real parent of the child that runs it: on Windows the
    // token users and the parent's creation time, on Linux /proc, on macOS proc_pidinfo.
    [Test, NotInParallel(Order = 72)]
    public async Task TheSameUserCheckPassesForAChildOfTheSameUser()
    {
        var start = HostedGuestProgram.Guest("same-user");
        var info = new ProcessStartInfo(start.FileName) { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var argument in start.Arguments) info.ArgumentList.Add(argument);
        using var child = Process.Start(info)!;
        var output = await child.StandardOutput.ReadToEndAsync().WaitScaledAsync(Timeout);
        await child.WaitForExitAsync().WaitScaledAsync(Timeout);
        Check(output.Contains("SAME-USER True", StringComparison.Ordinal), $"The child's check reported: {output}");
    }

    private static int? GuestPid(HostFixture fixture) => LoggedNumber(fixture, "PID ");

    private static int? Grandchild(HostFixture fixture) => LoggedNumber(fixture, "GRANDCHILD ");

    private static int? LoggedNumber(HostFixture fixture, string prefix)
    {
        foreach (var entry in fixture.Logs.Entries.Where(static entry => entry.EventId == 2022))
        {
            var index = entry.Message.IndexOf(prefix, StringComparison.Ordinal);
            if (index >= 0 && int.TryParse(entry.Message[(index + prefix.Length)..].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var value))
                return value;
        }
        return null;
    }

    // Exited, or a zombie that only waits for its reaper (Linux).
    private static bool ProcessGone(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                return stat[(stat.LastIndexOf(')') + 2)] is 'Z' or 'X';
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return true; }
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
        public DesktopPermissionGrant? Grant { get; init; }
        public RunicHostedPresentationKind? Refused { get; init; }
        public List<RunicHostedWindowRequest> Windows { get; } = [];
        public List<RunicHostedPresentationKind> Presented { get; } = [];

        public ValueTask<RunicHostedWindowDecision> OpenWindowAsync(RunicHostedWindowRequest request, CancellationToken cancellationToken)
        {
            lock (Windows) Windows.Add(request);
            return ValueTask.FromResult(RefuseWindows ? RunicHostedWindowDecision.Refuse("The test policy refuses windows.")
                : Grant is { } grant ? RunicHostedWindowDecision.Allow(request.WindowOptions with { AllowedPermissions = grant })
                : RunicHostedWindowDecision.Allow());
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

    // What the guests wrote to standard error, which the host logs (event 2022).
    public string GuestErrors => string.Join('\n', Logs.Entries.Where(static entry => entry.EventId == 2022).Select(static entry => entry.Message));

    public static async Task<HostFixture> StartAsync(bool serveOnly = false, DesktopWindowOptions? windowOptions = null)
    {
        var logs = new RecordingLoggerFactory();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(logs);
        services.AddRunicHostedApplications();
        var provider = services.BuildServiceProvider();
        var factory = new HostedWindowFactory();
        var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        var host = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
        {
            ServeOnly = serveOnly,
            WindowOptions = windowOptions ?? RunicDesktopHost.DefaultWindowOptions,
        });
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

    // Holds each window's open until it completes, such as while the test ends the guest.
    public Func<Task>? OpenGate { get; set; }

    public IDesktopWindowHost Create()
    {
        var host = new HostedWindowHost(this);
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

    private readonly HostedWindowFactory? _factory;
    private volatile bool _open;
    private int _focused;

    public HostedWindowHost(HostedWindowFactory? factory = null) => _factory = factory;

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

    public async ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
    {
        Url = url;
        Options = options;
        if (_factory?.OpenGate is { } gate) await gate();
        _open = true;
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
