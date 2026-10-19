using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Runic.Platform.Runtime;

namespace Runic.Platform.MacOS;

// One disk as Disk Arbitration describes it (DADiskCopyDescription), with the BSD name of the physical whole disk
// below it in the I/O Registry: an APFS volume sits on a synthesized container disk, which sits on a partition of
// the drive's own whole disk.
internal sealed record MacDisk(string BsdName)
{
    internal string? VolumeKind { get; init; }
    // The partition type, such as the EFI system partition's GUID.
    internal string? Content { get; init; }
    internal bool Mountable { get; init; }
    internal string? VolumeName { get; init; }
    internal string? VolumePath { get; init; }
    // Mounted with MNT_DONTBROWSE (nobrowse), which Finder does not show, such as Cryptex and simulator images.
    internal bool Hidden { get; init; }
    internal long? Size { get; init; }
    internal bool Whole { get; init; }
    internal bool Removable { get; init; }
    internal bool Ejectable { get; init; }
    internal bool Writable { get; init; } = true;
    internal bool Internal { get; init; }
    internal string? Vendor { get; init; }
    internal string? Model { get; init; }
    internal string? PhysicalDisk { get; init; }
}

// What MacVolumes needs from Disk Arbitration; MacVolumeSource is the native one, tests use a fake.
internal interface IMacVolumeSource
{
    // Every disk Disk Arbitration reported; Unavailable when no session could start.
    PlatformResult<ImmutableArray<MacDisk>> Disks();

    // Calls changed when a disk appears, disappears or its description changes; null without a session.
    IDisposable? Subscribe(Action changed);

    // DADiskMount at the default location; the result is the mount path.
    Task<PlatformResult<string?>> MountAsync(string bsdName);
    Task<PlatformResult<PlatformUnit>> UnmountAsync(string bsdName);
    // DADiskEject of a whole disk.
    Task<PlatformResult<PlatformUnit>> EjectAsync(string bsdName);
}

// Volumes from Disk Arbitration, as Finder and Disk Utility see them. Disk Arbitration reports every disk with its
// description and asks dissenters, such as an application with a file open, before it unmounts or ejects.
internal sealed class MacVolumes(IMacVolumeSource source, TimeSpan settle) : IDesktopVolumes
{
    internal static readonly TimeSpan DefaultSettle = TimeSpan.FromMilliseconds(100);
    private const int MountPathAttempts = 20;
    private static readonly TimeSpan MountPathDelay = TimeSpan.FromMilliseconds(100);
    private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    private readonly CancellationTokenSource _disposed = new();

    public async ValueTask<PlatformResult<ImmutableArray<DesktopVolume>>> ListAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(List, cancellationToken).ConfigureAwait(false);
    }

    private PlatformResult<ImmutableArray<DesktopVolume>> List() => source.Disks() switch
    {
        PlatformResult<ImmutableArray<MacDisk>>.Success(var disks) => new PlatformResult<ImmutableArray<DesktopVolume>>.Success(Volumes(disks)),
        PlatformResult<ImmutableArray<MacDisk>>.Unavailable(var reason) => new PlatformResult<ImmutableArray<DesktopVolume>>.Unavailable(reason),
        PlatformResult<ImmutableArray<MacDisk>>.Failed failed => new PlatformResult<ImmutableArray<DesktopVolume>>.Failed(failed.Code, failed.Diagnostic),
        _ => throw new InvalidOperationException(),
    };

    public async ValueTask<PlatformResult<DesktopVolume>> MountAsync(string volumeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeId);
        var found = await FindAsync(volumeId, cancellationToken).ConfigureAwait(false);
        if (found is not PlatformResult<DesktopVolume>.Success(var volume)) return found;
        if (!volume.MountPaths.IsEmpty) return found;
        if (!volume.CanMount) return new PlatformResult<DesktopVolume>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        cancellationToken.ThrowIfCancellationRequested();
        var mounted = await source.MountAsync(volumeId).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (mounted is not PlatformResult<string?>.Success(var path)) return Forward<string?, DesktopVolume>(mounted);
        // The new mount path also arrives as a description change, which can follow the mount's completion; without
        // a path from the completion, wait a moment for it.
        var refreshed = volume;
        for (int attempt = 0; attempt < MountPathAttempts; attempt++)
        {
            if (await FindAsync(volumeId, cancellationToken).ConfigureAwait(false) is PlatformResult<DesktopVolume>.Success(var current)) refreshed = current;
            if (path is not null || !refreshed.MountPaths.IsEmpty) break;
            await Task.Delay(MountPathDelay, cancellationToken).ConfigureAwait(false);
        }
        if (path is not null && !refreshed.MountPaths.Contains(path))
            refreshed = refreshed with { MountPaths = [path], CanMount = false, CanUnmount = !refreshed.IsSystem };
        return new PlatformResult<DesktopVolume>.Success(refreshed);
    }

    public async ValueTask<PlatformResult<PlatformUnit>> UnmountAsync(string volumeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeId);
        var found = await FindAsync(volumeId, cancellationToken).ConfigureAwait(false);
        if (found is not PlatformResult<DesktopVolume>.Success(var volume)) return Forward<DesktopVolume, PlatformUnit>(found);
        if (volume.MountPaths.IsEmpty) return Done;
        // The startup disk's own volumes are never unmounted.
        if (!volume.CanUnmount) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        cancellationToken.ThrowIfCancellationRequested();
        return await source.UnmountAsync(volumeId).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PlatformResult<PlatformUnit>> EjectAsync(string volumeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeId);
        var listed = await ListAsync(cancellationToken).ConfigureAwait(false);
        if (listed is not PlatformResult<ImmutableArray<DesktopVolume>>.Success(var volumes)) return Forward<ImmutableArray<DesktopVolume>, PlatformUnit>(listed);
        if (volumes.FirstOrDefault(v => v.Id == volumeId) is not { } volume) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.NotFound);
        if (!volume.CanEject || volume.DriveId is not { } drive) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        // Every mounted volume of the drive goes first; a busy one stops the eject.
        foreach (var other in volumes.Where(v => v.DriveId == drive && !v.MountPaths.IsEmpty))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await source.UnmountAsync(other.Id).WaitAsync(cancellationToken).ConfigureAwait(false) is not PlatformResult<PlatformUnit>.Success and var failed)
                return failed;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return await source.EjectAsync(drive).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<PlatformResult<DesktopVolumeChange>> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        var signal = new VolumeSignal();
        using var subscription = source.Subscribe(signal.Set);
        if (subscription is null)
        {
            // Without Disk Arbitration nothing will change: report it once, as an unavailable provider does.
            yield return new PlatformResult<DesktopVolumeChange>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
            yield break;
        }
        // Disk Arbitration reports every change, so the watch lists again only when it does.
        await foreach (var change in VolumeTracking.WatchAsync(ListOnceAsync, signal, null, settle, linked.Token).ConfigureAwait(false))
            yield return change;

        async ValueTask<PlatformResult<ImmutableArray<DesktopVolume>>> ListOnceAsync(CancellationToken token) =>
            await Task.Run(List, token).ConfigureAwait(false);
    }

    // The volumes among the disks: a disk with a filesystem Disk Arbitration can mount. A volume belongs to the drive
    // of its physical whole disk. Volumes mounted at /, under /System or hidden from Finder, the unmounted volumes of
    // their drives, such as Recovery, and EFI system partitions belong to the system; a drive with such a mounted
    // volume is never ejected.
    internal static ImmutableArray<DesktopVolume> Volumes(ImmutableArray<MacDisk> disks)
    {
        var byName = disks.ToDictionary(static d => d.BsdName, StringComparer.Ordinal);
        var systemDrives = disks.Where(Protected).Select(Drive).ToHashSet(StringComparer.Ordinal);
        var volumes = new List<DesktopVolume>();
        foreach (var disk in disks)
        {
            if (disk.VolumeKind is null && !disk.Mountable) continue;
            string drive = Drive(disk);
            var physical = byName.GetValueOrDefault(drive) ?? disk;
            string driveName = string.Join(' ', new[] { physical.Vendor, physical.Model }.Where(static s => !string.IsNullOrWhiteSpace(s))).Trim();
            bool mounted = disk.VolumePath is not null;
            bool system = Protected(disk) || (!mounted && systemDrives.Contains(drive))
                || string.Equals(disk.Content, EfiSystemPartition, StringComparison.OrdinalIgnoreCase) || disk.Content == "EFI";
            string name = !string.IsNullOrWhiteSpace(disk.VolumeName) ? disk.VolumeName!
                : driveName.Length > 0 ? disk.Size is { } size ? $"{driveName} ({VolumeTracking.Bytes(size)})" : driveName
                : disk.BsdName;
            volumes.Add(new DesktopVolume(disk.BsdName, name)
            {
                MountPaths = mounted ? [disk.VolumePath!] : [],
                FileSystem = disk.VolumeKind,
                Size = disk.Size,
                DriveId = drive,
                DriveName = driveName.Length > 0 ? driveName : null,
                IsRemovable = physical.Removable || physical.Ejectable || !physical.Internal,
                IsReadOnly = !disk.Writable,
                IsSystem = system,
                CanMount = !mounted && disk.Mountable && !system,
                CanUnmount = mounted && !system,
                CanEject = physical.Ejectable && !systemDrives.Contains(drive),
            });
        }
        return VolumeTracking.Sorted(volumes);
    }

    private const string EfiSystemPartition = "C12A7328-F81F-11D2-BA4B-00A0C93EC93B";

    // Mounted by the system for itself: the startup volume, the volumes under /System, and the ones Finder hides.
    private static bool Protected(MacDisk disk) =>
        disk.VolumePath is "/" || disk.VolumePath?.StartsWith("/System/", StringComparison.Ordinal) == true || (disk.VolumePath is not null && disk.Hidden);

    private static string Drive(MacDisk disk) => disk.PhysicalDisk ?? disk.BsdName;

    private async ValueTask<PlatformResult<DesktopVolume>> FindAsync(string volumeId, CancellationToken cancellationToken) =>
        await ListAsync(cancellationToken).ConfigureAwait(false) switch
        {
            PlatformResult<ImmutableArray<DesktopVolume>>.Success(var volumes) => volumes.FirstOrDefault(v => v.Id == volumeId) is { } volume
                ? new PlatformResult<DesktopVolume>.Success(volume) : new PlatformResult<DesktopVolume>.Failed(PlatformFailureCode.NotFound),
            var other => Forward<ImmutableArray<DesktopVolume>, DesktopVolume>(other),
        };

    private static PlatformResult<TOut> Forward<TIn, TOut>(PlatformResult<TIn> result) => result switch
    {
        PlatformResult<TIn>.Unavailable(var reason) => new PlatformResult<TOut>.Unavailable(reason),
        PlatformResult<TIn>.Failed failed => new PlatformResult<TOut>.Failed(failed.Code, failed.Diagnostic),
        _ => throw new InvalidOperationException(),
    };

    public ValueTask DisposeAsync()
    {
        if (!_disposed.IsCancellationRequested)
        {
            _disposed.Cancel();
            if (source is IDisposable disposable) disposable.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
