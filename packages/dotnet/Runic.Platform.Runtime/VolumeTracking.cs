using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Runic.Platform.Runtime;

// A coalescing wake-up: any number of Set calls before a wait end that one wait. The volume providers set it from
// native change callbacks.
internal sealed class VolumeSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    internal void Set() => _channel.Writer.TryWrite(true);

    // Waits for a Set or the timeout; null waits for a Set only.
    internal async ValueTask WaitAsync(TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (_channel.Reader.TryRead(out _)) return;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } after) limit.CancelAfter(after);
        try { await _channel.Reader.ReadAsync(limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
    }

    internal void Clear() => _channel.Reader.TryRead(out _);
}

// A listed volume and the identity of what is behind its ID. On macOS the ID is a BSD name, which the system hands to
// the next disk once a disk is gone, so the provider adds an identity of the disk's appearance; a volume with another
// identity under the same ID is a different volume. Null where the ID alone identifies the volume.
internal readonly record struct TrackedVolume(DesktopVolume Volume, string? Identity);

// What the Windows and macOS volume providers share: the order of a listing, the watch over successive listings and
// the size in a display name.
internal static class VolumeTracking
{
    internal static ImmutableArray<DesktopVolume> Sorted(IEnumerable<DesktopVolume> volumes) =>
        [.. volumes.OrderBy(static v => v.Id, StringComparer.Ordinal)];

    // Removals first, then additions and changes in ID order. An ID that now belongs to a volume of another identity
    // is the old volume's removal and the new one's addition, never a change; with no identity on either side the
    // ID decides.
    internal static IEnumerable<DesktopVolumeChange> Diff(ImmutableArray<TrackedVolume> previous, ImmutableArray<TrackedVolume> current)
    {
        var before = previous.ToDictionary(static v => v.Volume.Id, StringComparer.Ordinal);
        var after = current.ToDictionary(static v => v.Volume.Id, StringComparer.Ordinal);
        foreach (var id in before.Keys.Where(id => !after.TryGetValue(id, out var now) || Replaced(before[id], now)).OrderBy(static id => id, StringComparer.Ordinal))
            yield return new DesktopVolumeChange.Removed(id);
        foreach (var tracked in after.Values.OrderBy(static v => v.Volume.Id, StringComparer.Ordinal))
        {
            if (!before.TryGetValue(tracked.Volume.Id, out var old) || Replaced(old, tracked)) yield return new DesktopVolumeChange.Added(tracked.Volume);
            else if (!Same(old.Volume, tracked.Volume)) yield return new DesktopVolumeChange.Changed(tracked.Volume);
        }
    }

    private static bool Replaced(TrackedVolume before, TrackedVolume after) =>
        before.Identity is { } was && after.Identity is { } now && !string.Equals(was, now, StringComparison.Ordinal);

    internal static bool Same(DesktopVolume left, DesktopVolume right) =>
        left with { MountPaths = [] } == right with { MountPaths = [] } && left.MountPaths.SequenceEqual(right.MountPaths);

    // Lists the volumes, reports a Snapshot, then lists again after every signal (once settle has passed, so a burst
    // of native notifications is one listing) and every poll interval, and reports the differences. A listing that
    // is unavailable is reported once; the next successful one is a new Snapshot.
    internal static IAsyncEnumerable<PlatformResult<DesktopVolumeChange>> WatchAsync(
        Func<CancellationToken, ValueTask<PlatformResult<ImmutableArray<DesktopVolume>>>> list, VolumeSignal signal, TimeSpan? poll,
        TimeSpan settle, CancellationToken cancellationToken) =>
        WatchAsync(async token => await list(token).ConfigureAwait(false) switch
        {
            PlatformResult<ImmutableArray<DesktopVolume>>.Success(var volumes) =>
                new PlatformResult<ImmutableArray<TrackedVolume>>.Success([.. volumes.Select(static v => new TrackedVolume(v, null))]),
            PlatformResult<ImmutableArray<DesktopVolume>>.Unavailable(var reason) => new PlatformResult<ImmutableArray<TrackedVolume>>.Unavailable(reason),
            PlatformResult<ImmutableArray<DesktopVolume>>.Failed failed => new PlatformResult<ImmutableArray<TrackedVolume>>.Failed(failed.Code, failed.Diagnostic),
            _ => new PlatformResult<ImmutableArray<TrackedVolume>>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
        }, signal, poll, settle, cancellationToken);

    internal static async IAsyncEnumerable<PlatformResult<DesktopVolumeChange>> WatchAsync(
        Func<CancellationToken, ValueTask<PlatformResult<ImmutableArray<TrackedVolume>>>> list, VolumeSignal signal, TimeSpan? poll,
        TimeSpan settle, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ImmutableArray<TrackedVolume>? previous = null;
        bool announcedUnavailable = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listed = await list(cancellationToken).ConfigureAwait(false);
            if (listed is PlatformResult<ImmutableArray<TrackedVolume>>.Success(var current))
            {
                if (previous is not { } before)
                    yield return new PlatformResult<DesktopVolumeChange>.Success(new DesktopVolumeChange.Snapshot([.. current.Select(static v => v.Volume)]));
                else foreach (var change in Diff(before, current)) yield return new PlatformResult<DesktopVolumeChange>.Success(change);
                previous = current;
                announcedUnavailable = false;
            }
            else if (!announcedUnavailable)
            {
                previous = null;
                announcedUnavailable = true;
                yield return listed switch
                {
                    PlatformResult<ImmutableArray<TrackedVolume>>.Failed failed => new PlatformResult<DesktopVolumeChange>.Failed(failed.Code, failed.Diagnostic),
                    PlatformResult<ImmutableArray<TrackedVolume>>.Unavailable(var reason) => new PlatformResult<DesktopVolumeChange>.Unavailable(reason),
                    _ => new PlatformResult<DesktopVolumeChange>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
                };
            }
            await signal.WaitAsync(poll, cancellationToken).ConfigureAwait(false);
            if (settle > TimeSpan.Zero)
            {
                await Task.Delay(settle, cancellationToken).ConfigureAwait(false);
                signal.Clear();
            }
        }
    }

    internal static string Bytes(long size) => size switch
    {
        >= 1_000_000_000_000 => string.Create(CultureInfo.CurrentCulture, $"{size / 1_000_000_000_000.0:0.#} TB"),
        >= 1_000_000_000 => string.Create(CultureInfo.CurrentCulture, $"{size / 1_000_000_000.0:0.#} GB"),
        >= 1_000_000 => string.Create(CultureInfo.CurrentCulture, $"{size / 1_000_000.0:0.#} MB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{size} B"),
    };
}
