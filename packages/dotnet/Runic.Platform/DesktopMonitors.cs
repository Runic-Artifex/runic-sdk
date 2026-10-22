using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Runic.Platform;

/// <summary>A rectangle in a platform's screen coordinates.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge, or the bottom edge on macOS, whose screen coordinates grow upwards.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public readonly record struct DesktopRect(int X, int Y, int Width, int Height);

/// <summary>One monitor (a screen or display) of the desktop.</summary>
/// <param name="Id">
/// The platform's identifier of the monitor; treat it as opaque. It stays the same across restarts where the operating
/// system has a stable one: the display's device path on Windows, its display UUID on macOS and its connector name on
/// Linux. Pass it to <c>DesktopWindow.MoveToMonitorAsync</c> or <c>DesktopWindowOptions.Monitor</c>.
/// </param>
/// <param name="DisplayName">A name to show, such as the monitor's model, or its connector where the model is unknown.</param>
/// <remarks>
/// <see cref="Bounds"/> and <see cref="WorkArea"/> use the units the platform's windows use for their position and size,
/// so a work area can be passed to <c>DesktopWindow.MoveAsync</c> as it is: physical virtual-screen pixels on Windows,
/// points in AppKit screen coordinates (bottom-left origin, measured up from the bottom of the main screen) on macOS, and
/// logical pixels on Linux.
/// </remarks>
public sealed record DesktopMonitor(string Id, string DisplayName)
{
    /// <summary>Gets the whole monitor.</summary>
    public DesktopRect Bounds { get; init; }

    /// <summary>Gets the part of the monitor windows normally use: without the taskbar, the Dock and menu bar, or panels.</summary>
    /// <remarks>Equals <see cref="Bounds"/> where the platform does not report one, such as on Wayland.</remarks>
    public DesktopRect WorkArea { get; init; }

    /// <summary>Gets the monitor's scale: 1 is 96 DPI on Windows, the backing scale factor on macOS and GDK's scale on Linux.</summary>
    public double Scale { get; init; } = 1;

    /// <summary>Gets whether this is the primary monitor. Wayland has none, so every monitor reports <see langword="false"/> there.</summary>
    public bool IsPrimary { get; init; }
}

/// <summary>The desktop's monitors: listing them and following changes.</summary>
/// <remarks>
/// Methods are safe to call from any thread; results and watch items arrive on the thread pool. A service that cannot
/// list monitors, such as one of a host without a native window toolkit, reports
/// <see cref="PlatformResult{T}.Unavailable"/>.
/// </remarks>
public interface IDesktopMonitors
{
    /// <summary>Lists the connected monitors, the primary one first where there is one.</summary>
    /// <param name="cancellationToken">Cancels waiting for the result.</param>
    ValueTask<PlatformResult<ImmutableArray<DesktopMonitor>>> GetMonitorsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reports the current monitors, then the whole list again after each change.</summary>
    /// <param name="cancellationToken">Ends the watch.</param>
    /// <remarks>
    /// A change is a monitor connected or disconnected, or a change of its bounds, work area, scale or primary flag; a
    /// change that leaves the list equal is not reported. When reading the monitors fails, the watch reports that result
    /// and ends. A service without monitors reports <see cref="PlatformResult{T}.Unavailable"/> once and then waits.
    /// </remarks>
    IAsyncEnumerable<PlatformResult<ImmutableArray<DesktopMonitor>>> WatchAsync(CancellationToken cancellationToken = default);
}

/// <summary>Monitors for hosts and platforms that cannot list them.</summary>
public static class DesktopMonitors
{
    /// <summary>Creates monitors that report every request unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>
    /// <see cref="IDesktopMonitors.WatchAsync"/> reports that value once and then waits for cancellation. Nothing throws
    /// except for caller cancellation.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not defined.</exception>
    public static IDesktopMonitors Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableMonitors(reason);
    }

    private sealed class UnavailableMonitors(PlatformUnavailableReason reason) : IDesktopMonitors, IDesktopServiceStatus
    {
        public CapabilityStatus Status { get; } = DesktopServiceStatus.Unavailable(reason);

        public ValueTask<PlatformResult<ImmutableArray<DesktopMonitor>>> GetMonitorsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<ImmutableArray<DesktopMonitor>>>(
                new PlatformResult<ImmutableArray<DesktopMonitor>>.Unavailable(reason));
        }

        public async IAsyncEnumerable<PlatformResult<ImmutableArray<DesktopMonitor>>> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new PlatformResult<ImmutableArray<DesktopMonitor>>.Unavailable(reason);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }
}
