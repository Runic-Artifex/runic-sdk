using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Tool;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

// #1396: a terminal Ctrl+C reaches the whole process group, so the development server can exit with 130 before dev's own
// handler cancels dev. dev then ends quietly with 130 when its stop follows within the grace; an unexpected exit, or a
// stop signal that dev never gets, still reports RAPPDEV1007.
internal sealed partial class ToolTests
{
    private static DevDevelopmentException StopTestServerExit(int exitCode) =>
        new("RAPPDEV1007", $"The vite development server exited unexpectedly with code {exitCode}.") { ChildExitCode = exitCode };

    [Test, DisplayName("dev takes 128 + SIGHUP, SIGINT, SIGQUIT and SIGTERM, or Windows' Ctrl+C status, for a stop signal's exit")]
    public void RecognizesStopSignalExitCodes()
    {
        foreach (int code in new[] { 129, 130, 131, 143 })
        {
            True(DevStop.IsStopSignalExit(code, windows: false), $"{code} is a stop signal's exit on Unix.");
            False(DevStop.IsStopSignalExit(code, windows: true), $"{code} is no stop signal's exit on Windows.");
        }
        foreach (int code in new[] { 0, 1, 2, 137, 139, 255 })
        {
            False(DevStop.IsStopSignalExit(code, windows: false), $"{code} is an unexpected exit.");
        }
        True(DevStop.IsStopSignalExit(unchecked((int)0xC000013A), windows: true), "STATUS_CONTROL_C_EXIT is Ctrl+C's exit on Windows.");
    }

    [Test, DisplayName("a server exit by Ctrl+C is dev's stop when dev's own signal follows within the grace")]
    public async Task ServerExitBySignalWaitsForTheStop()
    {
        int signalExit = OperatingSystem.IsWindows() ? unchecked((int)0xC000013A) : 130;
        var time = new FakeTimeProvider();
        using var stop = new CancellationTokenSource();
        True(DevStop.MayBeStop(StopTestServerExit(signalExit), stop.Token), "A stop signal's exit may be dev's stop.");
        Task<bool> decision = DevStop.IsStopAsync(StopTestServerExit(signalExit), DevStop.SignalGrace, time, stop.Token);
        False(decision.IsCompleted, "dev waits for its own stop.");
        time.Advance(TimeSpan.FromMilliseconds(50));
        await stop.CancelAsync().ConfigureAwait(false);
        True(await decision.ConfigureAwait(false), "dev's stop followed: the exit is the stop.");

        // dev's stop never comes, as for a SIGINT sent to the server alone: after the grace it is an unexpected exit.
        using var never = new CancellationTokenSource();
        Task<bool> unexpected = DevStop.IsStopAsync(StopTestServerExit(signalExit), DevStop.SignalGrace, time, never.Token);
        time.Advance(DevStop.SignalGrace);
        False(await unexpected.ConfigureAwait(false), "Without dev's stop, the exit is reported.");
    }

    [Test, DisplayName("an unexpected server exit reports RAPPDEV1007 at once, and any failure while dev stops is the stop")]
    public async Task UnexpectedExitReportsAtOnce()
    {
        var time = new FakeTimeProvider();
        using var stop = new CancellationTokenSource();
        // A crash (code 1) or SIGKILL (137): no wait, so dev fails as fast as before.
        False(DevStop.MayBeStop(StopTestServerExit(1), stop.Token), "A crash is not a stop.");
        Task<bool> crash = DevStop.IsStopAsync(StopTestServerExit(137), DevStop.SignalGrace, time, stop.Token);
        True(crash.IsCompleted, "A crash is decided without waiting.");
        False(await crash.ConfigureAwait(false), "A crash is reported.");
        False(DevStop.MayBeStop(new DevDevelopmentException("RAPPDEV1007", "The page did not load."), stop.Token), "No child exit, no stop.");

        // Once dev is stopping, a failure that the stop caused (a request to the ended server) is the stop too.
        await stop.CancelAsync().ConfigureAwait(false);
        var failure = new DevDevelopmentException("RAPPDEV1007", "The Vite development server did not answer.");
        True(DevStop.MayBeStop(failure, stop.Token), "dev is stopping.");
        True(await DevStop.IsStopAsync(failure, DevStop.SignalGrace, time, stop.Token).ConfigureAwait(false), "dev is stopping.");
    }

    [Test, DisplayName("a development server's exit before readiness carries its exit code")]
    public void ExitBeforeReadinessCarriesTheCode()
    {
        DevDevelopmentException? error = null;
        try
        {
            ViteDevelopmentServer.ThrowIfExited(Task.FromResult(130));
        }
        catch (DevDevelopmentException thrown)
        {
            error = thrown;
        }
        Equal(130, error?.ChildExitCode);
        Equal("RAPPDEV1007", error?.Code);
    }
}
