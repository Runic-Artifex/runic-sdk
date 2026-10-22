using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// Monitors read through a platform function and a change signal; the shared watch of the built-in hosts. ReadAsync
// lists the monitors; Subscribe calls its callback after the platform reported a possible change, on any thread. The
// watch rereads after each signal and reports the list when it differs. Linked by Runic.Desktop.Gtk4.
internal abstract class DesktopMonitorSource : IDesktopMonitors
{
    protected abstract ValueTask<PlatformResult<ImmutableArray<DesktopMonitor>>> ReadAsync(CancellationToken cancellationToken);

    protected abstract IDisposable Subscribe(Action changed);

    // Whether two lists describe the same monitors in the same order.
    internal static bool SameMonitors(ImmutableArray<DesktopMonitor> left, ImmutableArray<DesktopMonitor> right) =>
        left.IsDefault == right.IsDefault && (left.IsDefault || left.SequenceEqual(right));

    public ValueTask<PlatformResult<ImmutableArray<DesktopMonitor>>> GetMonitorsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ReadAsync(cancellationToken);
    }

    public async IAsyncEnumerable<PlatformResult<ImmutableArray<DesktopMonitor>>> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Subscribed before the first read, so a change during it is not lost.
        var signal = new SemaphoreSlim(0, 1);
        using var subscription = Subscribe(() =>
        {
            try { signal.Release(); }
            catch (SemaphoreFullException) { } // A reread is already pending.
        });
        var first = await ReadAsync(cancellationToken).ConfigureAwait(false);
        yield return first;
        if (first is not PlatformResult<ImmutableArray<DesktopMonitor>>.Success { Value: var previous }) yield break;
        while (true)
        {
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            var next = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (next is PlatformResult<ImmutableArray<DesktopMonitor>>.Success { Value: var monitors })
            {
                if (SameMonitors(previous, monitors)) continue;
                previous = monitors;
                yield return next;
                continue;
            }
            yield return next;
            yield break;
        }
    }
}
