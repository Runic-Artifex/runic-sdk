using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Runic.Platform.Runtime;

// A provider's native network status: the Windows connectivity hint, the macOS path monitor, the desktop portal or
// NetworkManager on Linux.
internal interface INetworkStateSource
{
    // The current state.
    ValueTask<PlatformResult<DesktopNetworkState>> ReadAsync(CancellationToken cancellationToken);

    // Reports the current state, then each change, until the subscription is disposed. A source that goes away reports
    // Unavailable and, once it is back, a fresh state. Calls to report may come from any thread, also during the call.
    ValueTask<IAsyncDisposable> SubscribeAsync(Action<PlatformResult<DesktopNetworkState>> report, CancellationToken cancellationToken);
}

// IDesktopNetworkStatus over a source: the watch semantics every provider shares. Each watch subscribes on its own and
// reports the latest state; states equal to the previous one are suppressed.
internal sealed class NetworkStatusService(INetworkStateSource source) : IDesktopNetworkStatus, IDesktopServiceStatus
{
    private readonly CancellationTokenSource _disposed = new();

    public CapabilityStatus Status { get; } = new CapabilityStatus.Available();

    public async ValueTask<PlatformResult<DesktopNetworkState>> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
        cancellationToken.ThrowIfCancellationRequested();
        return Checked(await source.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    public IAsyncEnumerable<PlatformResult<DesktopNetworkState>> WatchAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
        return WatchCoreAsync(cancellationToken);
    }

    private async IAsyncEnumerable<PlatformResult<DesktopNetworkState>> WatchCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        var token = linked.Token;
        // Only the latest state matters: a slow reader skips the states in between.
        var updates = Channel.CreateBounded<PlatformResult<DesktopNetworkState>>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        IAsyncDisposable? subscription = null;
        try
        {
            try { subscription = await source.SubscribeAsync(result => updates.Writer.TryWrite(Checked(result)), token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // Without a source the watch reports it once and waits.
                updates.Writer.TryWrite(new PlatformResult<DesktopNetworkState>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
            }
            PlatformResult<DesktopNetworkState>? previous = null;
            while (true)
            {
                var next = await updates.Reader.ReadAsync(token).ConfigureAwait(false);
                if (previous is not null && Same(previous, next)) continue;
                previous = next;
                yield return next;
            }
        }
        finally
        {
            if (subscription is not null) await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Results are records, so equal states and equal reasons compare equal.
    internal static bool Same(PlatformResult<DesktopNetworkState> left, PlatformResult<DesktopNetworkState> right) => Equals(left, right);

    // A state without a network has no connectivity, whatever the source said.
    internal static PlatformResult<DesktopNetworkState> Checked(PlatformResult<DesktopNetworkState> result) =>
        result is PlatformResult<DesktopNetworkState>.Success { Value: { IsAvailable: false } state } && state.Connectivity != DesktopNetworkConnectivity.None
            ? new PlatformResult<DesktopNetworkState>.Success(state with { Connectivity = DesktopNetworkConnectivity.None })
            : result;

    public ValueTask DisposeAsync()
    {
        _disposed.Cancel();
        return ValueTask.CompletedTask;
    }
}
