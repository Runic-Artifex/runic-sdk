using System;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

/// <summary>
/// Tells a stop of dev from an unexpected exit of its child processes (#1396). A terminal Ctrl+C signals the whole
/// process group, so the development server can exit before dev's own signal handler cancels dev. Its exit code then
/// is that of the signal, and dev's cancellation follows within milliseconds. Any other exit reports at once.
/// </summary>
internal static class DevStop
{
    /// <summary>How long dev waits for its own stop after a child exited with a stop signal's code.</summary>
    internal static readonly TimeSpan SignalGrace = TimeSpan.FromSeconds(2);

    // STATUS_CONTROL_C_EXIT, a Windows process that Ctrl+C ended.
    private const int ControlCExit = unchecked((int)0xC000013A);

    /// <summary>
    /// Whether <paramref name="exitCode"/> is that of a process that a stop signal ended: 128 + SIGHUP, SIGINT, SIGQUIT
    /// or SIGTERM on Unix (as .NET and the shells report it, also for npm, pnpm and Bun, which exit with their child's
    /// signal), STATUS_CONTROL_C_EXIT on Windows.
    /// </summary>
    internal static bool IsStopSignalExit(int exitCode, bool windows) =>
        windows ? exitCode == ControlCExit : exitCode is 129 or 130 or 131 or 143;

    internal static bool IsStopSignalExit(int exitCode) => IsStopSignalExit(exitCode, OperatingSystem.IsWindows());

    /// <summary>
    /// Whether a failure may come from dev's own stop: dev is stopping already, or a child exited with a stop signal's
    /// code, so <see cref="IsStopAsync"/> waits for dev's signal.
    /// </summary>
    internal static bool MayBeStop(DevDevelopmentException error, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(error);
        return stop.IsCancellationRequested || error.ChildExitCode is { } exitCode && IsStopSignalExit(exitCode);
    }

    /// <summary>
    /// Whether the failure is dev's stop: dev is stopping, or the child exited with a stop signal's code and dev's own
    /// stop follows within <paramref name="grace"/>. Any other failure reports at once.
    /// </summary>
    internal static async Task<bool> IsStopAsync(DevDevelopmentException error, TimeSpan grace, TimeProvider time, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (stop.IsCancellationRequested) return true;
        return error.ChildExitCode is { } exitCode && IsStopSignalExit(exitCode) &&
            await FollowsAsync(grace, time, stop).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="stop"/> is cancelled now or within <paramref name="grace"/>.</summary>
    internal static async Task<bool> FollowsAsync(TimeSpan grace, TimeProvider time, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (stop.IsCancellationRequested) return true;
        try
        {
            await Task.Delay(grace, time, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        return stop.IsCancellationRequested;
    }
}
