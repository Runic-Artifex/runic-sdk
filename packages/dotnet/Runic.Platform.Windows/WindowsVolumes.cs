using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

// What WindowsVolumes needs from Windows; WindowsVolumeSource is the native one, tests use a fake.
internal interface IWindowsVolumeSource
{
    // Lists the volumes that have a recognized filesystem. Runs on a thread-pool thread.
    PlatformResult<ImmutableArray<DesktopVolume>> List();

    // Calls changed when a volume arrives or leaves; null when Windows offers no notification.
    IDisposable? Subscribe(Action changed);

    // Safely removes the drive (its DriveId), as the notification area's "Safely Remove Hardware" does, or opens the
    // tray of an optical drive that is no removable device of its own.
    PlatformResult<PlatformUnit> Eject(string volumeId, string? driveId);

    // Shows Explorer's BitLocker unlock prompt for a locked volume and waits until it closes.
    Task<PlatformResult<PlatformUnit>> PromptUnlockAsync(string volumeId, CancellationToken cancellationToken);
}

// Volumes from the volume management API. Windows mounts every volume it recognizes itself, at its drive letters and
// mounted folders, so MountAsync and UnmountAsync only answer for volumes already in the state asked for; EjectAsync
// safely removes the drive, which dismounts its volumes first, or opens an optical drive's tray. A locked BitLocker
// volume unlocks through Explorer's prompt; a passphrase needs an administrator, so that overload keeps its default.
internal sealed class WindowsVolumes(IWindowsVolumeSource source, TimeSpan poll, TimeSpan settle) : IDesktopVolumes
{
    // Drive letters, mounted folders, labels and media in a drive that stays present have no volume notification:
    // the watch lists again at this interval as well.
    internal static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(2);
    // A drive letter follows its volume's arrival a moment later.
    internal static readonly TimeSpan DefaultSettle = TimeSpan.FromMilliseconds(300);

    private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    private readonly CancellationTokenSource _disposed = new();

    public async ValueTask<PlatformResult<ImmutableArray<DesktopVolume>>> ListAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(List, cancellationToken).ConfigureAwait(false);
    }

    // The system's own disk is never offered for removal, even where its controller is removable, as a virtual
    // machine's SCSI disk can be.
    private PlatformResult<ImmutableArray<DesktopVolume>> List()
    {
        var listed = source.List();
        if (listed is not PlatformResult<ImmutableArray<DesktopVolume>>.Success(var volumes)) return listed;
        var systemDrives = volumes.Where(static v => v.IsSystem && v.DriveId is not null).Select(static v => v.DriveId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new PlatformResult<ImmutableArray<DesktopVolume>>.Success(systemDrives.Count == 0 ? volumes
            : [.. volumes.Select(v => v.CanEject && v.DriveId is { } drive && systemDrives.Contains(drive) ? v with { CanEject = false } : v)]);
    }

    public async ValueTask<PlatformResult<DesktopVolume>> MountAsync(string volumeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeId);
        return await FindAsync(volumeId, cancellationToken).ConfigureAwait(false) switch
        {
            PlatformResult<DesktopVolume>.Success { Value.IsLocked: true } => new PlatformResult<DesktopVolume>.Failed(PlatformFailureCode.Locked),
            PlatformResult<DesktopVolume>.Success { Value.MountPaths.IsEmpty: false } mounted => mounted,
            PlatformResult<DesktopVolume>.Success => new PlatformResult<DesktopVolume>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            var other => other,
        };
    }

    public async ValueTask<PlatformResult<PlatformUnit>> UnmountAsync(string volumeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeId);
        return await FindAsync(volumeId, cancellationToken).ConfigureAwait(false) switch
        {
            PlatformResult<DesktopVolume>.Success { Value.MountPaths.IsEmpty: true } => Done,
            // Dismounting needs an administrator and Windows mounts the volume again on its next access.
            PlatformResult<DesktopVolume>.Success => new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            var other => Forward<PlatformUnit>(other),
        };
    }

    public async ValueTask<PlatformResult<PlatformUnit>> EjectAsync(string volumeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeId);
        var found = await FindAsync(volumeId, cancellationToken).ConfigureAwait(false);
        if (found is not PlatformResult<DesktopVolume>.Success(var volume)) return Forward<PlatformUnit>(found);
        if (!volume.CanEject) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => source.Eject(volume.Id, volume.DriveId), cancellationToken).ConfigureAwait(false);
    }

    // A locked BitLocker volume opens Explorer's prompt; the volume is unlocked if it no longer is locked afterwards.
    public async ValueTask<PlatformResult<DesktopVolume>> UnlockAsync(string volumeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeId);
        var found = await FindAsync(volumeId, cancellationToken).ConfigureAwait(false);
        if (found is not PlatformResult<DesktopVolume>.Success(var volume) || !volume.IsLocked) return found;
        if (volume.UnlockMethod != DesktopVolumeUnlockMethod.SystemPrompt) return new PlatformResult<DesktopVolume>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        cancellationToken.ThrowIfCancellationRequested();
        var prompted = await source.PromptUnlockAsync(volumeId, cancellationToken).ConfigureAwait(false);
        if (prompted is not PlatformResult<PlatformUnit>.Success) return Forward<PlatformUnit, DesktopVolume>(prompted);
        return await FindAsync(volumeId, cancellationToken).ConfigureAwait(false) switch
        {
            PlatformResult<DesktopVolume>.Success { Value.IsLocked: true } => new PlatformResult<DesktopVolume>.Failed(PlatformFailureCode.UserDismissed),
            var other => other,
        };
    }

    public async IAsyncEnumerable<PlatformResult<DesktopVolumeChange>> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        var signal = new VolumeSignal();
        using var subscription = source.Subscribe(signal.Set);
        await foreach (var change in VolumeTracking.WatchAsync(ListOnceAsync, signal, poll, settle, linked.Token).ConfigureAwait(false))
            yield return change;

        async ValueTask<PlatformResult<ImmutableArray<DesktopVolume>>> ListOnceAsync(CancellationToken token) =>
            await Task.Run(List, token).ConfigureAwait(false);
    }

    private async ValueTask<PlatformResult<DesktopVolume>> FindAsync(string volumeId, CancellationToken cancellationToken) =>
        await ListAsync(cancellationToken).ConfigureAwait(false) switch
        {
            PlatformResult<ImmutableArray<DesktopVolume>>.Success(var volumes) =>
                volumes.FirstOrDefault(v => string.Equals(v.Id, volumeId, StringComparison.OrdinalIgnoreCase)) is { } volume
                    ? new PlatformResult<DesktopVolume>.Success(volume)
                    : new PlatformResult<DesktopVolume>.Failed(PlatformFailureCode.NotFound),
            PlatformResult<ImmutableArray<DesktopVolume>>.Unavailable(var reason) => new PlatformResult<DesktopVolume>.Unavailable(reason),
            PlatformResult<ImmutableArray<DesktopVolume>>.Failed failed => new PlatformResult<DesktopVolume>.Failed(failed.Code, failed.Diagnostic),
            _ => throw new InvalidOperationException(),
        };

    private static PlatformResult<T> Forward<T>(PlatformResult<DesktopVolume> result) => Forward<DesktopVolume, T>(result);

    private static PlatformResult<TOut> Forward<TIn, TOut>(PlatformResult<TIn> result) => result switch
    {
        PlatformResult<TIn>.Unavailable(var reason) => new PlatformResult<TOut>.Unavailable(reason),
        PlatformResult<TIn>.Failed failed => new PlatformResult<TOut>.Failed(failed.Code, failed.Diagnostic),
        _ => throw new InvalidOperationException(),
    };

    public ValueTask DisposeAsync()
    {
        _disposed.Cancel();
        return ValueTask.CompletedTask;
    }
}
