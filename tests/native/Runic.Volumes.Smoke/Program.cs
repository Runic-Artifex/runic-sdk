using Runic.Platform;
using Runic.Platform.Linux.Portal;

// A Linux smoke: the portal services are Linux only.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

// Volumes against the machine's UDisks2 on the system bus. Only nixos/volumes runs it, inside a VM with a scratch disk:
//   list                    print each volume
//   mount <id> | unmount <id> | eject <id>
//   mount-ro <id>           mount read-only
//   unlock <id>             unlock with the passphrase on the first line of standard input
//   watch <seconds>         print each change for that long
// Each command ends with "result: ..." for the test script.
await using var volumes = new PortalApplication().CreateVolumes();
string command = args.Length > 0 ? args[0] : "list";
switch (command)
{
    case "list":
        Report(await volumes.ListAsync(), static list =>
        {
            foreach (var volume in list) Console.WriteLine(Describe(volume));
            return $"{list.Length} volumes";
        });
        break;
    case "mount":
        Report(await volumes.MountAsync(args[1]), static volume => Describe(volume));
        break;
    case "mount-ro":
        Report(await volumes.MountAsync(args[1], new DesktopVolumeMountOptions { ReadOnly = true }, default), static volume => Describe(volume));
        break;
    case "unlock":
        char[] passphrase = (Console.In.ReadLine() ?? "").ToCharArray();
        try { Report(await volumes.UnlockAsync(args[1], passphrase, default), static volume => Describe(volume)); }
        finally { Array.Clear(passphrase); }
        break;
    case "unmount":
        Report(await volumes.UnmountAsync(args[1]), static _ => "unmounted");
        break;
    case "eject":
        Report(await volumes.EjectAsync(args[1]), static _ => "ejected");
        break;
    case "watch":
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture))))
        {
            try
            {
                await foreach (var change in volumes.WatchAsync(deadline.Token))
                {
                    Console.WriteLine(change switch
                    {
                        PlatformResult<DesktopVolumeChange>.Success(DesktopVolumeChange.Snapshot snapshot) => $"change: snapshot {snapshot.Volumes.Length}",
                        PlatformResult<DesktopVolumeChange>.Success(DesktopVolumeChange.Added added) => $"change: added {Describe(added.Volume)}",
                        PlatformResult<DesktopVolumeChange>.Success(DesktopVolumeChange.Changed changed) => $"change: changed {Describe(changed.Volume)}",
                        PlatformResult<DesktopVolumeChange>.Success(DesktopVolumeChange.Removed removed) => $"change: removed {removed.Id}",
                        PlatformResult<DesktopVolumeChange>.Unavailable unavailable => $"change: unavailable {unavailable.Reason}",
                        _ => "change: other",
                    });
                    Console.Out.Flush();
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
        Console.WriteLine("result: watched");
        break;
    default:
        Console.Error.WriteLine($"Unknown command {command}.");
        return 2;
}
return 0;

static string Describe(DesktopVolume volume) =>
    $"volume {volume.Id} name={volume.DisplayName} fs={volume.FileSystem} mounts=[{string.Join(',', volume.MountPaths)}] removable={volume.IsRemovable} " +
    $"system={volume.IsSystem} locked={volume.IsLocked} mount={volume.CanMount} unmount={volume.CanUnmount} eject={volume.CanEject} " +
    $"encrypted={volume.IsEncrypted} unlock={volume.UnlockMethod}";

static void Report<T>(PlatformResult<T> result, Func<T, string> success) => Console.WriteLine(result switch
{
    PlatformResult<T>.Success(var value) => $"result: success {success(value)}",
    PlatformResult<T>.Unavailable unavailable => $"result: unavailable {unavailable.Reason}",
    PlatformResult<T>.Failed failed => $"result: failed {failed.Code} {failed.Diagnostic?.Domain}",
    _ => "result: unknown",
});
