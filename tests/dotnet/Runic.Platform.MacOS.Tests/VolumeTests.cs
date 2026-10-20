using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using TUnit.Core;

namespace Runic.Platform.MacOS.Tests;

internal sealed partial class VolumeTests
{
    // An Apple silicon startup disk: the iSCPreboot, system and recovery containers on disk0, the sealed system
    // snapshot at /, the Data volume and the other system volumes; and a USB stick with an EFI partition and exFAT.
    private static readonly ImmutableArray<MacDisk> Layout =
    [
        new("disk0") { Whole = true, Internal = true, Vendor = "APPLE", Model = "SSD AP0512Q", Size = 500_000_000_000 },
        new("disk0s2") { Internal = true, PhysicalDisk = "disk0", Content = "7C3457EF-0000-11AA-AA11-00306543ECAC" },
        new("disk3") { Whole = true, Internal = true, PhysicalDisk = "disk0" },
        new("disk3s1") { VolumeKind = "apfs", Mountable = true, VolumeName = "Macintosh HD", Internal = true, PhysicalDisk = "disk0" },
        new("disk3s1s1") { VolumeKind = "apfs", Mountable = true, VolumeName = "Macintosh HD", VolumePath = "/", Writable = false, Internal = true, PhysicalDisk = "disk0" },
        new("disk3s5") { VolumeKind = "apfs", Mountable = true, VolumeName = "Data", VolumePath = "/System/Volumes/Data", Internal = true, PhysicalDisk = "disk0" },
        new("disk2s1") { VolumeKind = "apfs", Mountable = true, VolumeName = "Recovery", Internal = true, PhysicalDisk = "disk0" },
        new("disk4") { Whole = true, Ejectable = true, Vendor = "SanDisk", Model = "Ultra", Size = 64_000_000_000 },
        new("disk4s1") { VolumeKind = "msdos", Mountable = true, VolumeName = "EFI", Content = "C12A7328-F81F-11D2-BA4B-00A0C93EC93B", Ejectable = true, PhysicalDisk = "disk4" },
        new("disk4s2") { VolumeKind = "exfat", Mountable = true, VolumeName = "STICK", VolumePath = "/Volumes/STICK", Size = 63_000_000_000, Ejectable = true, PhysicalDisk = "disk4" },
        new("disk4s3") { VolumeKind = "exfat", Mountable = true, Size = 1_000_000_000, Ejectable = true, PhysicalDisk = "disk4" },
        // A Cryptex image the system mounts under /System, and a simulator runtime it mounts hidden (nobrowse).
        new("disk6") { Whole = true, Ejectable = true, Removable = true, Vendor = "Apple", Model = "Disk Image" },
        new("disk6s1") { VolumeKind = "apfs", Mountable = true, VolumeName = "Cryptex", VolumePath = "/System/Library/AssetsV2/x/.AssetData", Writable = false, PhysicalDisk = "disk6" },
        new("disk8") { Whole = true, Ejectable = true, Removable = true, Vendor = "Apple", Model = "Disk Image" },
        new("disk8s1") { VolumeKind = "apfs", Mountable = true, VolumeName = "iOS Simulator", VolumePath = "/Library/Developer/CoreSimulator/Volumes/iOS", Hidden = true, PhysicalDisk = "disk8" },
    ];

    [Test, DisplayName("volumes: Disk Arbitration's disks become volumes with their drive and flags")]
    public void Model()
    {
        var volumes = MacVolumes.Volumes(Layout).ToDictionary(static v => v.Id);
        Check(!volumes.ContainsKey("disk0") && !volumes.ContainsKey("disk3") && !volumes.ContainsKey("disk0s2"), "disks without a filesystem are not volumes");
        Check(volumes["disk3s1s1"] is { IsSystem: true, IsReadOnly: true, CanUnmount: false, CanEject: false, DriveId: "disk0", DriveName: "APPLE SSD AP0512Q", MountPaths: ["/"] },
            "the startup volume belongs to the system and its drive");
        Check(volumes["disk3s5"] is { IsSystem: true, CanUnmount: false } && volumes["disk2s1"] is { IsSystem: true, CanMount: false } && volumes["disk3s1"].IsSystem,
            "the Data volume, the unmounted Recovery and the system volume under the snapshot belong to the system");
        Check(!volumes["disk3s5"].IsRemovable, "the internal disk is not removable");
        Check(volumes["disk4s2"] is { DisplayName: "STICK", FileSystem: "exfat", IsSystem: false, IsRemovable: true, CanUnmount: true, CanMount: false, CanEject: true,
            DriveId: "disk4", DriveName: "SanDisk Ultra", Size: 63_000_000_000 }, "the stick's mounted volume");
        Check(volumes["disk4s1"] is { IsSystem: true, CanMount: false, CanEject: true }, "an EFI system partition belongs to the system");
        Check(volumes["disk4s3"] is { CanMount: true, CanUnmount: false, MountPaths.IsEmpty: true } && volumes["disk4s3"].DisplayName == "SanDisk Ultra (1 GB)",
            "an unmounted volume without a name is named after its drive and size");
        Check(volumes["disk6s1"] is { IsSystem: true, CanUnmount: false, CanEject: false } && volumes["disk8s1"] is { IsSystem: true, CanUnmount: false, CanEject: false },
            "images the system mounts under /System or hides from Finder are left alone");
        Check(MacVolumes.Volumes(Layout).Select(static v => v.Id).SequenceEqual(volumes.Keys.Order(StringComparer.Ordinal)), "the volumes are ordered by ID");
    }

    [Test, DisplayName("volumes: dissenter statuses map to typed outcomes")]
    public void Results()
    {
#pragma warning disable CA1416 // Result is pure; nothing here calls Disk Arbitration.
        Check(MacVolumeSource.Result<PlatformUnit>(unchecked((int)0xF8DA0002), false) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy, Diagnostic: { Domain: "DAReturn", Code: 0xF8DA0002 } }, "busy");
        Check(MacVolumeSource.Result<PlatformUnit>(unchecked((int)0xF8DA0004), false) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy }, "exclusive access");
        Check(MacVolumeSource.Result<PlatformUnit>(0xC000 | 16, false) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy }, "EBUSY");
        Check(MacVolumeSource.Result<PlatformUnit>(unchecked((int)0xF8DA0008), false) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }, "not permitted");
        Check(MacVolumeSource.Result<PlatformUnit>(unchecked((int)0xF8DA0009), false) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }, "not privileged");
        Check(MacVolumeSource.Result<PlatformUnit>(0xC000 | 13, false) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }, "EACCES");
        Check(MacVolumeSource.Result<PlatformUnit>(unchecked((int)0xF8DA0006), false) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound }, "not found");
        Check(MacVolumeSource.Result<PlatformUnit>(unchecked((int)0xF8DA000C), false) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "unsupported");
        Check(MacVolumeSource.Result<PlatformUnit>(unchecked((int)0xF8DA0007), false) is PlatformResult<PlatformUnit>.Success, "an unmount of a volume that is not mounted");
        Check(MacVolumeSource.Result<string?>(unchecked((int)0xF8DA0007), true) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.Conflict }, "a mount that finds nothing mounted");
        Check(MacVolumeSource.Result<PlatformUnit>(unchecked((int)0xF8DA0001), false) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.Other }, "other errors");
#pragma warning restore CA1416
    }

    [Test, DisplayName("volumes: mount, unmount and eject go through Disk Arbitration; the startup disk is left alone")]
    public async Task Operations()
    {
        var source = new FakeSource { Disks = Layout };
        await using var volumes = new MacVolumes(source, TimeSpan.Zero);

        Check(await volumes.MountAsync("disk4s2") is PlatformResult<DesktopVolume>.Success { Value.MountPaths: ["/Volumes/STICK"] } && source.Calls.Count == 0,
            "a mounted volume is returned as it is");
        source.MountPath = "/Volumes/Untitled";
        Check(await volumes.MountAsync("disk4s3") is PlatformResult<DesktopVolume>.Success { Value: { MountPaths: ["/Volumes/Untitled"], CanMount: false, CanUnmount: true } }
            && source.Calls is ["mount disk4s3"], "a mount reports its path before the description changes");
        Check(await volumes.MountAsync("disk2s1") is PlatformResult<DesktopVolume>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "the Recovery volume is not mounted");
        Check(await volumes.MountAsync("disk9s1") is PlatformResult<DesktopVolume>.Failed { Code: PlatformFailureCode.NotFound }, "mount of an unknown BSD name");
        source.Result = new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied);
        Check(await volumes.MountAsync("disk4s3") is PlatformResult<DesktopVolume>.Failed { Code: PlatformFailureCode.PermissionDenied }, "a refused mount");
        source.Result = null;
        source.Calls.Clear();

        Check(await volumes.UnmountAsync("disk4s3") is PlatformResult<PlatformUnit>.Success && source.Calls.Count == 0, "unmounting a volume that is not mounted succeeds");
        Check(await volumes.UnmountAsync("disk3s1s1") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable)
            && await volumes.UnmountAsync("disk3s5") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable)
            && source.Calls.Count == 0, "the startup disk's volumes are never unmounted");
        Check(await volumes.UnmountAsync("disk4s2") is PlatformResult<PlatformUnit>.Success && source.Calls is ["unmount disk4s2"], "an unmount");

        source.Calls.Clear();
        source.Disks = Layout.Replace(Layout.Single(static d => d.BsdName == "disk4s3"), Layout.Single(static d => d.BsdName == "disk4s3") with { VolumePath = "/Volumes/Two" });
        Check(await volumes.EjectAsync("disk4s1") is PlatformResult<PlatformUnit>.Success && source.Calls is ["unmount disk4s2", "unmount disk4s3", "eject disk4"],
            "eject unmounts every mounted volume of the drive, then ejects its whole disk");
        source.Calls.Clear();
        source.Result = new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.ResourceBusy);
        Check(await volumes.EjectAsync("disk4s2") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy } && source.Calls is ["unmount disk4s2"],
            "a busy volume stops the eject");
        source.Result = null;
        source.Calls.Clear();
        Check(await volumes.EjectAsync("disk3s5") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable) && source.Calls.Count == 0,
            "the startup disk is never ejected");
        Check(await volumes.EjectAsync("disk9s1") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound }, "eject of an unknown BSD name");

        source.Disks = default;
        Check(await volumes.ListAsync() is PlatformResult<ImmutableArray<DesktopVolume>>.Unavailable(PlatformUnavailableReason.BackendUnavailable)
            && await volumes.MountAsync("disk4s3") is PlatformResult<DesktopVolume>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "without a Disk Arbitration session");
        await Throws<ArgumentException>(() => volumes.EjectAsync("").AsTask());
        await volumes.DisposeAsync();
        Check(source.Disposed, "disposal ends the session");
        await Throws<ObjectDisposedException>(() => volumes.ListAsync().AsTask());
    }

    [Test, DisplayName("volumes: the watch reports a snapshot, then each change Disk Arbitration announces")]
    public async Task Watch()
    {
        var source = new FakeSource { Disks = Layout };
        await using var volumes = new MacVolumes(source, TimeSpan.Zero);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using (var watch = volumes.WatchAsync(timeout.Token).GetAsyncEnumerator(timeout.Token))
        {
            Check(await Next(watch) is DesktopVolumeChange.Snapshot { Volumes.Length: 9 }, "a snapshot comes first");
            source.Disks = [.. Layout.Where(static d => !d.BsdName.StartsWith("disk4", StringComparison.Ordinal)), new MacDisk("disk9") { Whole = true, VolumeKind = "hfs", Mountable = true, VolumeName = "Image" }];
            source.Notify();
            Check(await Next(watch) is DesktopVolumeChange.Removed("disk4s1") && await Next(watch) is DesktopVolumeChange.Removed("disk4s2")
                && await Next(watch) is DesktopVolumeChange.Removed("disk4s3"), "the stick's volumes are removed");
            Check(await Next(watch) is DesktopVolumeChange.Added { Volume: { Id: "disk9", DisplayName: "Image", DriveId: "disk9" } }, "a disk image with a filesystem on its whole disk");
            source.Disks = source.Disks.Replace(source.Disks[^1], source.Disks[^1] with { VolumePath = "/Volumes/Image" });
            source.Notify();
            Check(await Next(watch) is DesktopVolumeChange.Changed { Volume.MountPaths: ["/Volumes/Image"] }, "a mount is a change");
        }
        Check(source.Subscribers == 0, "ending the watch removes its subscription");

        var without = new FakeSource { Disks = default };
        await using var unavailable = new MacVolumes(without, TimeSpan.Zero);
        await using var lone = unavailable.WatchAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Check(await lone.MoveNextAsync() && lone.Current is PlatformResult<DesktopVolumeChange>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "without Disk Arbitration the watch reports unavailable once");
        var waiting = lone.MoveNextAsync().AsTask();
        await Task.Delay(100);
        Check(!waiting.IsCompleted, "and then waits");
        await unavailable.DisposeAsync();
        try { await waiting.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Volume check failed: the watch continued after disposal."); }
        catch (OperationCanceledException) { }
    }

    // Disk images that hdiutil creates and attaches for the run, with names unique to it: the runner's own disks are
    // only listed. CI only (RUNIC_TEST_VOLUMES_IMAGES=1).
    [Test, Explicit, Category("NativeVolumes"), DisplayName("volumes: disk images attach, mount, unmount and eject through Disk Arbitration")]
    public async Task DiskImagesAsync()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Disk Arbitration requires macOS.");
        if (Environment.GetEnvironmentVariable("RUNIC_TEST_VOLUMES_IMAGES") != "1")
            throw new InvalidOperationException("Runs only on CI runners: it creates, attaches and ejects disk images.");
        string unique = "RUNIC" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        string root = Path.Combine(Path.GetTempPath(), unique);
        Directory.CreateDirectory(root);
        var attached = new List<string>();
        var volumes = MacOSPlatformProvider.CreateVolumes();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var changes = Channel.CreateUnbounded<PlatformResult<DesktopVolumeChange>>();
        var watching = Task.Run(async () =>
        {
            try { await foreach (var change in volumes.WatchAsync(timeout.Token)) changes.Writer.TryWrite(change); }
            catch (OperationCanceledException) { }
            finally { changes.Writer.TryComplete(); }
        });
        try
        {
            var first = await changes.Reader.ReadAsync(timeout.Token);
            Check(first is PlatformResult<DesktopVolumeChange>.Success { Value: DesktopVolumeChange.Snapshot }, $"the watch starts with a snapshot: {first}");
            var listed = await volumes.ListAsync(timeout.Token) is PlatformResult<ImmutableArray<DesktopVolume>>.Success(var all) ? all : default;
            Check(!listed.IsDefault, "the volumes are listed");
            foreach (var volume in listed) Console.WriteLine($"Listed: {Describe(volume)}");
            var startup = listed.Single(static v => v.MountPaths.Contains("/"));
            Check(startup is { IsSystem: true, CanUnmount: false, CanEject: false, FileSystem: "apfs", DriveId: not null }, $"the startup volume: {Describe(startup)}");
            Check(await volumes.UnmountAsync(startup.Id, timeout.Token) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
                "the startup volume is not unmounted");
            // The runner's Cryptex and simulator images are mounted under /System or hidden from Finder.
            Check(listed.Where(static v => v.MountPaths.Any(static p => p.StartsWith("/System/", StringComparison.Ordinal) || p.StartsWith("/Library/Developer/CoreSimulator/", StringComparison.Ordinal)))
                .All(static v => v is { IsSystem: true, CanUnmount: false, CanEject: false }), "the system's own disk images are left alone");
            Check(await volumes.MountAsync("disk999s9", timeout.Token) is PlatformResult<DesktopVolume>.Failed { Code: PlatformFailureCode.NotFound }, "an unknown volume");

            // HFS+ on a partitioned image: unmount, a busy unmount, mount and eject.
            string hfs = unique + "H";
            Run("hdiutil", "create", "-size", "32m", "-fs", "HFS+", "-volname", hfs, Path.Combine(root, "hfs.dmg"));
            attached.Add(Attach(Path.Combine(root, "hfs.dmg")));
            var volumeHfs = await WaitForAsync(changes.Reader, v => v.DisplayName == hfs && !v.MountPaths.IsEmpty, timeout.Token);
            Console.WriteLine($"Attached: {Describe(volumeHfs)}");
            Check(volumeHfs is { FileSystem: "hfs", IsSystem: false, IsReadOnly: false, CanUnmount: true, CanEject: true, IsRemovable: true, DriveId: not null }
                && volumeHfs.DriveId == attached[^1] && volumeHfs.MountPaths[0] == "/Volumes/" + hfs, $"the attached image: {Describe(volumeHfs)}");

            string busyFile = Path.Combine(volumeHfs.MountPaths[0], "open.txt");
            using (var open = new FileStream(busyFile, FileMode.Create, FileAccess.ReadWrite))
            {
                var busy = await volumes.UnmountAsync(volumeHfs.Id, timeout.Token);
                Console.WriteLine($"Unmount with an open file: {busy}");
                Check(busy is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy }, $"an open file keeps the volume mounted: {busy}");
            }
            Check(await volumes.UnmountAsync(volumeHfs.Id, timeout.Token) is PlatformResult<PlatformUnit>.Success, "the volume is unmounted");
            await WaitForAsync(changes.Reader, v => v.Id == volumeHfs.Id && v.MountPaths.IsEmpty && v.CanMount, timeout.Token);
            Check(await volumes.UnmountAsync(volumeHfs.Id, timeout.Token) is PlatformResult<PlatformUnit>.Success, "unmounting it again succeeds");
            var mounted = await volumes.MountAsync(volumeHfs.Id, timeout.Token);
            Console.WriteLine($"Mount: {mounted}");
            Check(mounted is PlatformResult<DesktopVolume>.Success { Value.MountPaths: [var path] } && Directory.Exists(path), $"the volume is mounted again: {mounted}");
            var ejected = await volumes.EjectAsync(volumeHfs.Id, timeout.Token);
            Console.WriteLine($"Eject: {ejected}");
            Check(ejected is PlatformResult<PlatformUnit>.Success, $"the image is ejected: {ejected}");
            await WaitForRemovalAsync(changes.Reader, volumeHfs.Id, timeout.Token);
            attached.RemoveAt(attached.Count - 1);

            // APFS: the volume is on a synthesized container; it belongs to the image's own whole disk.
            string apfs = unique + "A";
            Run("hdiutil", "create", "-size", "64m", "-fs", "APFS", "-volname", apfs, Path.Combine(root, "apfs.dmg"));
            attached.Add(Attach(Path.Combine(root, "apfs.dmg")));
            var volumeApfs = await WaitForAsync(changes.Reader, v => v.DisplayName == apfs && !v.MountPaths.IsEmpty, timeout.Token);
            Console.WriteLine($"Attached: {Describe(volumeApfs)}");
            Check(volumeApfs is { FileSystem: "apfs", IsSystem: false, CanEject: true } && volumeApfs.DriveId == attached[^1],
                $"the APFS volume belongs to the image's disk {attached[^1]}: {Describe(volumeApfs)}");
            var ejectedApfs = await volumes.EjectAsync(volumeApfs.Id, timeout.Token);
            Console.WriteLine($"Eject: {ejectedApfs}");
            Check(ejectedApfs is PlatformResult<PlatformUnit>.Success, $"the APFS image is ejected: {ejectedApfs}");
            await WaitForRemovalAsync(changes.Reader, volumeApfs.Id, timeout.Token);
            attached.RemoveAt(attached.Count - 1);

            // Attached read-only, the volume is read-only.
            attached.Add(Attach(Path.Combine(root, "hfs.dmg"), "-readonly"));
            var readOnly = await WaitForAsync(changes.Reader, v => v.DisplayName == hfs && !v.MountPaths.IsEmpty, timeout.Token);
            Console.WriteLine($"Attached read-only: {Describe(readOnly)}");
            Check(readOnly.IsReadOnly, $"the read-only volume: {Describe(readOnly)}");
            Check(await volumes.EjectAsync(readOnly.Id, timeout.Token) is PlatformResult<PlatformUnit>.Success, "the read-only image is ejected");
            await WaitForRemovalAsync(changes.Reader, readOnly.Id, timeout.Token);
            attached.RemoveAt(attached.Count - 1);
            Console.WriteLine("PASS macOS volumes: listing, the startup volume, HFS+ and APFS images mounted, unmounted, busy and ejected, read-only.");
        }
        finally
        {
            foreach (string device in attached) Run("hdiutil", check: false, "detach", "-force", "/dev/" + device);
            await timeout.CancelAsync();
            await watching;
            await volumes.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Attaches an image and returns its whole disk's BSD name, the first /dev/diskN that hdiutil prints.
    private static string Attach(string image, params string[] options)
    {
        string output = Run("hdiutil", ["attach", .. options, image]);
        return WholeDisk().Match(output) is { Success: true } match ? match.Groups[1].Value : throw new InvalidOperationException($"hdiutil printed no disk: {output}");
    }

    [GeneratedRegex(@"/dev/(disk\d+)\s")]
    private static partial Regex WholeDisk();

    private static string Run(string program, params string[] arguments) => Run(program, check: true, arguments);

    private static string Run(string program, bool check, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Console.WriteLine($"{program} {string.Join(' ', arguments)}: {output.Trim()}");
        if (check && process.ExitCode != 0) throw new InvalidOperationException($"{program} exited with {process.ExitCode}");
        return output;
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

    private static string Describe(DesktopVolume v) =>
        $"{v.Id} '{v.DisplayName}' [{string.Join(", ", v.MountPaths)}] {v.FileSystem} {v.Size} drive={v.DriveId} '{v.DriveName}' removable={v.IsRemovable} "
        + $"readOnly={v.IsReadOnly} system={v.IsSystem} mount={v.CanMount} unmount={v.CanUnmount} eject={v.CanEject}";

    private static async Task<DesktopVolumeChange> Next(IAsyncEnumerator<PlatformResult<DesktopVolumeChange>> watch) =>
        await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopVolumeChange>.Success(var change)
            ? change : throw new InvalidOperationException("The watch did not report a change.");

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

    private sealed class FakeSource : IMacVolumeSource, IDisposable
    {
        private readonly List<Action> _subscribers = [];
        internal ImmutableArray<MacDisk> Disks;
        internal string? MountPath;
        internal PlatformResult<PlatformUnit>? Result;
        internal List<string> Calls { get; } = [];
        internal bool Disposed;
        internal int Subscribers { get { lock (_subscribers) return _subscribers.Count; } }

        PlatformResult<ImmutableArray<MacDisk>> IMacVolumeSource.Disks() => Disks.IsDefault
            ? new PlatformResult<ImmutableArray<MacDisk>>.Unavailable(PlatformUnavailableReason.BackendUnavailable)
            : new PlatformResult<ImmutableArray<MacDisk>>.Success(Disks);

        public IDisposable? Subscribe(Action changed)
        {
            if (Disks.IsDefault) return null;
            lock (_subscribers) _subscribers.Add(changed);
            return new Subscription(this, changed);
        }

        internal void Notify()
        {
            Action[] subscribers;
            lock (_subscribers) subscribers = [.. _subscribers];
            foreach (var subscriber in subscribers) subscriber();
        }

        public Task<PlatformResult<string?>> MountAsync(string bsdName)
        {
            Calls.Add("mount " + bsdName);
            return Task.FromResult<PlatformResult<string?>>(Result switch
            {
                PlatformResult<PlatformUnit>.Failed failed => new PlatformResult<string?>.Failed(failed.Code),
                _ => new PlatformResult<string?>.Success(MountPath),
            });
        }

        public Task<PlatformResult<PlatformUnit>> UnmountAsync(string bsdName)
        {
            Calls.Add("unmount " + bsdName);
            return Task.FromResult(Result ?? new PlatformResult<PlatformUnit>.Success(new PlatformUnit()));
        }

        public Task<PlatformResult<PlatformUnit>> EjectAsync(string bsdName)
        {
            Calls.Add("eject " + bsdName);
            return Task.FromResult(Result ?? new PlatformResult<PlatformUnit>.Success(new PlatformUnit()));
        }

        public void Dispose() => Disposed = true;

        private sealed class Subscription(FakeSource source, Action changed) : IDisposable
        {
            public void Dispose() { lock (source._subscribers) source._subscribers.Remove(changed); }
        }
    }
}
