using System.Runtime.CompilerServices;

namespace Runic.Platform;

/// <summary>The computer's power and session state, as far as the operating system tells it.</summary>
/// <remarks>Each value is <see langword="null"/> when the platform does not say, such as before the first change it reports.</remarks>
public sealed record DesktopPowerState
{
    /// <summary>Whether the computer runs on battery; <see langword="false"/> on mains power and for a computer without a battery.</summary>
    public bool? IsOnBattery { get; init; }

    /// <summary>
    /// Whether the user or the system asks applications to save energy: Battery Saver or Energy Saver on Windows, Low
    /// Power Mode on macOS, and the power-saver profile on Linux.
    /// </summary>
    public bool? IsLowPower { get; init; }

    /// <summary>Whether the user's session is locked, so nobody sees the application's windows.</summary>
    public bool? IsLocked { get; init; }
}

/// <summary>What happened to the computer's power or the user's session.</summary>
/// <remarks>This set is open: a minor release may add values, so ignore unknown ones.</remarks>
public enum DesktopPowerEventKind
{
    /// <summary>
    /// The state changed, such as from mains power to battery; the watch also reports the current state first with this
    /// kind.
    /// </summary>
    StateChanged = 0,

    /// <summary>
    /// The computer is about to sleep or hibernate. The application gets little or no time, and Runic does not delay
    /// the sleep: save what must not be lost as it changes, and treat this as a hint, such as to close network
    /// connections.
    /// </summary>
    Suspending = 1,

    /// <summary>The computer woke up from sleep or hibernation: refresh what may be stale, such as network connections.</summary>
    Resumed = 2,

    /// <summary>The user's session was locked.</summary>
    Locked = 3,

    /// <summary>The user's session was unlocked.</summary>
    Unlocked = 4,
}

/// <summary>A power or session event, with the state after it.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="State">The state after the event.</param>
public sealed record DesktopPowerEvent(DesktopPowerEventKind Kind, DesktopPowerState State);

/// <summary>Sleep, wake, session lock and battery events of the computer.</summary>
/// <remarks>
/// <para>
/// An application-scoped service that needs no window; a ViewModel takes it in its constructor. Results and watch
/// items arrive on the thread pool. Dispose it at shutdown; that ends its watches.
/// </para>
/// <para>
/// Windows: <c>WM_POWERBROADCAST</c>, <c>WTSRegisterSessionNotification</c>, <c>GetSystemPowerStatus</c> and the
/// power saving status. macOS: <c>NSWorkspace</c>'s sleep and wake notifications, the screen lock notifications,
/// IOKit's power source and <c>NSProcessInfo</c>'s Low Power Mode. Linux: logind's <c>PrepareForSleep</c> and the
/// session's <c>Lock</c>, <c>Unlock</c> and <c>LockedHint</c>, UPower's <c>OnBattery</c> and the power-saver profile,
/// on the system bus; inside Flatpak that needs <c>--system-talk-name</c> for <c>org.freedesktop.login1</c>,
/// <c>org.freedesktop.UPower</c> and <c>org.freedesktop.UPower.PowerProfiles</c>. Hosts without a desktop, such as
/// CS-WebUI and serve-only Runic Desktop, report <see cref="PlatformResult{T}.Unavailable"/>.
/// </para>
/// </remarks>
public interface IDesktopPowerEvents : IAsyncDisposable
{
    /// <summary>Reads the current power and session state.</summary>
    /// <param name="cancellationToken">Cancels waiting for the answer.</param>
    /// <returns>The state, or <see cref="PlatformResult{T}.Unavailable"/> when the platform has no source for it.</returns>
    /// <exception cref="ObjectDisposedException">The service was disposed.</exception>
    ValueTask<PlatformResult<DesktopPowerState>> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Reports the current state as <see cref="DesktopPowerEventKind.StateChanged"/>, then each event.</summary>
    /// <param name="cancellationToken">Ends the watch, which then throws <see cref="OperationCanceledException"/>.</param>
    /// <remarks>
    /// A <see cref="DesktopPowerEventKind.StateChanged"/> that leaves the state as it was is not reported, nor is a
    /// repeated event, such as a second <see cref="DesktopPowerEventKind.Locked"/>. A reader that falls behind loses no
    /// <see cref="DesktopPowerEventKind.Suspending"/>, <see cref="DesktopPowerEventKind.Resumed"/>,
    /// <see cref="DesktopPowerEventKind.Locked"/> or <see cref="DesktopPowerEventKind.Unlocked"/>; state changes waiting
    /// between two of them are combined into the latest. When a source goes away the watch
    /// reports <see cref="PlatformResult{T}.Unavailable"/> and continues with the current state once it is back, so
    /// events while it was away, such as a sleep and wake, are missed and only the state after them shows; without
    /// a source it reports <see cref="PlatformResult{T}.Unavailable"/> once and then waits. Disposing the service ends
    /// the watch.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The service was disposed.</exception>
    IAsyncEnumerable<PlatformResult<DesktopPowerEvent>> WatchAsync(CancellationToken cancellationToken = default);
}

/// <summary>Power events for hosts and platforms without a provider.</summary>
public static class DesktopPowerEvents
{
    /// <summary>Creates power events that report every read unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>
    /// <see cref="IDesktopPowerEvents.WatchAsync"/> reports that value once and then waits for cancellation or disposal.
    /// Nothing throws except caller cancellation and use after disposal.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not defined.</exception>
    public static IDesktopPowerEvents Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailablePowerEvents(reason);
    }

    private sealed class UnavailablePowerEvents(PlatformUnavailableReason reason) : IDesktopPowerEvents, IDesktopServiceStatus
    {
        private readonly CancellationTokenSource _disposed = new();

        public CapabilityStatus Status { get; } = DesktopServiceStatus.Unavailable(reason);

        public ValueTask<PlatformResult<DesktopPowerState>> ReadAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<DesktopPowerState>>(new PlatformResult<DesktopPowerState>.Unavailable(reason));
        }

        public IAsyncEnumerable<PlatformResult<DesktopPowerEvent>> WatchAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
            return Watch(cancellationToken);
        }

        private async IAsyncEnumerable<PlatformResult<DesktopPowerEvent>> Watch([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
            linked.Token.ThrowIfCancellationRequested();
            yield return new PlatformResult<DesktopPowerEvent>.Unavailable(reason);
            await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            _disposed.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
