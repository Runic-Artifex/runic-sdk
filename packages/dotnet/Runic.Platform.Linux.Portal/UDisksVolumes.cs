using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Linux.Portal.UDisks;

namespace Runic.Platform.Linux.Portal;

// Volumes from UDisks2 on the system bus, through proxies generated from UDisks/org.freedesktop.UDisks2.xml.
// UDisks2 asks Polkit itself; calls allow interactive authorization so it can show the prompt.
internal sealed class UDisksVolumes(string? address = null, string service = UDisksVolumes.ServiceName) : IDesktopVolumes
{
    internal const string ServiceName = "org.freedesktop.UDisks2";
    private const string BlockInterface = "org.freedesktop.UDisks2.Block";
    private const string FilesystemInterface = "org.freedesktop.UDisks2.Filesystem";
    private const string EncryptedInterface = "org.freedesktop.UDisks2.Encrypted";
    private const string DriveInterface = "org.freedesktop.UDisks2.Drive";
    private static readonly DBusObjectPath Root = new("/org/freedesktop/UDisks2");
    private static readonly ImmutableDictionary<string, DBusValue> Interactive =
        ImmutableDictionary<string, DBusValue>.Empty.Add("auth.no_user_interaction", DBusValue.From(false));

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposed = new();
    private DBusConnection? _connection;

    public async ValueTask<PlatformResult<ImmutableArray<DesktopVolume>>> ListAsync(CancellationToken cancellationToken = default)
    {
        var objects = await ObjectsAsync(cancellationToken).ConfigureAwait(false);
        return objects switch
        {
            PlatformResult<Model>.Success(var model) => new PlatformResult<ImmutableArray<DesktopVolume>>.Success([.. model.Volumes().Values.OrderBy(static v => v.Id, StringComparer.Ordinal)]),
            PlatformResult<Model>.Unavailable(var reason) => new PlatformResult<ImmutableArray<DesktopVolume>>.Unavailable(reason),
            PlatformResult<Model>.Failed failed => new PlatformResult<ImmutableArray<DesktopVolume>>.Failed(failed.Code, failed.Diagnostic),
            _ => throw new InvalidOperationException(),
        };
    }

    public async ValueTask<PlatformResult<DesktopVolume>> MountAsync(string volumeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeId);
        var found = await VolumeAsync(volumeId, cancellationToken).ConfigureAwait(false);
        if (found is not PlatformResult<(DBusConnection, Model, DesktopVolume)>.Success((var connection, var model, var volume))) return Forward<DesktopVolume>(found);
        if (volume.IsLocked) return new PlatformResult<DesktopVolume>.Failed(PlatformFailureCode.Locked);
        if (!model.Has(volumeId, FilesystemInterface)) return new PlatformResult<DesktopVolume>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        if (!volume.MountPaths.IsEmpty) return new PlatformResult<DesktopVolume>.Success(volume);
        try
        {
            string path = await new Filesystem(connection, service, volumeId).MountAsync(Interactive, DBusCallOptions.AllowInteractiveAuthorization, cancellationToken)
                .ConfigureAwait(false);
            // UDisks2 announces the new mount point separately; report it now either way.
            var refreshed = await ObjectsAsync(cancellationToken).ConfigureAwait(false) is PlatformResult<Model>.Success(var after) && after.Volumes().TryGetValue(volumeId, out var current)
                ? current : volume;
            if (!refreshed.MountPaths.Contains(path)) refreshed = refreshed with { MountPaths = refreshed.MountPaths.Add(path), CanMount = false, CanUnmount = true };
            return new PlatformResult<DesktopVolume>.Success(refreshed);
        }
        catch (DBusException error) { return Map<DesktopVolume>(error); }
    }

    public async ValueTask<PlatformResult<PlatformUnit>> UnmountAsync(string volumeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeId);
        var found = await VolumeAsync(volumeId, cancellationToken).ConfigureAwait(false);
        if (found is not PlatformResult<(DBusConnection, Model, DesktopVolume)>.Success((var connection, var model, var volume))) return Forward<PlatformUnit>(found);
        if (!model.Has(volumeId, FilesystemInterface)) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        return volume.MountPaths.IsEmpty ? Done : await UnmountAsync(connection, volumeId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PlatformResult<PlatformUnit>> EjectAsync(string volumeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeId);
        var found = await VolumeAsync(volumeId, cancellationToken).ConfigureAwait(false);
        if (found is not PlatformResult<(DBusConnection, Model, DesktopVolume)>.Success((var connection, var model, var volume))) return Forward<PlatformUnit>(found);
        if (!volume.CanEject || volume.DriveId is not { } drive) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        // Every mounted volume of the drive goes first; a busy one stops the eject.
        foreach (var other in model.Volumes().Values.Where(v => v.DriveId == drive && !v.MountPaths.IsEmpty).OrderBy(static v => v.Id, StringComparer.Ordinal))
            if (await UnmountAsync(connection, other.Id, cancellationToken).ConfigureAwait(false) is not PlatformResult<PlatformUnit>.Success and var failed) return failed;
        try
        {
            await new Drive(connection, service, drive).EjectAsync(Interactive, DBusCallOptions.AllowInteractiveAuthorization, cancellationToken).ConfigureAwait(false);
            return Done;
        }
        catch (DBusException error) { return Map<PlatformUnit>(error); }
    }

    private async ValueTask<PlatformResult<PlatformUnit>> UnmountAsync(DBusConnection connection, string volumeId, CancellationToken cancellationToken)
    {
        try
        {
            await new Filesystem(connection, service, volumeId).UnmountAsync(Interactive, DBusCallOptions.AllowInteractiveAuthorization, cancellationToken)
                .ConfigureAwait(false);
            return Done;
        }
        catch (DBusException error) when (error.ErrorName == "org.freedesktop.UDisks2.Error.NotMounted") { return Done; }
        catch (DBusException error) { return Map<PlatformUnit>(error); }
    }

    public async IAsyncEnumerable<PlatformResult<DesktopVolumeChange>> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        var token = linked.Token;
        if (await ConnectAsync(token).ConfigureAwait(false) is not PlatformResult<DBusConnection>.Success(var connection))
        {
            // Without a system bus nothing will change: report it once, as an unavailable provider does.
            yield return new PlatformResult<DesktopVolumeChange>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            yield break;
        }
        var signals = Channel.CreateUnbounded<DBusSignal>(new UnboundedChannelOptions { SingleReader = true });
        var subscriptions = new List<DBusSignalSubscription>();
        try
        {
            // Subscribe before reading the objects, so no change between the two is lost.
            foreach (var filter in new[]
            {
                new DBusSignalFilter { Sender = service, Path = Root, Interface = "org.freedesktop.DBus.ObjectManager" },
                new DBusSignalFilter { Sender = service, PathNamespace = Root, Interface = "org.freedesktop.DBus.Properties", Member = "PropertiesChanged" },
                new DBusSignalFilter { Sender = "org.freedesktop.DBus", Interface = "org.freedesktop.DBus", Member = "NameOwnerChanged", Arg0 = service },
            })
                subscriptions.Add(await connection.SubscribeAsync(filter, token).ConfigureAwait(false));
        }
        catch (DBusException) when (!token.IsCancellationRequested)
        {
            foreach (var subscription in subscriptions) await subscription.DisposeAsync().ConfigureAwait(false);
            subscriptions.Clear();
        }
        if (subscriptions.Count == 0)
        {
            yield return new PlatformResult<DesktopVolumeChange>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            yield break;
        }
        var pumps = subscriptions.Select(subscription => PumpAsync(subscription, signals.Writer, token)).ToArray();
        _ = Task.WhenAll(pumps).ContinueWith(task => signals.Writer.TryComplete(task.Exception?.InnerException), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            Model? model = null;
            var previous = ImmutableDictionary<string, DesktopVolume>.Empty;
            bool announcedUnavailable = false;
            while (!token.IsCancellationRequested)
            {
                if (model is null)
                {
                    var objects = await FetchAsync(connection, token).ConfigureAwait(false);
                    if (objects is PlatformResult<Model>.Success(var fetched))
                    {
                        model = fetched;
                        // Signals that arrived while fetching are older than or as new as the objects: apply them, then report.
                        while (signals.Reader.TryRead(out var queued)) await ApplyAsync(connection, model, queued, token).ConfigureAwait(false);
                        previous = model.Volumes();
                        announcedUnavailable = false;
                        yield return new PlatformResult<DesktopVolumeChange>.Success(
                            new DesktopVolumeChange.Snapshot([.. previous.Values.OrderBy(static v => v.Id, StringComparer.Ordinal)]));
                    }
                    else if (!announcedUnavailable)
                    {
                        announcedUnavailable = true;
                        yield return new PlatformResult<DesktopVolumeChange>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
                    }
                }
                DBusSignal signal;
                try { signal = await signals.Reader.ReadAsync(token).ConfigureAwait(false); }
                catch (ChannelClosedException) { break; }
                if (signal.Member == "NameOwnerChanged")
                {
                    bool running = signal.Arguments is [_, _, var owner] && owner.GetString().Length > 0;
                    if (!running && model is not null)
                    {
                        model = null;
                        previous = ImmutableDictionary<string, DesktopVolume>.Empty;
                        announcedUnavailable = true;
                        yield return new PlatformResult<DesktopVolumeChange>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
                    }
                    continue;
                }
                if (model is null) continue;
                await ApplyAsync(connection, model, signal, token).ConfigureAwait(false);
                var current = model.Volumes();
                foreach (var change in Diff(previous, current)) yield return new PlatformResult<DesktopVolumeChange>.Success(change);
                previous = current;
            }
        }
        finally
        {
            foreach (var subscription in subscriptions) await subscription.DisposeAsync().ConfigureAwait(false);
        }
        if (!token.IsCancellationRequested) yield return new PlatformResult<DesktopVolumeChange>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        token.ThrowIfCancellationRequested();
    }

    private static async Task PumpAsync(DBusSignalSubscription subscription, ChannelWriter<DBusSignal> writer, CancellationToken cancellationToken)
    {
        await foreach (var signal in subscription.WithCancellation(cancellationToken).ConfigureAwait(false)) writer.TryWrite(signal);
    }

    private async ValueTask ApplyAsync(DBusConnection connection, Model model, DBusSignal signal, CancellationToken cancellationToken)
    {
        try
        {
            switch (signal.Member)
            {
                case "InterfacesAdded":
                    var (added, interfaces) = ObjectManager.ReadInterfacesAdded(signal.Arguments);
                    foreach (var (name, properties) in interfaces) model.Set(added.Value, name, properties);
                    break;
                case "InterfacesRemoved":
                    var (removed, names) = ObjectManager.ReadInterfacesRemoved(signal.Arguments);
                    foreach (var name in names) model.Remove(removed.Value, name);
                    break;
                case "PropertiesChanged" when DBusProxy.ReadPropertyChanges(signal) is { } changes:
                    model.Update(signal.Path.Value, changes.Interface, changes.Changed);
                    if (!changes.Invalidated.IsEmpty)
                        model.Set(signal.Path.Value, changes.Interface,
                            await new DBusProxy(connection, service, signal.Path).GetAllPropertiesAsync(changes.Interface, cancellationToken).ConfigureAwait(false));
                    break;
            }
        }
        catch (DBusException) { /* A malformed or vanished object changes nothing; the next snapshot corrects the model. */ }
    }

    internal static IEnumerable<DesktopVolumeChange> Diff(ImmutableDictionary<string, DesktopVolume> previous, ImmutableDictionary<string, DesktopVolume> current)
    {
        foreach (var id in previous.Keys.Where(id => !current.ContainsKey(id)).OrderBy(static id => id, StringComparer.Ordinal))
            yield return new DesktopVolumeChange.Removed(id);
        foreach (var volume in current.Values.OrderBy(static v => v.Id, StringComparer.Ordinal))
        {
            if (!previous.TryGetValue(volume.Id, out var before)) yield return new DesktopVolumeChange.Added(volume);
            else if (!Same(before, volume)) yield return new DesktopVolumeChange.Changed(volume);
        }
    }

    private static bool Same(DesktopVolume left, DesktopVolume right) =>
        left with { MountPaths = [] } == right with { MountPaths = [] } && left.MountPaths.SequenceEqual(right.MountPaths);

    private async ValueTask<PlatformResult<(DBusConnection, Model, DesktopVolume)>> VolumeAsync(string volumeId, CancellationToken cancellationToken)
    {
        if (await ConnectAsync(cancellationToken).ConfigureAwait(false) is not PlatformResult<DBusConnection>.Success(var connection))
            return new PlatformResult<(DBusConnection, Model, DesktopVolume)>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        return await FetchAsync(connection, cancellationToken).ConfigureAwait(false) switch
        {
            PlatformResult<Model>.Success(var model) when model.Volumes().TryGetValue(volumeId, out var volume) =>
                new PlatformResult<(DBusConnection, Model, DesktopVolume)>.Success((connection, model, volume)),
            PlatformResult<Model>.Success => new PlatformResult<(DBusConnection, Model, DesktopVolume)>.Failed(PlatformFailureCode.NotFound),
            PlatformResult<Model>.Unavailable(var reason) => new PlatformResult<(DBusConnection, Model, DesktopVolume)>.Unavailable(reason),
            PlatformResult<Model>.Failed failed => new PlatformResult<(DBusConnection, Model, DesktopVolume)>.Failed(failed.Code, failed.Diagnostic),
            _ => throw new InvalidOperationException(),
        };
    }

    private async ValueTask<PlatformResult<Model>> ObjectsAsync(CancellationToken cancellationToken) =>
        await ConnectAsync(cancellationToken).ConfigureAwait(false) is PlatformResult<DBusConnection>.Success(var connection)
            ? await FetchAsync(connection, cancellationToken).ConfigureAwait(false)
            : new PlatformResult<Model>.Unavailable(PlatformUnavailableReason.BackendUnavailable);

    private async ValueTask<PlatformResult<Model>> FetchAsync(DBusConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var objects = await new ObjectManager(connection, service, Root).GetManagedObjectsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return new PlatformResult<Model>.Success(new Model(objects));
        }
        catch (DBusException error) { return Map<Model>(error); }
    }

    private async ValueTask<PlatformResult<DBusConnection>> ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
            if (_connection is { Disconnected.IsCancellationRequested: false } current) return new PlatformResult<DBusConnection>.Success(current);
            if (_connection is { } closed) await closed.DisposeAsync().ConfigureAwait(false);
            _connection = null;
            var result = address is null
                ? await DBusConnection.ConnectAsync(DBusBus.System, cancellationToken).ConfigureAwait(false)
                : await DBusConnection.ConnectToAddressAsync(address, cancellationToken).ConfigureAwait(false);
            if (result is PlatformResult<DBusConnection>.Success(var connection)) _connection = connection;
            return result;
        }
        finally { _gate.Release(); }
    }

    // UDisks2 errors (https://storaged.org/doc/udisks2-api/latest/UDisksError.html) and bus errors.
    internal static PlatformResult<T> Map<T>(DBusException error)
    {
        var diagnostic = new PlatformDiagnostic(error.ErrorName, 0);
        return error.ErrorName switch
        {
            "org.freedesktop.UDisks2.Error.NotAuthorized" or "org.freedesktop.UDisks2.Error.NotAuthorizedCanObtain" or DBusException.AccessDenied =>
                new PlatformResult<T>.Failed(PlatformFailureCode.PermissionDenied, diagnostic),
            "org.freedesktop.UDisks2.Error.NotAuthorizedDismissed" => new PlatformResult<T>.Failed(PlatformFailureCode.UserDismissed, diagnostic),
            "org.freedesktop.UDisks2.Error.DeviceBusy" => new PlatformResult<T>.Failed(PlatformFailureCode.ResourceBusy, diagnostic),
            "org.freedesktop.UDisks2.Error.AlreadyMounted" or "org.freedesktop.UDisks2.Error.NotMounted" or "org.freedesktop.UDisks2.Error.Cancelled" =>
                new PlatformResult<T>.Failed(PlatformFailureCode.Conflict, diagnostic),
            "org.freedesktop.DBus.Error.UnknownObject" or "org.freedesktop.DBus.Error.UnknownMethod" or "org.freedesktop.DBus.Error.UnknownInterface" =>
                new PlatformResult<T>.Failed(PlatformFailureCode.NotFound, diagnostic),
            "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner" or DBusException.Disconnected
                or "org.freedesktop.DBus.Error.NoReply" or "org.freedesktop.DBus.Error.Spawn.ServiceNotFound" =>
                new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            _ => new PlatformResult<T>.Failed(PlatformFailureCode.IoError, diagnostic),
        };
    }

    private static PlatformResult<T> Forward<T>(PlatformResult<(DBusConnection, Model, DesktopVolume)> result) => result switch
    {
        PlatformResult<(DBusConnection, Model, DesktopVolume)>.Unavailable(var reason) => new PlatformResult<T>.Unavailable(reason),
        PlatformResult<(DBusConnection, Model, DesktopVolume)>.Failed failed => new PlatformResult<T>.Failed(failed.Code, failed.Diagnostic),
        _ => throw new InvalidOperationException(),
    };

    private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());

    public async ValueTask DisposeAsync()
    {
        if (_disposed.IsCancellationRequested) return;
        _disposed.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is { } connection) await connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
        finally { _gate.Release(); }
    }

    // UDisks2's objects and the volumes they describe.
    internal sealed class Model
    {
        private readonly Dictionary<string, Dictionary<string, ImmutableDictionary<string, DBusValue>>> _objects = new(StringComparer.Ordinal);

        internal Model(ImmutableDictionary<DBusObjectPath, ImmutableDictionary<string, ImmutableDictionary<string, DBusValue>>> objects)
        {
            foreach (var (path, interfaces) in objects)
                foreach (var (name, properties) in interfaces) Set(path.Value, name, properties);
        }

        internal bool Has(string path, string name) => _objects.TryGetValue(path, out var interfaces) && interfaces.ContainsKey(name);

        internal void Set(string path, string name, ImmutableDictionary<string, DBusValue> properties)
        {
            if (!_objects.TryGetValue(path, out var interfaces)) _objects[path] = interfaces = new(StringComparer.Ordinal);
            interfaces[name] = properties;
        }

        internal void Update(string path, string name, ImmutableDictionary<string, DBusValue> changed)
        {
            if (_objects.TryGetValue(path, out var interfaces) && interfaces.TryGetValue(name, out var properties)) interfaces[name] = properties.SetItems(changed);
        }

        internal void Remove(string path, string name)
        {
            if (!_objects.TryGetValue(path, out var interfaces)) return;
            interfaces.Remove(name);
            if (interfaces.Count == 0) _objects.Remove(path);
        }

        internal ImmutableDictionary<string, DesktopVolume> Volumes()
        {
            var volumes = ImmutableDictionary.CreateBuilder<string, DesktopVolume>(StringComparer.Ordinal);
            foreach (var (path, interfaces) in _objects)
            {
                if (!interfaces.TryGetValue(BlockInterface, out var blockValues)) continue;
                var block = BlockProperties.Read(blockValues);
                if (block.HintIgnore == true) continue;
                bool filesystem = interfaces.TryGetValue(FilesystemInterface, out var filesystemValues);
                bool encrypted = interfaces.TryGetValue(EncryptedInterface, out var encryptedValues);
                // An unlocked encrypted volume is listed through its cleartext device; a locked one is listed itself.
                bool locked = encrypted && EncryptedProperties.Read(encryptedValues!).CleartextDevice is not { Value: not "/" };
                if (!filesystem && !locked) continue;
                var mounts = filesystem ? FilesystemProperties.Read(filesystemValues!).MountPoints ?? [] : [];
                var paths = mounts.Select(static bytes => Path(bytes)).Where(static p => p.Length > 0).ToImmutableArray();
                string? drivePath = block.Drive is { Value: not "/" } drive ? drive.Value : null;
                // A cleartext device has no drive of its own; it belongs to the drive of its encrypted backing device.
                if (drivePath is null && block.CryptoBackingDevice is { Value: not "/" } backing && _objects.TryGetValue(backing.Value, out var backingInterfaces)
                    && backingInterfaces.TryGetValue(BlockInterface, out var backingValues) && BlockProperties.Read(backingValues).Drive is { Value: not "/" } backingDrive)
                    drivePath = backingDrive.Value;
                DriveProperties? driveProperties = drivePath is not null && _objects.TryGetValue(drivePath, out var driveInterfaces)
                    && driveInterfaces.TryGetValue(DriveInterface, out var driveValues) ? DriveProperties.Read(driveValues) : null;
                string device = Path(block.PreferredDevice is { IsDefaultOrEmpty: false } preferred ? preferred : block.Device ?? []);
                string driveName = string.Join(' ', new[] { driveProperties?.Vendor, driveProperties?.Model }.Where(static s => !string.IsNullOrWhiteSpace(s))).Trim();
                long? size = block.Size is { } bytes && bytes > 0 ? (long)Math.Min(bytes, long.MaxValue) : null;
                string name = !string.IsNullOrWhiteSpace(block.HintName) ? block.HintName!
                    : !string.IsNullOrWhiteSpace(block.IdLabel) ? block.IdLabel!
                    : driveName.Length > 0 ? size is { } known ? $"{driveName} ({Bytes(known)})" : driveName
                    : device.Length > 0 ? System.IO.Path.GetFileName(device) : System.IO.Path.GetFileName(path);
                volumes[path] = new DesktopVolume(path, name)
                {
                    MountPaths = paths,
                    FileSystem = string.IsNullOrEmpty(block.IdType) ? null : block.IdType,
                    Size = size,
                    DriveId = drivePath,
                    DriveName = driveName.Length > 0 ? driveName : null,
                    IsRemovable = driveProperties is { Removable: true } or { MediaRemovable: true },
                    IsReadOnly = block.ReadOnly == true,
                    IsSystem = block.HintSystem == true,
                    IsLocked = locked,
                    CanMount = filesystem && paths.IsEmpty,
                    CanUnmount = filesystem && !paths.IsEmpty,
                    CanEject = driveProperties is { Ejectable: true },
                };
            }
            return volumes.ToImmutable();
        }

        // UDisks2 paths are NUL-terminated bytes.
        private static string Path(ImmutableArray<byte> bytes)
        {
            int length = bytes.IndexOf((byte)0);
            return Encoding.UTF8.GetString(bytes.AsSpan()[..(length < 0 ? bytes.Length : length)]);
        }

        private static string Bytes(long size) => size switch
        {
            >= 1_000_000_000_000 => string.Create(CultureInfo.CurrentCulture, $"{size / 1_000_000_000_000.0:0.#} TB"),
            >= 1_000_000_000 => string.Create(CultureInfo.CurrentCulture, $"{size / 1_000_000_000.0:0.#} GB"),
            >= 1_000_000 => string.Create(CultureInfo.CurrentCulture, $"{size / 1_000_000.0:0.#} MB"),
            _ => string.Create(CultureInfo.CurrentCulture, $"{size} B"),
        };
    }
}
