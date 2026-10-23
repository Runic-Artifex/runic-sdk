using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Runic.Platform.Runtime;

// The watch semantics every provider's IDesktopClipboard.WatchAsync shares: subscribe to the native change signal,
// report the formats now, then read them again after each signal. Signals that arrive while the reader is busy collapse
// into one. Only formats are read, never content.
internal static class ClipboardWatch
{
    // subscribe receives the signal, which may run on any thread, also during the call; it returns the subscription,
    // or null when the window cannot watch. A read that reports Unavailable ends the watch.
    internal static async IAsyncEnumerable<PlatformResult<DesktopClipboardFormats>> WatchAsync(
        Func<CancellationToken, ValueTask<PlatformResult<DesktopClipboardFormats>>> read,
        Func<Action, CancellationToken, ValueTask<IAsyncDisposable?>> subscribe,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        // Subscribed before the first read, so no change between the two is lost.
        var subscription = await subscribe(() => changes.Writer.TryWrite(true), cancellationToken).ConfigureAwait(false);
        try
        {
            if (subscription is null)
            {
                yield return new PlatformResult<DesktopClipboardFormats>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
                yield break;
            }
            while (true)
            {
                var formats = await read(cancellationToken).ConfigureAwait(false);
                yield return formats;
                if (formats is PlatformResult<DesktopClipboardFormats>.Unavailable) yield break;
                await changes.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (subscription is not null) await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }
}
