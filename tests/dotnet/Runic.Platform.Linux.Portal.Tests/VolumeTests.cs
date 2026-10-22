using System.Collections.Immutable;
using System.Text;
using Runic.Platform;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Linux.Portal;
using TUnit.Core;

// UDisks2 volumes against a fake UDisks2 on the private session bus of dbus-run-session. No real disk is touched.
internal sealed class VolumeTests
{
    private const string Stick = "/org/freedesktop/UDisks2/block_devices/sdb1";
    private const string StickDrive = "/org/freedesktop/UDisks2/drives/Example_Stick";
    private const string StickSecond = "/org/freedesktop/UDisks2/block_devices/sdb2";
    private const string Root = "/org/freedesktop/UDisks2/block_devices/sda2";
    private const string Locked = "/org/freedesktop/UDisks2/block_devices/sdc1";
    private const string Container = "/org/freedesktop/UDisks2/block_devices/sdd1";
    private const string Cleartext = "/org/freedesktop/UDisks2/block_devices/dm_2d0";
    private const string Ignored = "/org/freedesktop/UDisks2/block_devices/loop0";
    private const string Fixed = "/org/freedesktop/UDisks2/drives/Example_Disk";
    private const string Unlocked = "/org/freedesktop/UDisks2/block_devices/dm_2d1";
    private const string Passphrase = "correct horse battery staple";

    [Test, Explicit, Category("DBus"), DisplayName("volumes: UDisks2 objects become volumes with their mount state, drive and flags")]
    public async Task ListAsync()
    {
        await using var fake = await FakeUDisks.StartAsync();
        await using var volumes = FakeUDisks.Volumes();
        var listed = ((PlatformResult<ImmutableArray<DesktopVolume>>.Success)await volumes.ListAsync()).Value.ToDictionary(static v => v.Id);
        Check(listed.Keys.Order().SequenceEqual(new[] { Root, Stick, StickSecond, Locked, Cleartext }.Order()), $"listed {string.Join(", ", listed.Keys)}");
        var stick = listed[Stick];
        Check(stick is { DisplayName: "PHOTOS", FileSystem: "vfat", Size: 8_000_000_000, DriveId: StickDrive, DriveName: "Example Stick", IsRemovable: true,
            IsSystem: false, IsLocked: false, CanMount: true, CanUnmount: false, CanEject: true } && stick.MountPaths.IsEmpty, $"the stick: {stick}");
        Check(listed[Root] is { IsSystem: true, CanUnmount: true, CanEject: false, IsRemovable: false } root && root.MountPaths.SequenceEqual(["/"]), "the system volume");
        Check(listed[Locked] is { IsLocked: true, CanMount: false, CanUnmount: false }, "a locked encrypted volume");
        Check(listed[Cleartext] is { DriveId: Fixed, CanMount: false } cleartext && cleartext.MountPaths.SequenceEqual(["/home"]) && cleartext.DisplayName == "Example Disk (512 GB)",
            $"an unlocked volume belongs to its backing device's drive: {listed[Cleartext]}");
    }

    [Test, Explicit, Category("DBus"), DisplayName("volumes: mount, unmount and eject ask UDisks2 with interactive authorization")]
    public async Task OperationsAsync()
    {
        await using var fake = await FakeUDisks.StartAsync();
        await using var volumes = FakeUDisks.Volumes();
        var mounted = await volumes.MountAsync(Stick);
        Check(mounted is PlatformResult<DesktopVolume>.Success(var volume) && volume.MountPaths.SequenceEqual(["/run/media/alice/PHOTOS"]) && volume.CanUnmount && !volume.CanMount,
            $"mount: {mounted}");
        Check(fake.Calls.Contains($"Mount {Stick} auth.no_user_interaction=false"), $"Mount allowed the prompt: {string.Join("; ", fake.Calls)}");
        Check(await volumes.MountAsync(Stick) is PlatformResult<DesktopVolume>.Success && fake.Calls.Count(static c => c.StartsWith("Mount", StringComparison.Ordinal)) == 1,
            "a mounted volume is not mounted again");
        Check(await volumes.UnmountAsync(Stick) is PlatformResult<PlatformUnit>.Success && fake.Calls.Contains($"Unmount {Stick} auth.no_user_interaction=false"), "unmount");
        int calls = fake.Calls.Count;
        Check(await volumes.UnmountAsync(Stick) is PlatformResult<PlatformUnit>.Success && fake.Calls.Count == calls, "an unmounted volume succeeds without a call");

        // Eject unmounts every mounted volume of the drive, then ejects it.
        await volumes.MountAsync(Stick);
        await volumes.MountAsync(StickSecond);
        fake.Calls.Clear();
        Check(await volumes.EjectAsync(StickSecond) is PlatformResult<PlatformUnit>.Success, "eject");
        Check(fake.Calls.SequenceEqual([$"Unmount {Stick} auth.no_user_interaction=false", $"Unmount {StickSecond} auth.no_user_interaction=false",
            $"Eject {StickDrive} auth.no_user_interaction=false"]), $"eject order: {string.Join("; ", fake.Calls)}");
        Check(await volumes.EjectAsync(Root) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "a fixed drive cannot be ejected");
        Check(await volumes.MountAsync(Locked) is PlatformResult<DesktopVolume>.Failed { Code: PlatformFailureCode.Locked }, "a locked volume");
        Check(await volumes.MountAsync("/org/freedesktop/UDisks2/block_devices/nothing") is PlatformResult<DesktopVolume>.Failed { Code: PlatformFailureCode.NotFound }, "an unknown volume");
        Check(await volumes.MountAsync(Ignored) is PlatformResult<DesktopVolume>.Failed { Code: PlatformFailureCode.NotFound }, "an ignored device is no volume");
    }

    [Test, Explicit, Category("DBus"), DisplayName("volumes: unlock with a passphrase and mount options through UDisks2")]
    public async Task UnlockAndOptionsAsync()
    {
        await using var fake = await FakeUDisks.StartAsync();
        await using IDesktopVolumes volumes = FakeUDisks.Volumes();
        var listed = ((PlatformResult<ImmutableArray<DesktopVolume>>.Success)await volumes.ListAsync()).Value.ToDictionary(static v => v.Id);
        Check(listed[Locked] is { IsLocked: true, IsEncrypted: true, UnlockMethod: DesktopVolumeUnlockMethod.Passphrase }
            && listed[Cleartext] is { IsLocked: false, IsEncrypted: true, UnlockMethod: DesktopVolumeUnlockMethod.None }
            && listed[Stick] is { IsEncrypted: false, UnlockMethod: DesktopVolumeUnlockMethod.None }, "encryption flags");

        Check(await volumes.UnlockAsync(Locked) is PlatformResult<DesktopVolume>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "UDisks2 has no prompt of its own for a passphrase");
        var wrong = await volumes.UnlockAsync(Locked, "wrong".AsMemory(), default);
        Check(wrong is PlatformResult<DesktopVolume>.Failed { Code: PlatformFailureCode.Other, Diagnostic.Domain: "org.freedesktop.UDisks2.Error.Failed" }, $"a wrong passphrase: {wrong}");
        fake.NextError = "org.freedesktop.UDisks2.Error.NotAuthorizedDismissed";
        Check(await volumes.UnlockAsync(Locked, Passphrase.AsMemory(), default) is PlatformResult<DesktopVolume>.Failed { Code: PlatformFailureCode.UserDismissed },
            "a dismissed authorization");
        fake.NextError = "org.freedesktop.UDisks2.Error.NotAuthorized";
        Check(await volumes.UnlockAsync(Locked, Passphrase.AsMemory(), default) is PlatformResult<DesktopVolume>.Failed { Code: PlatformFailureCode.PermissionDenied },
            "a refused authorization");
        var unlocked = await volumes.UnlockAsync(Locked, Passphrase.ToCharArray(), default);
        Check(unlocked is PlatformResult<DesktopVolume>.Success { Value: { Id: Unlocked, DisplayName: "SECRETS", IsLocked: false, IsEncrypted: true, CanMount: true, DriveId: Fixed } },
            $"the cleartext volume: {unlocked}");
        Check(fake.Calls.Contains($"Unlock {Locked} auth.no_user_interaction=false"), $"Unlock allowed the prompt: {string.Join("; ", fake.Calls)}");
        var after = ((PlatformResult<ImmutableArray<DesktopVolume>>.Success)await volumes.ListAsync()).Value;
        Check(!after.Any(static v => v.Id == Locked) && after.Any(static v => v.Id == Unlocked), "the container leaves the listing; its cleartext device joins it");
        Check(await volumes.UnlockAsync(Stick, "x".AsMemory(), default) is PlatformResult<DesktopVolume>.Success { Value.Id: Stick }, "an unlocked volume succeeds as it is");

        fake.Calls.Clear();
        var mounted = await volumes.MountAsync(Stick, new DesktopVolumeMountOptions { ReadOnly = true, Options = ["noexec", "nosuid"] }, default);
        Check(mounted is PlatformResult<DesktopVolume>.Success && fake.Calls is [var call] && call.Contains("options='ro,noexec,nosuid'", StringComparison.Ordinal)
            && call.Contains("auth.no_user_interaction=false", StringComparison.Ordinal), $"mount options: {string.Join("; ", fake.Calls)}");
        await volumes.UnmountAsync(Stick);
        fake.Calls.Clear();
        Check(await volumes.MountAsync(Stick, new DesktopVolumeMountOptions(), default) is PlatformResult<DesktopVolume>.Success
            && fake.Calls is [var plain] && !plain.Contains("options=", StringComparison.Ordinal), "the default options send none");
        await volumes.UnmountAsync(Stick);
        fake.NextError = "org.freedesktop.UDisks2.Error.OptionNotPermitted";
        Check(await volumes.MountAsync(Stick, new DesktopVolumeMountOptions { Options = ["uid=0"] }, default)
            is PlatformResult<DesktopVolume>.Failed { Code: PlatformFailureCode.InvalidData, Diagnostic.Domain: "org.freedesktop.UDisks2.Error.OptionNotPermitted" },
            "an option UDisks2 does not permit");
    }

    [Test, DisplayName("volumes: mount options become UDisks2's options")]
    public void MountOptions()
    {
        Check(!UDisksVolumes.MountOptions(new DesktopVolumeMountOptions()).ContainsKey("options"), "no options");
        Check(UDisksVolumes.MountOptions(new DesktopVolumeMountOptions { ReadOnly = true })["options"].GetString() == "ro", "read-only");
        Check(UDisksVolumes.MountOptions(new DesktopVolumeMountOptions { Options = ["noexec", "uid=1000"] })["options"].GetString() == "noexec,uid=1000", "options");
    }

    [Test, Explicit, Category("DBus"), DisplayName("volumes: UDisks2 errors map to platform failures")]
    public async Task ErrorsAsync()
    {
        await using var fake = await FakeUDisks.StartAsync();
        await using var volumes = FakeUDisks.Volumes();
        foreach (var (error, code) in new[]
        {
            ("org.freedesktop.UDisks2.Error.NotAuthorizedCanObtain", PlatformFailureCode.PermissionDenied),
            ("org.freedesktop.UDisks2.Error.NotAuthorized", PlatformFailureCode.PermissionDenied),
            ("org.freedesktop.UDisks2.Error.NotAuthorizedDismissed", PlatformFailureCode.UserDismissed),
            ("org.freedesktop.UDisks2.Error.Failed", PlatformFailureCode.Other),
        })
        {
            fake.NextError = error;
            Check(await volumes.MountAsync(Stick) is PlatformResult<DesktopVolume>.Failed failed && failed.Code == code && failed.Diagnostic?.Domain == error, $"{error} -> {code}");
        }
        await volumes.MountAsync(Stick);
        fake.NextError = "org.freedesktop.UDisks2.Error.DeviceBusy";
        Check(await volumes.UnmountAsync(Stick) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy }, "a busy volume");
        fake.NextError = "org.freedesktop.UDisks2.Error.DeviceBusy";
        fake.Calls.Clear();
        Check(await volumes.EjectAsync(Stick) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy } && !fake.Calls.Any(static c => c.StartsWith("Eject", StringComparison.Ordinal)),
            "a busy volume stops the eject before the drive is ejected");
    }

    [Test, Explicit, Category("DBus"), DisplayName("volumes: the watch reports a snapshot, changes, and the service stopping and returning")]
    public async Task WatchAsync()
    {
        await using var fake = await FakeUDisks.StartAsync();
        await using var volumes = FakeUDisks.Volumes();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var watch = volumes.WatchAsync(deadline.Token).GetAsyncEnumerator();
        Check(await Next(watch) is PlatformResult<DesktopVolumeChange>.Success(DesktopVolumeChange.Snapshot snapshot) && snapshot.Volumes.Length == 5, "the snapshot first");

        await volumes.MountAsync(Stick);
        Check(await Next(watch) is PlatformResult<DesktopVolumeChange>.Success(DesktopVolumeChange.Changed { Volume: { Id: Stick, CanUnmount: true } mounted })
            && mounted.MountPaths.SequenceEqual(["/run/media/alice/PHOTOS"]), "mounting changes the volume");

        await fake.AddDeviceAsync("/org/freedesktop/UDisks2/block_devices/sde1", "CARD");
        Check(await Next(watch) is PlatformResult<DesktopVolumeChange>.Success(DesktopVolumeChange.Added { Volume.DisplayName: "CARD" }), "a new device is added");
        await fake.RemoveDeviceAsync("/org/freedesktop/UDisks2/block_devices/sde1");
        Check(await Next(watch) is PlatformResult<DesktopVolumeChange>.Success(DesktopVolumeChange.Removed { Id: "/org/freedesktop/UDisks2/block_devices/sde1" }), "a removed device");

        await fake.StopAsync();
        Check(await Next(watch) is PlatformResult<DesktopVolumeChange>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "the service stopped");
        await fake.RestartAsync();
        Check(await Next(watch) is PlatformResult<DesktopVolumeChange>.Success(DesktopVolumeChange.Snapshot again) && again.Volumes.Length == 5, "a new snapshot after the service returns");
    }

    [Test, Explicit, Category("DBus"), DisplayName("volumes: without UDisks2 every operation reports it unavailable")]
    public async Task NoServiceAsync()
    {
        await using var volumes = new UDisksVolumes(DBusAddress(), "org.runic.Test.NoUDisks");
        Check(await volumes.ListAsync() is PlatformResult<ImmutableArray<DesktopVolume>>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "list");
        Check(await volumes.MountAsync(Stick) is PlatformResult<DesktopVolume>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "mount");
        await using var noBus = new UDisksVolumes($"unix:path={Path.Combine(Path.GetTempPath(), "runic-no-bus-" + Guid.NewGuid().ToString("N"))}");
        Check(await noBus.ListAsync() is PlatformResult<ImmutableArray<DesktopVolume>>.Unavailable, "no bus");
    }

    [Test, DisplayName("volumes: changes between two states")]
    public void Diff()
    {
        var a = new DesktopVolume("a", "A");
        var b = new DesktopVolume("b", "B") { MountPaths = ["/b"] };
        var previous = ImmutableDictionary<string, DesktopVolume>.Empty.Add("a", a).Add("b", b);
        var current = ImmutableDictionary<string, DesktopVolume>.Empty.Add("b", b with { MountPaths = ["/b"] }).Add("c", new DesktopVolume("c", "C"));
        var changes = UDisksVolumes.Diff(previous, current).ToList();
        Check(changes is [DesktopVolumeChange.Removed { Id: "a" }, DesktopVolumeChange.Added { Volume.Id: "c" }], "equal mount paths in a new array are no change");
        current = current.SetItem("b", b with { MountPaths = ["/b", "/c"] });
        Check(UDisksVolumes.Diff(previous, current).OfType<DesktopVolumeChange.Changed>().Single().Volume.MountPaths.Length == 2, "a new mount path is a change");
    }

    private static async Task<PlatformResult<DesktopVolumeChange>> Next(IAsyncEnumerator<PlatformResult<DesktopVolumeChange>> watch) =>
        await watch.MoveNextAsync() ? watch.Current : throw new InvalidOperationException("The watch ended.");

    private static string DBusAddress() => Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")
        ?? throw new InvalidOperationException("Run the bus cases under dbus-run-session.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // UDisks2 as the provider sees it: an object manager and device objects with Block, Filesystem, Encrypted and Drive.
    private sealed class FakeUDisks : IAsyncDisposable
    {
        private const string Name = "org.runic.Test.UDisks2";
        private readonly Dictionary<string, Dictionary<string, ImmutableDictionary<string, DBusValue>>> _objects = new(StringComparer.Ordinal);
        private readonly List<IAsyncDisposable> _exports = [];
        private DBusConnection _connection = null!;
        private Manager _manager = null!;
        public List<string> Calls { get; } = [];
        public string? NextError;

        public static async Task<FakeUDisks> StartAsync()
        {
            var fake = new FakeUDisks();
            fake.Seed();
            await fake.RestartAsync();
            return fake;
        }

        public static UDisksVolumes Volumes() => new(DBusAddress(), Name);

        private void Seed()
        {
            AddDrive(StickDrive, "Example", "Stick", removable: true, ejectable: true);
            AddDrive(Fixed, "Example", "Disk", removable: false, ejectable: false);
            AddBlock(Stick, StickDrive, "PHOTOS", "vfat", 8_000_000_000, filesystem: []);
            AddBlock(StickSecond, StickDrive, "NOTES", "ext4", 1_000_000_000, filesystem: []);
            AddBlock(Root, Fixed, "", "ext4", 500_000_000_000, filesystem: ["/"], system: true);
            AddBlock(Locked, Fixed, "", "crypto_LUKS", 100_000_000_000, filesystem: null);
            Set(Locked, "org.freedesktop.UDisks2.Encrypted", ("CleartextDevice", DBusValue.From(new DBusObjectPath("/"))));
            AddBlock(Container, Fixed, "", "crypto_LUKS", 512_000_000_000, filesystem: null);
            Set(Container, "org.freedesktop.UDisks2.Encrypted", ("CleartextDevice", DBusValue.From(new DBusObjectPath(Cleartext))));
            AddBlock(Cleartext, "/", "", "ext4", 512_000_000_000, filesystem: ["/home"]);
            Set(Cleartext, "org.freedesktop.UDisks2.Block", ("CryptoBackingDevice", DBusValue.From(new DBusObjectPath(Container))));
            AddBlock(Ignored, "/", "", "squashfs", 1_000, filesystem: []);
            Set(Ignored, "org.freedesktop.UDisks2.Block", ("HintIgnore", DBusValue.From(true)));
        }

        private void AddDrive(string path, string vendor, string model, bool removable, bool ejectable) =>
            Set(path, "org.freedesktop.UDisks2.Drive", ("Vendor", DBusValue.From(vendor)), ("Model", DBusValue.From(model)), ("Removable", DBusValue.From(removable)),
                ("MediaRemovable", DBusValue.From(removable)), ("Ejectable", DBusValue.From(ejectable)));

        private void AddBlock(string path, string drive, string label, string type, ulong size, string[]? filesystem, bool system = false)
        {
            Set(path, "org.freedesktop.UDisks2.Block", ("Device", Bytes("/dev/" + path[(path.LastIndexOf('/') + 1)..])), ("PreferredDevice", Bytes("")),
                ("Size", DBusValue.From(size)), ("ReadOnly", DBusValue.From(false)), ("Drive", DBusValue.From(new DBusObjectPath(drive))),
                ("IdUsage", DBusValue.From(filesystem is null ? "crypto" : "filesystem")), ("IdType", DBusValue.From(type)), ("IdLabel", DBusValue.From(label)),
                ("CryptoBackingDevice", DBusValue.From(new DBusObjectPath("/"))), ("HintIgnore", DBusValue.From(false)), ("HintSystem", DBusValue.From(system)),
                ("HintName", DBusValue.From("")));
            if (filesystem is not null) Set(path, "org.freedesktop.UDisks2.Filesystem", ("MountPoints", MountPoints(filesystem)));
        }

        private static DBusValue Bytes(string text) => DBusValue.ByteArray([.. Encoding.UTF8.GetBytes(text), 0]);
        private static DBusValue MountPoints(IEnumerable<string> paths) => DBusValue.Array("ay", paths.Select(Bytes));

        private void Set(string path, string name, params (string Key, DBusValue Value)[] values)
        {
            if (!_objects.TryGetValue(path, out var interfaces)) _objects[path] = interfaces = new(StringComparer.Ordinal);
            interfaces[name] = (interfaces.TryGetValue(name, out var existing) ? existing : ImmutableDictionary<string, DBusValue>.Empty)
                .SetItems(values.Select(static v => KeyValuePair.Create(v.Key, v.Value)));
        }

        public async Task RestartAsync()
        {
            _connection = ((PlatformResult<DBusConnection>.Success)await DBusConnection.ConnectToAddressAsync(DBusAddress())).Value;
            _manager = new Manager(this);
            _exports.Add(_connection.Export("/org/freedesktop/UDisks2", _manager));
            foreach (var path in _objects.Keys) _exports.Add(_connection.Export(path, new Device(this)));
            if (await _connection.RequestNameAsync(Name, DBusRequestNameOptions.DoNotQueue) != DBusRequestNameReply.PrimaryOwner)
                throw new InvalidOperationException("The fake UDisks2 name is taken.");
        }

        public async Task StopAsync()
        {
            foreach (var export in _exports) await export.DisposeAsync();
            _exports.Clear();
            await _connection.DisposeAsync();
        }

        public Task AddDeviceAsync(string path, string label)
        {
            AddBlock(path, StickDrive, label, "exfat", 64_000_000_000, filesystem: []);
            _exports.Add(_connection.Export(path, new Device(this)));
            _manager.Emit("InterfacesAdded", DBusValue.From(new DBusObjectPath(path)),
                DBusValue.Dictionary("s", "a{sv}", _objects[path].Select(static i => KeyValuePair.Create(DBusValue.From(i.Key), DBusValue.VariantDictionary(i.Value)))));
            return Task.CompletedTask;
        }

        public Task RemoveDeviceAsync(string path)
        {
            var names = _objects[path].Keys.ToList();
            _objects.Remove(path);
            _manager.Emit("InterfacesRemoved", DBusValue.From(new DBusObjectPath(path)), DBusValue.StringArray(names));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => new(StopAsync());

        private sealed class Manager(FakeUDisks fake) : DBusObject
        {
            protected override ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context)
            {
                if (context.Member != "GetManagedObjects") throw new DBusException(DBusException.UnknownMethod, context.Member);
                return ValueTask.FromResult<ImmutableArray<DBusValue>>([DBusValue.Dictionary("o", "a{sa{sv}}", fake._objects.Select(static o => KeyValuePair.Create(
                    DBusValue.From(new DBusObjectPath(o.Key)),
                    DBusValue.Dictionary("s", "a{sv}", o.Value.Select(static i => KeyValuePair.Create(DBusValue.From(i.Key), DBusValue.VariantDictionary(i.Value)))))))]);
            }

            public bool Emit(string member, params DBusValue[] arguments) => EmitSignal("org.freedesktop.DBus.ObjectManager", member, arguments);
        }

        private sealed class Device(FakeUDisks fake) : DBusObject
        {
            protected override ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context)
            {
                string path = context.Path.Value;
                if ((context.Interface, context.Member) is ("org.freedesktop.UDisks2.Encrypted", "Unlock")) return Unlock(path, context);
                string options = string.Join(",", context.Arguments[0].GetVariantDictionary().Select(static o => $"{o.Key}={o.Value.ToString().ToLowerInvariant()}"));
                lock (fake.Calls) fake.Calls.Add($"{context.Member} {path} {options}");
                if (Interlocked.Exchange(ref fake.NextError, null) is { } error) throw new DBusException(error, "Refused by the fake.");
                switch (context.Interface, context.Member)
                {
                    case ("org.freedesktop.UDisks2.Filesystem", "Mount"):
                        string label = fake._objects[path]["org.freedesktop.UDisks2.Block"]["IdLabel"].GetString();
                        string mountPath = "/run/media/alice/" + label;
                        Change(path, ("MountPoints", MountPoints([mountPath])));
                        return ValueTask.FromResult<ImmutableArray<DBusValue>>([DBusValue.From(mountPath)]);
                    case ("org.freedesktop.UDisks2.Filesystem", "Unmount"):
                        Change(path, ("MountPoints", MountPoints([])));
                        return ValueTask.FromResult<ImmutableArray<DBusValue>>([]);
                    case ("org.freedesktop.UDisks2.Drive", "Eject"):
                        return ValueTask.FromResult<ImmutableArray<DBusValue>>([]);
                    default:
                        throw new DBusException(DBusException.UnknownMethod, context.Member);
                }
            }

            // The right passphrase adds a cleartext filesystem backed by the container, as UDisks2 does.
            private ValueTask<ImmutableArray<DBusValue>> Unlock(string path, DBusMethodContext context)
            {
                string passphrase = context.Arguments[0].GetString();
                string options = string.Join(",", context.Arguments[1].GetVariantDictionary().Select(static o => $"{o.Key}={o.Value.ToString().ToLowerInvariant()}"));
                lock (fake.Calls) fake.Calls.Add($"Unlock {path} {options}");
                if (Interlocked.Exchange(ref fake.NextError, null) is { } error) throw new DBusException(error, "Refused by the fake.");
                if (passphrase != Passphrase) throw new DBusException("org.freedesktop.UDisks2.Error.Failed", "Error unlocking: wrong passphrase.");
                string cleartext = Unlocked;
                fake.AddBlock(cleartext, "/", "SECRETS", "ext4", 100_000_000_000, filesystem: []);
                fake.Set(cleartext, "org.freedesktop.UDisks2.Block", ("CryptoBackingDevice", DBusValue.From(new DBusObjectPath(path))));
                fake.Set(path, "org.freedesktop.UDisks2.Encrypted", ("CleartextDevice", DBusValue.From(new DBusObjectPath(cleartext))));
                fake._exports.Add(fake._connection.Export(cleartext, new Device(fake)));
                fake._manager.Emit("InterfacesAdded", DBusValue.From(new DBusObjectPath(cleartext)),
                    DBusValue.Dictionary("s", "a{sv}", fake._objects[cleartext].Select(static i => KeyValuePair.Create(DBusValue.From(i.Key), DBusValue.VariantDictionary(i.Value)))));
                EmitPropertiesChanged("org.freedesktop.UDisks2.Encrypted", new Dictionary<string, DBusValue> { ["CleartextDevice"] = DBusValue.From(new DBusObjectPath(cleartext)) });
                return ValueTask.FromResult<ImmutableArray<DBusValue>>([DBusValue.From(new DBusObjectPath(cleartext))]);
            }

            private void Change(string path, params (string Key, DBusValue Value)[] values)
            {
                fake.Set(path, "org.freedesktop.UDisks2.Filesystem", values);
                EmitPropertiesChanged("org.freedesktop.UDisks2.Filesystem", values.ToDictionary(static v => v.Key, static v => v.Value));
            }

            protected override ValueTask<ImmutableDictionary<string, DBusValue>> GetAllPropertiesAsync(string interfaceName, DBusMethodContext context) =>
                ValueTask.FromResult(fake._objects.TryGetValue(context.Path.Value, out var interfaces) && interfaces.TryGetValue(interfaceName, out var values)
                    ? values : ImmutableDictionary<string, DBusValue>.Empty);
        }
    }
}
