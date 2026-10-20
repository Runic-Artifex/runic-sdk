using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading.Channels;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

internal sealed class VolumeTests
{
    private static readonly TimeSpan Never = TimeSpan.FromHours(1);

    private static DesktopVolume Volume(string id, string drive = "USBSTOR\\DISK\\1", bool mounted = true, bool ejectable = true, bool system = false) =>
        new(id, id.ToUpperInvariant())
        {
            MountPaths = mounted ? ["E:\\"] : [],
            FileSystem = "NTFS",
            DriveId = drive,
            IsSystem = system,
            CanEject = ejectable,
        };

    // Mount and unmount answer for the state Windows already gave the volume; eject goes to the drive. Runs on every OS.
    [Test]
    public async Task OperationsFollowWindowsMountModel()
    {
        var source = new FakeSource
        {
            Volumes =
            [
                Volume("\\\\?\\Volume{b}\\"),
                Volume("\\\\?\\Volume{a}\\", mounted: false),
                Volume("\\\\?\\Volume{c}\\", drive: "SCSI\\DISK\\0", system: true),
                Volume("\\\\?\\Volume{d}\\", drive: "SCSI\\DISK\\0"),
                Volume("\\\\?\\Volume{e}\\", drive: "SCSI\\DISK\\2", ejectable: false),
            ],
        };
        await using var volumes = new WindowsVolumes(source, Never, TimeSpan.Zero);

        var listed = await volumes.ListAsync() is PlatformResult<ImmutableArray<DesktopVolume>>.Success(var all) ? all : default;
        Check(listed.Select(static v => v.Id).SequenceEqual(["\\\\?\\Volume{a}\\", "\\\\?\\Volume{b}\\", "\\\\?\\Volume{c}\\", "\\\\?\\Volume{d}\\", "\\\\?\\Volume{e}\\"]),
            "the listing is ordered by ID");
        Check(!listed[3].CanEject, "a volume on the system's disk is not offered for removal");

        Check(await volumes.MountAsync("\\\\?\\Volume{b}\\") is PlatformResult<DesktopVolume>.Success(var mounted) && mounted.MountPaths is ["E:\\"],
            "mounting a mounted volume returns it");
        Check(await volumes.MountAsync("\\\\?\\VOLUME{B}\\") is PlatformResult<DesktopVolume>.Success, "IDs compare without case, as Windows compares them");
        Check(await volumes.MountAsync("\\\\?\\Volume{a}\\") is PlatformResult<DesktopVolume>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "a volume Windows did not mount cannot be mounted");
        Check(await volumes.MountAsync("\\\\?\\Volume{z}\\") is PlatformResult<DesktopVolume>.Failed { Code: PlatformFailureCode.NotFound }, "mount of an unknown ID");

        Check(await volumes.UnmountAsync("\\\\?\\Volume{a}\\") is PlatformResult<PlatformUnit>.Success, "unmounting a volume without a mount path succeeds");
        Check(await volumes.UnmountAsync("\\\\?\\Volume{b}\\") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "a mounted volume cannot be dismounted");
        Check(await volumes.UnmountAsync("\\\\?\\Volume{z}\\") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound }, "unmount of an unknown ID");

        Check(await volumes.EjectAsync("\\\\?\\Volume{b}\\") is PlatformResult<PlatformUnit>.Success && source.Ejected is ["USBSTOR\\DISK\\1"], "eject removes the drive");
        source.EjectResult = new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.ResourceBusy);
        Check(await volumes.EjectAsync("\\\\?\\Volume{a}\\") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy }, "a veto is reported");
        Check(await volumes.EjectAsync("\\\\?\\Volume{d}\\") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable)
            && await volumes.EjectAsync("\\\\?\\Volume{e}\\") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable)
            && source.Ejected.Count == 2, "the system's disk and a drive that cannot be removed are not ejected");
        Check(await volumes.EjectAsync("\\\\?\\Volume{z}\\") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound }, "eject of an unknown ID");

        source.Listing = new PlatformResult<ImmutableArray<DesktopVolume>>.Failed(PlatformFailureCode.IOError, PlatformDiagnostic.FromWin32Error(21));
        Check(await volumes.MountAsync("\\\\?\\Volume{b}\\") is PlatformResult<DesktopVolume>.Failed { Code: PlatformFailureCode.IOError }, "a failed listing is forwarded");
        Check(await volumes.EjectAsync("\\\\?\\Volume{b}\\") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.IOError }, "a failed listing is forwarded to eject");

        await Throws<ArgumentException>(() => volumes.MountAsync("").AsTask());
        await volumes.DisposeAsync();
        await Throws<ObjectDisposedException>(() => volumes.ListAsync().AsTask());
    }

    // The watch: a snapshot, then the differences after each notification and each interval. Runs on every OS.
    [Test]
    public async Task WatchReportsSnapshotThenChanges()
    {
        var source = new FakeSource { Volumes = [Volume("\\\\?\\Volume{a}\\"), Volume("\\\\?\\Volume{b}\\")] };
        await using var volumes = new WindowsVolumes(source, Never, TimeSpan.Zero);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var watch = volumes.WatchAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

        Check(await Next(watch) is DesktopVolumeChange.Snapshot { Volumes: [{ Id: "\\\\?\\Volume{a}\\" }, { Id: "\\\\?\\Volume{b}\\" }] }, "a snapshot comes first");
        Check(source.Subscribers == 1, "the watch subscribes to volume notifications");

        source.Volumes = [Volume("\\\\?\\Volume{a}\\", mounted: false), Volume("\\\\?\\Volume{c}\\")];
        source.Notify();
        Check(await Next(watch) is DesktopVolumeChange.Removed("\\\\?\\Volume{b}\\"), "a removed volume");
        Check(await Next(watch) is DesktopVolumeChange.Changed { Volume: { Id: "\\\\?\\Volume{a}\\", MountPaths.IsEmpty: true } }, "a volume that lost its drive letter");
        Check(await Next(watch) is DesktopVolumeChange.Added { Volume.Id: "\\\\?\\Volume{c}\\" }, "an added volume");

        // A listing that fails is reported once; the next one that succeeds is a new snapshot.
        source.Listing = new PlatformResult<ImmutableArray<DesktopVolume>>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        source.Notify();
        Check(await NextResult(watch) is PlatformResult<DesktopVolumeChange>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "an unavailable listing");
        source.Notify();
        source.Listing = null;
        source.Notify();
        Check(await Next(watch) is DesktopVolumeChange.Snapshot { Volumes.Length: 2 }, "a new snapshot once listing works again");

        source.Volumes = [Volume("\\\\?\\Volume{a}\\", mounted: false)];
        source.Notify();
        Check(await Next(watch) is DesktopVolumeChange.Removed("\\\\?\\Volume{c}\\"), "only the difference is reported");

        await watch.DisposeAsync();
        Check(source.Subscribers == 0, "ending the watch removes its notification");
    }

    // Without a notification the interval alone finds changes; disposal ends the watch. Runs on every OS.
    [Test]
    public async Task WatchPollsAndEndsWithDisposal()
    {
        var source = new FakeSource { Volumes = [Volume("\\\\?\\Volume{a}\\")], CanSubscribe = false };
        var volumes = new WindowsVolumes(source, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var watch = volumes.WatchAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Check(await Next(watch) is DesktopVolumeChange.Snapshot { Volumes.Length: 1 }, "a snapshot");
        source.Volumes = [Volume("\\\\?\\Volume{a}\\") with { DisplayName = "Renamed" }];
        Check(await Next(watch) is DesktopVolumeChange.Changed { Volume.DisplayName: "Renamed" }, "a relabelled volume, found by the interval");
        var pending = watch.MoveNextAsync().AsTask();
        await volumes.DisposeAsync();
        Check(await Ends(pending), "disposal ends the watch");
        await watch.DisposeAsync();
        await Throws<ObjectDisposedException>(async () => { await foreach (var _ in volumes.WatchAsync()) { } });
    }

    // Configuration Manager results and vetoes; runs on every OS.
    [Test]
    public void EjectResults()
    {
#pragma warning disable CA1416 // EjectResult and Strings are pure.
        Check(WindowsVolumeSource.EjectResult(0, 0) is PlatformResult<PlatformUnit>.Success, "removed");
        Check(WindowsVolumeSource.EjectResult(0x17, 5) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy, Diagnostic: { Domain: "PNP_VETO_TYPE", Code: 5 } },
            "an open handle vetoes");
        Check(WindowsVolumeSource.EjectResult(0x17, 0) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy, Diagnostic.Domain: "CONFIGRET" }, "an unexplained veto");
        Check(WindowsVolumeSource.EjectResult(0, 3) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy }, "an application vetoes");
        Check(WindowsVolumeSource.EjectResult(0x17, 12) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }, "insufficient rights");
        Check(WindowsVolumeSource.EjectResult(0x33, 0) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }, "access denied");
        Check(WindowsVolumeSource.EjectResult(0x17, 13) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound }, "already removed");
        Check(WindowsVolumeSource.EjectResult(0x0D, 0) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound }, "no such device");
        Check(WindowsVolumeSource.EjectResult(0x17, 10) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "not removable");
        Check(WindowsVolumeSource.EjectResult(0x1F, 0) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.Other }, "other results");
        Check(WindowsVolumeSource.Strings("E:\\\0C:\\Mount\\\0\0ignored".AsSpan()) is ["E:\\", "C:\\Mount\\"], "a double-NUL-terminated list");
        Check(WindowsVolumeSource.Strings("\0\0".AsSpan()).IsEmpty, "an empty list");
#pragma warning restore CA1416
    }

    // A VHD that diskpart creates and attaches for the run, with a label unique to it: the runner's own disks are
    // only listed. CI only (RUNIC_TEST_VOLUMES_IMAGES=1); diskpart needs an administrator, as the hosted runners are.
    [Test, Explicit, Category("NativeVolumes")]
    public async Task VirtualDiskAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8)) throw new PlatformNotSupportedException("Volumes require Windows.");
        if (Environment.GetEnvironmentVariable("RUNIC_TEST_VOLUMES_IMAGES") != "1")
            throw new InvalidOperationException("Runs only on CI runners: it creates, attaches and removes a virtual disk.");
        string label = "RUNIC" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        string root = Path.Combine(Path.GetTempPath(), label);
        Directory.CreateDirectory(root);
        string image = Path.Combine(root, label + ".vhd");
        var volumes = WindowsPlatformProvider.CreateVolumes();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var changes = Channel.CreateUnbounded<PlatformResult<DesktopVolumeChange>>();
        var watching = Task.Run(async () =>
        {
            try { await foreach (var change in volumes.WatchAsync(timeout.Token)) changes.Writer.TryWrite(change); }
            catch (OperationCanceledException) { }
            finally { changes.Writer.TryComplete(); }
        });
        bool attached = false;
        try
        {
            var first = await changes.Reader.ReadAsync(timeout.Token);
            Check(first is PlatformResult<DesktopVolumeChange>.Success { Value: DesktopVolumeChange.Snapshot }, $"the watch starts with a snapshot: {first}");
            var listed = await volumes.ListAsync(timeout.Token) is PlatformResult<ImmutableArray<DesktopVolume>>.Success(var all) ? all : default;
            Check(!listed.IsDefault, "the volumes are listed");
            foreach (var volume in listed) Console.WriteLine($"Listed: {Describe(volume)}");
            string systemRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
            var system = listed.Single(v => v.MountPaths.Contains(systemRoot, StringComparer.OrdinalIgnoreCase));
            Check(system is { IsSystem: true, CanEject: false, FileSystem: "NTFS", Size: > 0, DriveId: not null }, $"the system volume: {Describe(system)}");
            Check(await volumes.UnmountAsync(system.Id, timeout.Token) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
                "the system volume is not dismounted");
            Check(await volumes.MountAsync("\\\\?\\Volume{00000000-0000-0000-0000-000000000000}\\", timeout.Token)
                is PlatformResult<DesktopVolume>.Failed { Code: PlatformFailureCode.NotFound }, "an unknown volume");

            Diskpart($"""
                create vdisk file="{image}" maximum=64 type=expandable
                select vdisk file="{image}"
                attach vdisk
                online disk noerr
                attributes disk clear readonly noerr
                convert mbr noerr
                create partition primary
                format fs=ntfs label="{label}" quick
                assign
                """);
            attached = true;
            var added = await WaitForAsync(changes.Reader, v => v.DisplayName == label && !v.MountPaths.IsEmpty, timeout.Token);
            Console.WriteLine($"Attached: {Describe(added)}");
            Check(added is { FileSystem: "NTFS", Size: > 0, IsSystem: false, IsReadOnly: false, IsLocked: false, CanMount: false, CanUnmount: false, DriveId: not null }
                && added.DriveId != system.DriveId, $"the attached volume: {Describe(added)}");
            Check(Directory.Exists(added.MountPaths[0]), "its drive letter is a directory");
            Check(await volumes.MountAsync(added.Id, timeout.Token) is PlatformResult<DesktopVolume>.Success { Value.MountPaths.IsEmpty: false }, "mounting it returns it");
            Check(await volumes.UnmountAsync(added.Id, timeout.Token) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
                "unmounting is not offered");

            if (added.CanEject)
            {
                string file = Path.Combine(added.MountPaths[0], "open.txt");
                using (var open = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    var busy = await volumes.EjectAsync(added.Id, timeout.Token);
                    Console.WriteLine($"Eject with an open file: {busy}");
                    Check(busy is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy }, $"an open file vetoes the removal: {busy}");
                }
                var ejected = await volumes.EjectAsync(added.Id, timeout.Token);
                Console.WriteLine($"Eject: {ejected}");
                Check(ejected is PlatformResult<PlatformUnit>.Success, $"the drive is removed: {ejected}");
                await WaitForRemovalAsync(changes.Reader, added.Id, timeout.Token);
                Diskpart($"""
                    select vdisk file="{image}"
                    detach vdisk noerr
                    """);
            }
            else
            {
                var refused = await volumes.EjectAsync(added.Id, timeout.Token);
                Console.WriteLine($"The virtual disk has no removable device node; eject: {refused}");
                Check(refused is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable), $"eject without a removable device: {refused}");
                // The Configuration Manager request itself, on the virtual disk's own device node: a file open on
                // the volume must keep the disk, whatever Windows says about removing it.
                PlatformResult<PlatformUnit> vetoed;
                using (var open = new FileStream(Path.Combine(added.MountPaths[0], "open.txt"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                    vetoed = WindowsVolumeSource.RequestEjectForTest(added.DriveId!);
                Console.WriteLine($"CM_Request_Device_EjectW on the virtual disk with an open file: {vetoed}");
                Check(vetoed is not PlatformResult<PlatformUnit>.Success, $"an open file vetoes the request: {vetoed}");
                var direct = WindowsVolumeSource.RequestEjectForTest(added.DriveId!);
                Console.WriteLine($"CM_Request_Device_EjectW on the virtual disk: {direct}");
                bool removed = direct is PlatformResult<PlatformUnit>.Success;
                if (removed) await WaitForRemovalAsync(changes.Reader, added.Id, timeout.Token);
                Diskpart($"""
                    select vdisk file="{image}"
                    detach vdisk noerr
                    """, check: removed is false);
                if (!removed) await WaitForRemovalAsync(changes.Reader, added.Id, timeout.Token);
            }

            // Attached read-only, the same volume is read-only.
            Diskpart($"""
                select vdisk file="{image}"
                attach vdisk readonly
                """);
            var readOnly = await WaitForAsync(changes.Reader, v => v.DisplayName == label, timeout.Token);
            Console.WriteLine($"Attached read-only: {Describe(readOnly)}");
            Check(readOnly.IsReadOnly, $"the read-only volume: {Describe(readOnly)}");
            Diskpart($"""
                select vdisk file="{image}"
                detach vdisk
                """);
            attached = false;
            await WaitForRemovalAsync(changes.Reader, readOnly.Id, timeout.Token);
            Console.WriteLine("PASS Windows volumes: listing, the system volume, a virtual disk attached, read-only and removed, mount model, eject.");
        }
        finally
        {
            if (attached)
                Diskpart($"""
                    select vdisk file="{image}"
                    detach vdisk noerr
                    """, check: false);
            await timeout.CancelAsync();
            await watching;
            await volumes.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<DesktopVolume> WaitForAsync(ChannelReader<PlatformResult<DesktopVolumeChange>> changes, Func<DesktopVolume, bool> match, CancellationToken token)
    {
        while (true)
        {
            var change = await changes.ReadAsync(token);
            Console.WriteLine($"Change: {change}");
            var volume = change switch
            {
                PlatformResult<DesktopVolumeChange>.Success { Value: DesktopVolumeChange.Added(var v) } => v,
                PlatformResult<DesktopVolumeChange>.Success { Value: DesktopVolumeChange.Changed(var v) } => v,
                _ => null,
            };
            if (volume is not null && match(volume)) return volume;
        }
    }

    private static async Task WaitForRemovalAsync(ChannelReader<PlatformResult<DesktopVolumeChange>> changes, string id, CancellationToken token)
    {
        while (true)
        {
            var change = await changes.ReadAsync(token);
            Console.WriteLine($"Change: {change}");
            if (change is PlatformResult<DesktopVolumeChange>.Success { Value: DesktopVolumeChange.Removed(var removed) } && removed == id) return;
        }
    }

    private static void Diskpart(string script, bool check = true)
    {
        string path = Path.Combine(Path.GetTempPath(), $"runic-diskpart-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, script);
        try
        {
            using var process = Process.Start(new ProcessStartInfo("diskpart.exe", $"/s \"{path}\"") { RedirectStandardOutput = true, UseShellExecute = false })!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Console.WriteLine(output);
            if (check && process.ExitCode != 0) throw new InvalidOperationException($"diskpart exited with {process.ExitCode}");
        }
        finally { File.Delete(path); }
    }

    private static string Describe(DesktopVolume v) =>
        $"{v.Id} '{v.DisplayName}' [{string.Join(", ", v.MountPaths)}] {v.FileSystem} {v.Size} drive={v.DriveId} '{v.DriveName}' removable={v.IsRemovable} "
        + $"readOnly={v.IsReadOnly} system={v.IsSystem} eject={v.CanEject}";

    private static async Task<DesktopVolumeChange> Next(IAsyncEnumerator<PlatformResult<DesktopVolumeChange>> watch) =>
        await NextResult(watch) is PlatformResult<DesktopVolumeChange>.Success(var change) ? change : throw new InvalidOperationException("The watch did not report a change.");

    private static async Task<PlatformResult<DesktopVolumeChange>> NextResult(IAsyncEnumerator<PlatformResult<DesktopVolumeChange>> watch) =>
        await watch.MoveNextAsync() ? watch.Current : throw new InvalidOperationException("The watch ended.");

    private static async Task<bool> Ends(Task<bool> pending)
    {
        try { return !await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { return true; }
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Volume check failed: " + message);
    }

    private sealed class FakeSource : IWindowsVolumeSource
    {
        private readonly List<Action> _subscribers = [];
        internal ImmutableArray<DesktopVolume> Volumes = [];
        internal volatile PlatformResult<ImmutableArray<DesktopVolume>>? Listing;
        internal bool CanSubscribe = true;
        internal PlatformResult<PlatformUnit> EjectResult = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        internal List<string> Ejected { get; } = [];
        internal int Subscribers { get { lock (_subscribers) return _subscribers.Count; } }

        public PlatformResult<ImmutableArray<DesktopVolume>> List() =>
            Listing ?? new PlatformResult<ImmutableArray<DesktopVolume>>.Success([.. Volumes.OrderBy(static v => v.Id, StringComparer.Ordinal)]);

        public IDisposable? Subscribe(Action changed)
        {
            if (!CanSubscribe) return null;
            lock (_subscribers) _subscribers.Add(changed);
            return new Subscription(this, changed);
        }

        internal void Notify()
        {
            Action[] subscribers;
            lock (_subscribers) subscribers = [.. _subscribers];
            foreach (var subscriber in subscribers) subscriber();
        }

        public PlatformResult<PlatformUnit> Eject(string driveId)
        {
            Ejected.Add(driveId);
            return EjectResult;
        }

        private sealed class Subscription(FakeSource source, Action changed) : IDisposable
        {
            public void Dispose() { lock (source._subscribers) source._subscribers.Remove(changed); }
        }
    }
}
