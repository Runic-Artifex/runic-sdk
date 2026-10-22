using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Runic.Platform;

/// <summary>One storage volume: a filesystem the desktop can mount, or a locked encrypted volume.</summary>
/// <param name="Id">The platform's identifier, stable while the volume is present: a UDisks2 object path on Linux, a
/// <c>\\?\Volume{GUID}\</c> path on Windows, a BSD name on macOS. Treat it as opaque.</param>
/// <param name="DisplayName">A name to show, such as the volume's label or its drive's model and size.</param>
public sealed record DesktopVolume(string Id, string DisplayName)
{
    /// <summary>Where the volume is mounted; empty while it is not mounted. Linux can mount one volume at several paths.</summary>
    public ImmutableArray<string> MountPaths { get; init; } = [];
    /// <summary>The filesystem type, such as <c>ext4</c>, <c>vfat</c>, <c>NTFS</c> or <c>apfs</c>, when known.</summary>
    public string? FileSystem { get; init; }
    /// <summary>The volume's size in bytes, when known.</summary>
    public long? Size { get; init; }
    /// <summary>The identifier of the drive the volume belongs to, shared by its other volumes, when known.</summary>
    public string? DriveId { get; init; }
    /// <summary>A name for the drive, such as its vendor and model, when known.</summary>
    public string? DriveName { get; init; }
    /// <summary>Whether the volume or its media can be removed, such as a USB stick, an SD card or an optical disc.</summary>
    public bool IsRemovable { get; init; }
    /// <summary>Whether the volume is read-only.</summary>
    public bool IsReadOnly { get; init; }
    /// <summary>Whether the operating system considers the volume part of the system; file managers usually list such volumes separately.</summary>
    public bool IsSystem { get; init; }
    /// <summary>Whether the volume is encrypted and locked; it cannot be mounted until it is unlocked.</summary>
    public bool IsLocked { get; init; }
    /// <summary>Whether <see cref="IDesktopVolumes.MountAsync"/> can mount it now.</summary>
    public bool CanMount { get; init; }
    /// <summary>Whether <see cref="IDesktopVolumes.UnmountAsync"/> can unmount it now.</summary>
    public bool CanUnmount { get; init; }
    /// <summary>Whether <see cref="IDesktopVolumes.EjectAsync"/> can eject its media or drive.</summary>
    public bool CanEject { get; init; }
}

/// <summary>A change that <see cref="IDesktopVolumes.WatchAsync"/> reports.</summary>
/// <remarks>This set is closed: it gains cases only in a major release.</remarks>
public abstract record DesktopVolumeChange
{
    private DesktopVolumeChange() { }

    /// <summary>Every volume when the watch starts, and again after the volume service comes back.</summary>
    /// <param name="Volumes">The volumes.</param>
    public sealed record Snapshot(ImmutableArray<DesktopVolume> Volumes) : DesktopVolumeChange;
    /// <summary>A volume appeared, such as when a drive was plugged in.</summary>
    /// <param name="Volume">The volume.</param>
    public sealed record Added(DesktopVolume Volume) : DesktopVolumeChange;
    /// <summary>A volume changed, such as when it was mounted, unmounted or relabelled.</summary>
    /// <param name="Volume">The volume as it is now.</param>
    public sealed record Changed(DesktopVolume Volume) : DesktopVolumeChange;
    /// <summary>A volume disappeared.</summary>
    /// <param name="Id">The volume's <see cref="DesktopVolume.Id"/>.</param>
    public sealed record Removed(string Id) : DesktopVolumeChange;
}

/// <summary>The desktop's storage volumes: listing, mounting, unmounting, ejecting and change notifications.</summary>
/// <remarks>
/// <para>
/// Mounting and unmounting may ask the user for authorization, such as a Polkit password prompt on Linux. A refused
/// authorization is <see cref="PlatformFailureCode.PermissionDenied"/>, a dismissed prompt
/// <see cref="PlatformFailureCode.UserDismissed"/>, a volume in use <see cref="PlatformFailureCode.ResourceBusy"/>, a
/// locked encrypted volume <see cref="PlatformFailureCode.Locked"/>, and an identifier of a volume that is not present
/// <see cref="PlatformFailureCode.NotFound"/>. An operation the volume or platform does not offer, such as mounting on
/// Windows, which mounts volumes itself, is <see cref="PlatformUnavailableReason.BackendUnavailable"/>.
/// </para>
/// <para>Methods are safe to call from any thread; results and watch items arrive on the thread pool.</para>
/// </remarks>
public interface IDesktopVolumes : IAsyncDisposable
{
    /// <summary>Lists the present volumes.</summary>
    ValueTask<PlatformResult<ImmutableArray<DesktopVolume>>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Reports a <see cref="DesktopVolumeChange.Snapshot"/> of every volume, then each change as it happens.</summary>
    /// <remarks>
    /// When the volume service stops, the watch reports <see cref="PlatformResult{T}.Unavailable"/> and continues with a
    /// new snapshot once it is back. Without a volume service the watch reports
    /// <see cref="PlatformResult{T}.Unavailable"/> once and then waits.
    /// </remarks>
    IAsyncEnumerable<PlatformResult<DesktopVolumeChange>> WatchAsync(CancellationToken cancellationToken = default);

    /// <summary>Mounts a volume and returns it with its mount path.</summary>
    /// <param name="volumeId">The volume's <see cref="DesktopVolume.Id"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for the result; an authorization prompt the desktop already showed may stay open.</param>
    ValueTask<PlatformResult<DesktopVolume>> MountAsync(string volumeId, CancellationToken cancellationToken = default);

    /// <summary>Unmounts a volume from every mount path. A volume that is not mounted succeeds.</summary>
    /// <param name="volumeId">The volume's <see cref="DesktopVolume.Id"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for the result.</param>
    ValueTask<PlatformResult<PlatformUnit>> UnmountAsync(string volumeId, CancellationToken cancellationToken = default);

    /// <summary>Unmounts every volume of the volume's drive, then ejects its media or the drive itself.</summary>
    /// <param name="volumeId">The <see cref="DesktopVolume.Id"/> of any volume of the drive.</param>
    /// <param name="cancellationToken">Cancels waiting for the result.</param>
    ValueTask<PlatformResult<PlatformUnit>> EjectAsync(string volumeId, CancellationToken cancellationToken = default);
}

/// <summary>Volumes for hosts and platforms without a provider.</summary>
public static class DesktopVolumes
{
    /// <summary>Creates volumes that report every operation unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>
    /// <see cref="IDesktopVolumes.WatchAsync"/> reports that value once and then waits for cancellation or disposal.
    /// Nothing throws except for invalid arguments, caller cancellation and use after disposal.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not defined.</exception>
    public static IDesktopVolumes Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableVolumes(reason);
    }

    private sealed class UnavailableVolumes(PlatformUnavailableReason reason) : IDesktopVolumes, IDesktopServiceStatus
    {
        public CapabilityStatus Status { get; } = DesktopServiceStatus.Unavailable(reason);
        private readonly CancellationTokenSource _disposed = new();

        public ValueTask<PlatformResult<ImmutableArray<DesktopVolume>>> ListAsync(CancellationToken cancellationToken = default) =>
            Answer<ImmutableArray<DesktopVolume>>(cancellationToken);

        public async IAsyncEnumerable<PlatformResult<DesktopVolumeChange>> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
            linked.Token.ThrowIfCancellationRequested();
            yield return new PlatformResult<DesktopVolumeChange>.Unavailable(reason);
            await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
        }

        public ValueTask<PlatformResult<DesktopVolume>> MountAsync(string volumeId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(volumeId);
            return Answer<DesktopVolume>(cancellationToken);
        }

        public ValueTask<PlatformResult<PlatformUnit>> UnmountAsync(string volumeId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(volumeId);
            return Answer<PlatformUnit>(cancellationToken);
        }

        public ValueTask<PlatformResult<PlatformUnit>> EjectAsync(string volumeId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(volumeId);
            return Answer<PlatformUnit>(cancellationToken);
        }

        private ValueTask<PlatformResult<T>> Answer<T>(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<T>>(new PlatformResult<T>.Unavailable(reason));
        }

        public ValueTask DisposeAsync()
        {
            _disposed.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
