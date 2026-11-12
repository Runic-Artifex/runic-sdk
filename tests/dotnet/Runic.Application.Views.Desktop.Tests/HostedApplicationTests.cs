using System.Collections.Immutable;
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
// it runs elevated: an elevated developer run checks that refusal and skips the case, under CI it fails the case
// (HostFixture.RequireUnelevatedAsync).
internal sealed class HostedApplicationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Test, NotInParallel(Order = 50)]
    public async Task OpensTheGuestsWindowInAWindowOfTheHostAndClosesItThroughTheGuest()
    {
        await using var fixture = await HostFixture.StartAsync();
        await fixture.RequireUnelevatedAsync();
        var opened = fixture.Next(opened: true);
        var closed = fixture.Next(opened: false);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("main"));
        Check(application.Guest == new RunicGuestInfo("Hosted guest", "1.0.0", HostedGuestProgram.ApplicationId),
            $"The guest named itself {application.Guest}.");
        Check(application.Protocol == 1 && application.Features.SequenceEqual(["menus/1", "dialogs/1", "window-services/1", "region/1"]), $"The channel uses protocol {application.Protocol} with the features {string.Join(", ", application.Features)}.");
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
        await fixture.RequireUnelevatedAsync();
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
        await fixture.RequireUnelevatedAsync();
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
        await fixture.RequireUnelevatedAsync();
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
        await fixture.RequireUnelevatedAsync();
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
        await fixture.RequireUnelevatedAsync();
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
        await fixture.RequireUnelevatedAsync();
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
        await fixture.RequireUnelevatedAsync();
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
        await fixture.RequireUnelevatedAsync();
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
        await fixture.RequireUnelevatedAsync();
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
        await fixture.RequireUnelevatedAsync();
        var opened = fixture.Next(opened: true);
        // Present, minimize, set-visible and set-full-screen all count toward the rate of requests that can activate a
        // window; the guest sends four at once, over the default burst.
        var policy = new RecordingPolicy { Refused = RunicHostedPresentationKind.Minimize, Limits = RunicHostedLimits.Default with { PresentBurst = 8 } };
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("present"), new RunicHostedApplicationOptions { Policy = policy });
        var window = await opened.WaitScaledAsync(Timeout);
        Check(await TestTimeouts.UntilAsync(() => fixture.GuestErrors.Contains("PRESENTED"), Timeout), "The guest did not finish its requests.");
        var errors = fixture.GuestErrors;
        Check(errors.Contains("OUTCOME present success"), $"present did not succeed:\n{errors}");
        Check(errors.Contains("OUTCOME minimize failed PermissionDenied"), $"The policy's refusal did not reach the guest:\n{errors}");
        Check(errors.Contains("OUTCOME visible success"), $"set-visible did not succeed:\n{errors}");
        Check(errors.Contains("OUTCOME full-screen success"), $"set-full-screen did not succeed:\n{errors}");
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

    // The command line CreateProcessW receives: CommandLineToArgvW and the C runtime read the same arguments back.
    [Test, NotInParallel(Order = 62)]
    public void TheWindowsCommandLineQuotesEachArgument()
    {
        if (!OperatingSystem.IsWindows()) return;
        var line = WindowsGuestProcess.CommandLine(@"C:\Program Files\Tool\tool.exe", ["plain", "", "a b", "a\"b", @"a\b", @"a\ b\", "\\\""]);
        const string Expected = "\"C:\\Program Files\\Tool\\tool.exe\" plain \"\" \"a b\" \"a\\\"b\" a\\b \"a\\ b\\\\\" \"\\\\\\\"\"";
        Check(line == Expected, $"The command line is {line}.");
    }

    // M1: the guest goes away while the host opens its window. The window that opened after the guest's end closes at
    // once, without WindowOpened or WindowClosed, and the application ends.
    [Test, NotInParallel(Order = 67)]
    public async Task AWindowThatOpensAfterTheGuestEndedIsClosed()
    {
        await using var fixture = await HostFixture.StartAsync();
        await fixture.RequireUnelevatedAsync();
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
        await fixture.RequireUnelevatedAsync();
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
            await fixture.RequireUnelevatedAsync();
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
        await fixture.RequireUnelevatedAsync();
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

    // N1: a Windows guest inherits its three pipe ends and none of the host's other inheritable handles, here the client
    // end of an anonymous pipe. A process that System.Diagnostics.Process starts inherits every inheritable handle: that
    // it holds this one shows that the check finds an inherited handle.
    [Test, NotInParallel(Order = 73)]
    public async Task AWindowsGuestInheritsNoOtherHandleOfTheHost()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await HostFixture.StartAsync();
        await fixture.RequireUnelevatedAsync();
        using var unrelated = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out, HandleInheritability.Inheritable);
        var inheritable = unrelated.ClientSafePipeHandle;

        var control = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c pause")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using (var child = Process.Start(control)!)
        {
            try { Check(InheritedHandles.Holds(child.Id, inheritable), "A process that System.Diagnostics.Process started does not hold the inheritable handle: the check cannot see an inherited handle."); }
            finally
            {
                child.Kill();
                await child.WaitForExitAsync().WaitScaledAsync(Timeout);
            }
        }

        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("main"));
        var window = await opened.WaitScaledAsync(Timeout);
        Check(!InheritedHandles.Holds(application.ProcessId, inheritable), "The guest inherited a handle of the host other than its pipes.");
        Check(await window.RequestCloseAsync(), "The guest refused the close.");
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly.");
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

    // The guest leads a process group of its own on Linux (posix_spawn with POSIX_SPAWN_SETPGROUP), and the start
    // information's environment reaches it without the serve-only switches.
    [Test, NotInParallel(Order = 64)]
    public async Task TheGuestRunsInAProcessGroupOfItsOwn()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await HostFixture.StartAsync();
        await fixture.RequireUnelevatedAsync();
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
        public RunicHostedLimits Limits { get; init; } = RunicHostedLimits.Default;
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
    private HostFixture(ServiceProvider provider, DesktopHost desktop, RunicDesktopHost host, HostedWindowFactory factory, RecordingLoggerFactory logs,
        HostAppearanceSettings settings)
    {
        Settings = settings;
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

    // The host's desktop appearance, which its guests follow (host-protocol.md, Appearance and locale).
    public HostAppearanceSettings Settings { get; }

    // The host application's locale, which its guests follow.
    public IRunicApplicationLocale Locale => Provider.GetRequiredService<IRunicApplicationLocale>();

    // What the guests wrote to standard error, which the host logs (event 2022).
    public string GuestErrors => string.Join('\n', Logs.Entries.Where(static entry => entry.EventId == 2022).Select(static entry => entry.Message));

    public static async Task<HostFixture> StartAsync(bool serveOnly = false, DesktopWindowOptions? windowOptions = null)
    {
        var logs = new RecordingLoggerFactory();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(logs);
        services.AddRunicHostedApplications();
        // A fixed desktop appearance, so the host's hello and pushes do not depend on the machine's settings.
        var settings = new HostAppearanceSettings();
        services.AddSingleton<IDesktopSettings>(settings);
        services.AddRunicWindowService();
        var provider = services.BuildServiceProvider();
        var factory = new HostedWindowFactory();
        var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        var host = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
        {
            ServeOnly = serveOnly,
            WindowOptions = windowOptions ?? RunicDesktopHost.DefaultWindowOptions,
        });
        return new HostFixture(provider, desktop, host, factory, logs, settings);
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

    // An elevated test process starts elevated guests, which refuse hosted mode (host-protocol.md, Refusal line), so the
    // host cases need a standard user. Under CI (CI=true), and in the relaunch as the standard user
    // (RUNIC_TEST_UNELEVATED=1), an elevated test process fails the case: it would otherwise pass without checking the
    // host. A developer's elevated run checks the refusal and skips the case, with the reason in the report.
    public async Task RequireUnelevatedAsync()
    {
        if (!Environment.IsPrivilegedProcess) return;
        if (Environment.GetEnvironmentVariable("RUNIC_TEST_UNELEVATED") == "1" ||
            string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The host cases run elevated under CI, where they must run as a standard user (the hosted smoke's --run-unelevated): an elevated guest refuses hosted mode, so the case would check nothing.");
        try
        {
            await using var application = await Applications.StartAsync(HostedGuestProgram.Guest("main"));
        }
        catch (RunicHostedApplicationException error) when (error.Code == "guest-refused" && error.GuestCode == "guest-elevated")
        {
            Skip.Test("This test process runs elevated: the guest refused hosted mode, as it must, and the host case did not run. Run the tests as a standard user to check the host.");
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

    // The monitors the host's windows report (window-services/1).
    public FakeMonitors FakeMonitors { get; } = new();
    public IDesktopMonitors Monitors => FakeMonitors;

    // Holds each window's open until it completes, such as while the test ends the guest.
    public Func<Task>? OpenGate { get; set; }

    // Answers each native context menu with the item to choose, or null to dismiss it; null dismisses every menu.
    public Func<ImmutableArray<DesktopMenuEntry>, DesktopContextMenuOptions, CancellationToken, Task<DesktopMenuItem?>>? ContextMenu { get; set; }

    public IDesktopWindowHost Create()
    {
        var host = new HostedWindowHost(this);
        lock (_hosts) _hosts.Add(host);
        return host;
    }

    // The windows in the order they were last activated, the last most recently: as on Windows, when the active window
    // closes or hides, the desktop activates the one before it, such as an owned window's owner.
    private readonly List<HostedWindowHost> _activations = [];

    internal void Activated(HostedWindowHost host)
    {
        lock (_activations)
        {
            _activations.Remove(host);
            _activations.Add(host);
        }
    }

    // Whether the user works in another application. As on macOS 15 and later, the host's requests then do not activate
    // a window: the one asked for last becomes the key window, which is activated once the user switches back to the
    // host application.
    private volatile bool _hostInactive;
    private HostedWindowHost? _keyOnActivation;

    internal bool IsHostInactive => _hostInactive;

    // The user switches to another application; the windows are made inactive by the caller.
    internal void DeactivateHost() => _hostInactive = true;

    // A request to activate the window while the host is not active: it becomes active when the user switches back.
    internal void Defer(HostedWindowHost host)
    {
        lock (_activations) _keyOnActivation = host;
    }

    // The user switches back to the host application: the window to activate, the one asked for last, or else the one
    // active last.
    internal HostedWindowHost? ActivateHost()
    {
        _hostInactive = false;
        lock (_activations)
        {
            var key = _keyOnActivation is { IsOpen: true } deferred ? deferred : _activations.LastOrDefault(static host => host.IsOpen);
            _keyOnActivation = null;
            return key;
        }
    }

    internal void ActivateNext(HostedWindowHost leaving)
    {
        HostedWindowHost? next;
        lock (_activations)
        {
            _activations.Remove(leaving);
            next = _activations.LastOrDefault(static host => host.IsOpen);
        }
        if (next is { IsActive: false }) next.SetActive(true);
    }
}

// A headless embedded window host that honours the ephemeral data store of hosted windows and records how it opened.
// UserClose is the title-bar close button.
internal sealed class HostedWindowHost : IDesktopWindowHost, IDesktopEphemeralDataStoreWindowHost, IDesktopMenuWindowHost, IDesktopActivityWindowHost,
    IDesktopFileDropWindowHost, IDesktopPlacementWindowHost
{
    public event EventHandler<DesktopFileDropEventArgs>? FilesDropped;

    // The user drops files onto the window.
    public void Drop(IReadOnlyList<string> paths, string? target = null) => FilesDropped?.Invoke(this, new DesktopFileDropEventArgs(paths, target));

    internal const DesktopWindowCapabilities SupportedCapabilities = DesktopWindowCapabilities.Focus | DesktopWindowCapabilities.Minimize |
        DesktopWindowCapabilities.Maximize | DesktopWindowCapabilities.Resize | DesktopWindowCapabilities.Move |
        DesktopWindowCapabilities.Visibility | DesktopWindowCapabilities.CloseConfirmation | DesktopWindowCapabilities.MenuBar |
        DesktopWindowCapabilities.ContextMenu | DesktopWindowCapabilities.FullScreen;

    // The window has the keyboard focus from its open until SetActive says otherwise or it closes. Runic Desktop reads it
    // for the host's context menu rule; without the Activity capability the host pushes no activity, which the
    // scenarios do not expect.
    private volatile bool _active = true;

    public DesktopWindowActivity? Activity => new() { IsActive = _active && !_closedOnce, IsVisible = !_closedOnce };

    public event EventHandler? ActivityChanged;

    // Another window takes the focus, or this one gets it back.
    public void SetActive(bool active)
    {
        _active = active;
        if (active) _factory?.Activated(this);
        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsActive => _active && !_closedOnce;

    private volatile bool _closedOnce;

    // The bars the window showed, in order: the native bar it shows now is the last.
    private readonly List<IReadOnlyList<DesktopMenu>> _bars = [];

    // How many times Runic Desktop rebuilt the native bar.
    public int MenuBarRebuilds
    {
        get { lock (_bars) return _bars.Count; }
    }

    // The bar the window showed before its current one, as a native bar that the user still uses during a change.
    public IReadOnlyList<DesktopMenu> PreviousMenuBar
    {
        get { lock (_bars) return _bars.Count >= 2 ? _bars[^2] : []; }
    }

    private IReadOnlyList<DesktopMenu> _menuBar = [];
    private TaskCompletionSource _menuBarChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The native menu bar: the menus Runic Desktop gave the window last, and the command path of their items.
    public IReadOnlyList<DesktopMenu> MenuBar => Volatile.Read(ref _menuBar);
    public IDesktopMenuCommandTarget? MenuCommands { get; set; }
    public DesktopMenuBarPlacement MenuBarPlacement => _open ? DesktopMenuBarPlacement.Window : DesktopMenuBarPlacement.None;
    public event EventHandler? MenuBarPlacementChanged { add { } remove { } }

    public ValueTask SetMenuBarAsync(IReadOnlyList<DesktopMenu> menus, CancellationToken cancellationToken = default)
    {
        lock (_bars) _bars.Add(menus);
        Volatile.Write(ref _menuBar, menus);
        Interlocked.Exchange(ref _menuBarChanged, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        return ValueTask.CompletedTask;
    }

    // Waits until the bar matches.
    public async Task<IReadOnlyList<DesktopMenu>> WaitForMenuBarAsync(Func<IReadOnlyList<DesktopMenu>, bool> match, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(TestTimeouts.Scale(timeout));
        while (true)
        {
            var changed = Volatile.Read(ref _menuBarChanged).Task;
            if (match(MenuBar)) return MenuBar;
            try { await changed.WaitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"The menu bar is {Describe(MenuBar)}.");
            }
        }
    }

    // The item of the bar at a path of titles, such as ("File", "Open…").
    public DesktopMenuItem? Item(params string[] titles) => Item(MenuBar, titles);

    public static DesktopMenuItem? Item(IReadOnlyList<DesktopMenu> bar, params string[] titles)
    {
        IEnumerable<DesktopMenuEntry> entries = bar;
        DesktopMenuEntry? found = null;
        foreach (var title in titles)
        {
            found = entries.FirstOrDefault(entry => entry is DesktopMenu menu && menu.Title == title || entry is DesktopMenuItem item && item.Title == title);
            if (found is DesktopMenu menu) entries = menu.Items;
            else entries = [];
        }
        return found as DesktopMenuItem;
    }

    // Chooses an item as the native bar or its shortcut does: whether its command could run.
    public bool Choose(params string[] titles)
    {
        if (Item(titles) is not { } item || MenuCommands is not { } commands || !item.IsEnabled || !commands.CanExecute(item)) return false;
        commands.Execute(item);
        return true;
    }

    // Chooses an item of the bar the window showed before its current one, as a click on the native bar that the window
    // still showed while the guest's change arrived: the native bar runs the item's command path without asking first.
    // False when that bar had no such enabled item.
    public bool ChoosePrevious(params string[] titles)
    {
        if (Item(PreviousMenuBar, titles) is not { IsEnabled: true } item || MenuCommands is not { } commands) return false;
        commands.Execute(item);
        return true;
    }

    internal static string Describe(IEnumerable<DesktopMenuEntry> entries) => "[" + string.Join(", ", entries.Select(static entry => entry switch
    {
        DesktopMenu menu => $"{menu.Title}{(menu.Role == DesktopMenuRole.None ? "" : $"<{menu.Role}>")}{Describe(menu.Items)}",
        DesktopMenuItem item => $"{item.Title}{(item.IsEnabled ? "" : " (disabled)")}{(item.IsChecked is { } isChecked ? isChecked ? " [x]" : " [ ]" : "")}{(item.Shortcut is { } shortcut ? " " + shortcut : "")}",
        _ => "-",
    })) + "]";

    public ValueTask<DesktopMenuItem?> ShowContextMenuAsync(ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions options,
        CancellationToken cancellationToken = default) =>
        _factory?.ContextMenu is { } choose ? new(choose(items, options, cancellationToken)) : ValueTask.FromResult<DesktopMenuItem?>(null);

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
        if (_factory is { IsHostInactive: true } factory)
        {
            _active = false;
            factory.Defer(this);
        }
        else _factory?.Activated(this);
    }

    // The platform activates the window at the host's request, or, while the user works in another application, makes
    // it the window activated when the user switches back.
    private void ActivateForRequest()
    {
        if (_active) return;
        if (_factory is { IsHostInactive: true } factory) factory.Defer(this);
        else SetActive(true);
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_open)
        {
            var wasActive = _active;
            _open = false;
            _closedOnce = true;
            Closed?.Invoke(this, EventArgs.Empty);
            if (wasActive) _factory?.ActivateNext(this);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    // A focus request activates the window, as the host's present does on a desktop that lets it.
    public ValueTask FocusAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _focused);
        ActivateForRequest();
        return ValueTask.CompletedTask;
    }

    public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    // As on Windows, where ShowWindow with SW_MAXIMIZE or SW_RESTORE activates the window.
    public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default)
    {
        ActivateForRequest();
        return ValueTask.CompletedTask;
    }

    private volatile bool _fullScreen;

    // As on Windows: entering full screen does not activate the window; leaving it restores its placement with
    // SW_SHOWNORMAL or SW_SHOWMAXIMIZED, which does.
    public ValueTask SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default)
    {
        var leaving = _fullScreen && !fullScreen;
        _fullScreen = fullScreen;
        if (leaving) ActivateForRequest();
        return ValueTask.CompletedTask;
    }

    public ValueTask MoveToMonitorAsync(string monitorId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
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
