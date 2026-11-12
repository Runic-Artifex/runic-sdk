using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using TUnit.Core;
using static HostedApplicationTests;

// The elevated pair of hosted mode (host-protocol.md, Elevated host): an elevated host that allows an elevated guest
// (RunicGuestStartInfo.AllowElevatedGuest) and an elevated guest that allows an elevated host
// (RunicGuestOptions.AllowElevatedHost). The guest's cases replace its check of itself and its parent
// (RunicGuestOptions.ReadPrivileges), so they play every combination on any machine; the host's cases replace its own
// elevation (RunicHostedApplicationOptions.IsHostElevated). ElevatedHostedApplicationTests checks the real pair.
internal sealed class HostedElevationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // The guest, in this process, against an offer that does or does not allow an elevated guest: the refusal line and
    // event 2010 name the code, and the accepted elevated pair logs event 2025. The guest's start runs on a Desktop host
    // of its own, not through RunicDesktopHost.Run, which on macOS must be called from the process's main thread.
    [Test, NotInParallel(Order = 45)]
    [Arguments(false, true, false, false, false, null)]
    [Arguments(false, true, false, true, true, null)]
    [Arguments(false, false, false, true, true, "guest-other-user")]
    [Arguments(true, true, true, false, false, "guest-elevated")]
    [Arguments(true, true, true, false, true, "guest-elevated")]
    [Arguments(true, true, true, true, false, "guest-elevated")]
    [Arguments(true, false, false, true, true, "guest-other-user")]
    [Arguments(true, true, false, true, true, "guest-elevated-host-not-elevated")]
    [Arguments(true, true, true, true, true, null)]
    [DisplayName("GuestElevation(elevated: $elevated, sameUser: $sameUser, parentElevated: $parentElevated, guestAllows: $guestAllows, offerAllows: $offerAllows) → $refusal")]
    public async Task TheGuestDecidesFromItsOwnCheckOfItsParent(bool elevated, bool sameUser, bool parentElevated, bool guestAllows,
        bool offerAllows, string? refusal)
    {
        using var input = new AnonymousPipeServerStream(PipeDirection.Out);
        var output = new Lines();
        var logs = new RecordingLoggerFactory();
        var guest = RunicGuestOptions.FromStreams(new AnonymousPipeClientStream(PipeDirection.In, input.ClientSafePipeHandle), output) with
        {
            AllowElevatedHost = guestAllows,
            ReadPrivileges = () => new GuestPrivilegeFacts(elevated, sameUser, parentElevated),
            // A backstop: the case ends the start itself by closing standard input.
            StartTimeout = TestTimeouts.Scale(StepTimeout),
            ExitProcess = static _ => { },
        };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WaitForConnection = false });
        var offer = HostProtocol.Line(HostProtocol.OfferPrefix,
            HostProtocol.Offer([HostProtocol.Version], HostProtocol.Base64Url(RandomNumberGenerator.GetBytes(32)), "Elevation host", "1.0", offerAllows));
        input.Write(Encoding.UTF8.GetBytes(offer));
        input.Flush();
        var start = HostedGuest.StartAsync(guest, desktop, logs.CreateLogger("Runic.Application.Views.Desktop"));

        // The start writes its line before it fails; one that fails without a line fails the case at once, with its error.
        var next = output.NextAsync(static text => text.StartsWith(HostProtocol.ChannelPrefix, StringComparison.Ordinal) ||
            text.StartsWith(HostProtocol.RefusedPrefix, StringComparison.Ordinal), TestTimeouts.Scale(StepTimeout));
        if (await Task.WhenAny(next, start) == start && !next.IsCompleted)
        {
            try { await next.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException)
            {
                await start; // Throws the start's failure.
                throw new InvalidOperationException("The guest's start completed without a channel or refusal line.");
            }
        }
        var line = await next;
        if (refusal is null)
        {
            Check(line.StartsWith(HostProtocol.ChannelPrefix, StringComparison.Ordinal), $"The guest refused: {line}");
            Check(logs.Entries.Any(static entry => entry.EventId == 2025) == elevated,
                $"Event 2025 was {(elevated ? "not " : "")}logged for {(elevated ? "the elevated pair" : "a standard user's guest")}.");
            // The host goes away before it connects: the start fails with host-gone.
            input.Dispose();
            var gone = await FailureOf(start);
            Check(gone.Code == "host-gone", $"The start ended with {gone.Code}.");
            return;
        }
        var content = HostProtocol.ParseLine(line, HostProtocol.RefusedPrefix);
        Check(content?["code"]?.GetValue<string>() == refusal, $"The guest wrote '{line}', not the refusal {refusal}.");
        var failure = await FailureOf(start);
        Check(failure.Code == refusal, $"The start threw {failure.Code}, not {refusal}.");
        Check(logs.Entries.Any(entry => entry.EventId == 2010 && entry.Level == LogLevel.Error && entry.Message.Contains($"({refusal})", StringComparison.Ordinal)),
            $"Event 2010 does not name {refusal}.");
        Check(logs.Entries.All(static entry => entry.EventId != 2025), "A refused start logged event 2025.");
    }

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    private static async Task<RunicHostedApplicationException> FailureOf(Task<HostedGuest> start)
    {
        try
        {
            var started = await start.WaitScaledAsync(StepTimeout);
            await started.DisposeAsync();
        }
        catch (RunicHostedApplicationException error) { return error; }
        throw new InvalidOperationException("The guest's start succeeded without a host.");
    }

    // Only an elevated host may allow an elevated guest; the start fails before the guest's process starts.
    [Test, NotInParallel(Order = 50)]
    public async Task AnUnelevatedHostCannotAllowAnElevatedGuest()
    {
        await using var fixture = await HostFixture.StartAsync();
        var start = HostedGuestProgram.Guest("main") with { AllowElevatedGuest = true };
        var failure = await StartFailureAsync(fixture, start, new RunicHostedApplicationOptions { IsHostElevated = static () => false });
        Check(failure.Code == "host-not-elevated" && failure.GuestCode is null, $"The start failed with {failure.Code}: {failure.Message}");
        Check(fixture.Logs.Entries.Any(static entry => entry.EventId == 2016 && entry.Message.Contains("(host-not-elevated)", StringComparison.Ordinal)),
            "Event 2016 does not name host-not-elevated.");
        Check(fixture.Logs.Entries.All(static entry => entry.EventId != 2022), "The guest's process started.");
        if (!Environment.IsPrivilegedProcess)
        {
            failure = await StartFailureAsync(fixture, start, null);
            Check(failure.Code == "host-not-elevated", $"This unelevated process's start failed with {failure.Code}: {failure.Message}");
        }
    }

    // An elevated guest is never looked up in PATH or relative to the current directory.
    [Test]
    public async Task AnElevatedGuestNeedsFullyQualifiedPaths()
    {
        await using var fixture = await HostFixture.StartAsync();
        var elevated = new RunicHostedApplicationOptions { IsHostElevated = static () => true };
        var guest = HostedGuestProgram.Guest("main") with { AllowElevatedGuest = true };
        foreach (var start in (RunicGuestStartInfo[])
        [
            guest with { FileName = Path.GetFileName(guest.FileName) },
            guest with { FileName = Path.Combine(".", Path.GetFileName(guest.FileName)) },
            guest with { WorkingDirectory = "relative" },
        ])
        {
            try
            {
                await using var application = await fixture.Applications.StartAsync(start, elevated);
                throw new InvalidOperationException($"'{start.FileName}' in '{start.WorkingDirectory}' started.");
            }
            catch (ArgumentException) { }
        }
        // Without the opt-in, the same relative name is looked up as before: the start is not refused for its path.
        try { await using var _ = await fixture.Applications.StartAsync(guest with { FileName = "runic-no-such-guest", AllowElevatedGuest = false }); }
        catch (RunicHostedApplicationException error) when (error.Code == "guest-start-failed") { }
    }

    // A host that allows an elevated guest hosts its guest: one that does not run elevated ignores the offer's opt-in,
    // and in an elevated test process this is the elevated pair. The test process plays an elevated host when it is not.
    [Test, NotInParallel(Order = 50)]
    public async Task AHostThatAllowsAnElevatedGuestHostsItsGuest()
    {
        await using var fixture = await HostFixture.StartAsync();
        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("main") with { AllowElevatedGuest = true },
            new RunicHostedApplicationOptions { IsHostElevated = static () => true });
        var window = await opened.WaitScaledAsync(Timeout);
        Check(window.Application == application && window.IsOpen, "The guest's window did not open.");
        Check(await window.RequestCloseAsync(), "The guest refused the close.");
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, $"The guest did not exit cleanly:\n{fixture.GuestErrors}");
    }

    // The guest's standard output, line by line.
    private sealed class Lines : TextWriter
    {
        private readonly StringBuilder _line = new();
        private readonly System.Threading.Channels.Channel<string> _lines = System.Threading.Channels.Channel.CreateUnbounded<string>();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_line)
            {
                if (value != '\n')
                {
                    _line.Append(value);
                    return;
                }
                _lines.Writer.TryWrite(_line.ToString().TrimEnd('\r'));
                _line.Clear();
            }
        }

        public async Task<string> NextAsync(Func<string, bool> wanted, TimeSpan timeout)
        {
            using var cancel = new CancellationTokenSource(timeout);
            while (true)
            {
                var line = await _lines.Reader.ReadAsync(cancel.Token);
                if (wanted(line)) return line;
            }
        }
    }
}

// The real elevated pair: an elevated test process hosts this suite's executable, which inherits its elevation, with
// both opt-ins (HostedGuestProgram.ElevatedPair). It runs in the elevated Windows CI step (RUNIC_TEST_ELEVATED_PAIR=1,
// where GitHub's runner runs elevated); elsewhere it is skipped, and with the variable an unelevated process fails it. The
// pair's windows, menus, dialogs and window services are the host cases' own, run as the pair.
internal sealed class ElevatedHostedApplicationTests
{
    private static void RequirePair()
    {
        if (!HostedGuestProgram.ElevatedPair)
            Skip.Test($"The elevated pair runs only with {HostedGuestProgram.ElevatedPairVariable}=1 in an elevated process, as in the elevated Windows CI step.");
        if (!Environment.IsPrivilegedProcess)
            throw new InvalidOperationException($"{HostedGuestProgram.ElevatedPairVariable}=1 is set, but the test process does not run elevated.");
    }

    [Test, NotInParallel(Order = 80)]
    public async Task ThePairOpensAndClosesTheGuestsWindow()
    {
        RequirePair();
        await new HostedApplicationTests().OpensTheGuestsWindowInAWindowOfTheHostAndClosesItThroughTheGuest();
    }

    [Test, NotInParallel(Order = 80)]
    public async Task ThePairShowsTheGuestsMenuBar()
    {
        RequirePair();
        await new HostedMenusAndDialogsTests().TheGuestsMenuBarShowsInItsWindowAndRunsTheGuestsCommands();
    }

    [Test, NotInParallel(Order = 80)]
    public async Task ThePairShowsTheGuestsContextMenusAndDialogs()
    {
        RequirePair();
        await new HostedMenusAndDialogsTests().TheHostShowsTheGuestsContextMenusAndDialogsOwnedByItsWindow();
    }

    [Test, NotInParallel(Order = 80)]
    public async Task ThePairServesTheGuestsWindowServices()
    {
        RequirePair();
        await new HostedWindowServicesTests().TheHostServesAGuestsWindowServicesWithItsDefaultPolicy();
    }

    // RunicTestHost plays the elevated host for a guest application's own tests.
    [Test, NotInParallel(Order = 80)]
    public async Task TheTestHostPlaysAnElevatedHost()
    {
        RequirePair();
        await new HostedWindowServicesTests().UnderTheTestHostAGuestFollowsTheHostAndUsesItsWindowServices();
    }

    // Every elevated combination outside the pair keeps its refusal, against the real parent check.
    [Test, NotInParallel(Order = 80)]
    public async Task AnElevatedGuestStillRefusesWithoutBothOptIns()
    {
        RequirePair();
        await using var fixture = await HostFixture.StartAsync();
        var failure = await StartFailureAsync(fixture, HostedGuestProgram.Guest("main") with { AllowElevatedGuest = false }, null);
        Check(failure is { Code: "guest-refused", GuestCode: "guest-elevated" }, $"Without the host's opt-in: {failure.Code}/{failure.GuestCode}: {failure.Message}");
        failure = await StartFailureAsync(fixture, HostedGuestProgram.Guest("no-elevated-host"), null);
        Check(failure is { Code: "guest-refused", GuestCode: "guest-elevated" }, $"Without the guest's opt-in: {failure.Code}/{failure.GuestCode}: {failure.Message}");

        var start = HostedGuestProgram.Guest("main");
        var process = new System.Diagnostics.ProcessStartInfo(start.FileName) { WorkingDirectory = start.WorkingDirectory };
        foreach (var argument in start.Arguments) process.ArgumentList.Add(argument);
        try
        {
            await using var host = await RunicTestHost.StartGuestAsync(process, new RunicTestHostOptions { StartTimeout = TestTimeouts.Scale(TimeSpan.FromSeconds(30)) });
            throw new InvalidOperationException("The test host hosted an elevated guest without the opt-in.");
        }
        catch (RunicHostedApplicationException error) when (error.GuestCode == "guest-elevated") { }
    }
}
