using System.Runtime.CompilerServices;

namespace Runic.Platform;

/// <summary>What the operating system knows about the computer's network connection.</summary>
/// <param name="IsAvailable">Whether a usable network route exists. <see langword="false"/> always comes with
/// <see cref="DesktopNetworkConnectivity.None"/>.</param>
/// <remarks>
/// The operating system's view, not a guarantee that a server answers: try the request and handle its failure, and use
/// the state to explain it or to postpone work, such as a large download on a metered connection.
/// </remarks>
public sealed record DesktopNetworkState(bool IsAvailable)
{
    /// <summary>How far the connection reaches, as the operating system's own connectivity check found;
    /// <see cref="DesktopNetworkConnectivity.Unknown"/> where it does not check.</summary>
    public DesktopNetworkConnectivity Connectivity { get; init; }

    /// <summary>Whether the connection is metered or limited in data, such as a mobile hotspot or Low Data Mode;
    /// <see langword="null"/> when the operating system does not say.</summary>
    public bool? IsMetered { get; init; }
}

/// <summary>How far a network connection reaches.</summary>
/// <remarks>This set is open: a minor release may add values, so treat unknown values as <see cref="Unknown"/>.</remarks>
public enum DesktopNetworkConnectivity
{
    /// <summary>The operating system does not say, such as macOS while a network is available.</summary>
    Unknown = 0,
    /// <summary>No network.</summary>
    None = 1,
    /// <summary>Only the local network, without a route to the internet.</summary>
    Local = 2,
    /// <summary>Some of the internet, but the operating system's check failed.</summary>
    Limited = 3,
    /// <summary>A captive portal, such as a hotel's sign-in page, intercepts requests until the user signs in.</summary>
    CaptivePortal = 4,
    /// <summary>The internet, as the operating system's check confirmed.</summary>
    Internet = 5,
}

/// <summary>The computer's network status: online, metered and captive portal, read once or watched.</summary>
/// <remarks>
/// An application-scoped service that needs no window. Results and watch items arrive on the thread pool. Dispose it
/// at shutdown; that ends its watches.
/// </remarks>
public interface IDesktopNetworkStatus : IAsyncDisposable
{
    /// <summary>Reads the current network state.</summary>
    /// <param name="cancellationToken">Cancels waiting for the answer.</param>
    /// <returns>The state, or <see cref="PlatformResult{T}.Unavailable"/> when the platform has no network status
    /// source.</returns>
    /// <exception cref="ObjectDisposedException">The service was disposed.</exception>
    ValueTask<PlatformResult<DesktopNetworkState>> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Reports the current network state, then each change.</summary>
    /// <param name="cancellationToken">Ends the watch, which then throws <see cref="OperationCanceledException"/>.</param>
    /// <remarks>
    /// A change that leaves the state as it was is not reported. When the status source goes away, such as a desktop
    /// portal that stopped, the watch reports <see cref="PlatformResult{T}.Unavailable"/> and continues with a fresh
    /// state once it is back. Without a source the watch reports <see cref="PlatformResult{T}.Unavailable"/> once and
    /// then waits. Disposing the service ends the watch.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The service was disposed.</exception>
    IAsyncEnumerable<PlatformResult<DesktopNetworkState>> WatchAsync(CancellationToken cancellationToken = default);
}

/// <summary>Network status for hosts and platforms without a provider.</summary>
public static class DesktopNetworkStatus
{
    /// <summary>Creates network status that reports every read unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>
    /// <see cref="IDesktopNetworkStatus.WatchAsync"/> reports that value once and then waits for cancellation or
    /// disposal. Nothing throws except caller cancellation and use after disposal.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not defined.</exception>
    public static IDesktopNetworkStatus Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableNetworkStatus(reason);
    }

    private sealed class UnavailableNetworkStatus(PlatformUnavailableReason reason) : IDesktopNetworkStatus, IDesktopServiceStatus
    {
        private readonly CancellationTokenSource _disposed = new();

        public CapabilityStatus Status { get; } = DesktopServiceStatus.Unavailable(reason);

        public ValueTask<PlatformResult<DesktopNetworkState>> ReadAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<DesktopNetworkState>>(new PlatformResult<DesktopNetworkState>.Unavailable(reason));
        }

        public IAsyncEnumerable<PlatformResult<DesktopNetworkState>> WatchAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
            return Watch(cancellationToken);
        }

        private async IAsyncEnumerable<PlatformResult<DesktopNetworkState>> Watch([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
            linked.Token.ThrowIfCancellationRequested();
            yield return new PlatformResult<DesktopNetworkState>.Unavailable(reason);
            await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            _disposed.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
