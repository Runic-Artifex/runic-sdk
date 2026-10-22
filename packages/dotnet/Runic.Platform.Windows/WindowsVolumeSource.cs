using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Runic.Platform.Runtime;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Diagnostics.Debug;
using Windows.Win32.System.Ioctl;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace Runic.Platform.Windows;

// The volume management API (FindFirstVolumeW, GetVolumePathNamesForVolumeNameW, GetVolumeInformationW,
// GetDiskFreeSpaceExW, GetDriveTypeW) lists the volumes; storage IOCTLs on a volume handle opened without access find
// its disk and partition, and the Configuration Manager (cfgmgr32) finds the disk's device node, its name and the
// removable device that "Safely Remove Hardware" would stop. CM_Register_Notification reports volume arrivals and
// removals. Every call runs on the caller's thread; no window or COM apartment is needed.
[SupportedOSPlatform("windows8.0")]
internal sealed unsafe class WindowsVolumeSource : IWindowsVolumeSource
{
    private const int VolumeNameLength = 64; // \\?\Volume{GUID}\ (49 characters) and its terminator
    private const int MaxPath = 261;
    private const uint FILE_DEVICE_DISK = 7;
    private const uint DRIVE_REMOVABLE = 2, DRIVE_FIXED = 3, DRIVE_CDROM = 5;
    // FVE_E_LOCKED_VOLUME: "This drive is locked by BitLocker Drive Encryption."
    internal const int LockedVolumeError = unchecked((int)0x80310000);
    // System.Volume.BitLockerProtection: 1 on, 2 off, 3 encrypting, 4 decrypting, 5 suspended, 6 on and locked.
    internal const uint ProtectionLocked = 6;
    // An ancestor this many levels up is no longer the drive's own hardware.
    private const int MaxDepth = 16;
    private static readonly Guid EfiSystemPartition = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    private static readonly Guid MicrosoftReservedPartition = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    private static readonly Guid RecoveryPartition = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");

    private sealed record Drive(string InstanceId, string? Name, bool Ejectable);

    public PlatformResult<ImmutableArray<DesktopVolume>> List()
    {
        // A drive without media must fail the query, not show the "There is no disk in the drive" dialog.
        THREAD_ERROR_MODE previous;
        if (!PInvoke.SetThreadErrorMode(THREAD_ERROR_MODE.SEM_FAILCRITICALERRORS | THREAD_ERROR_MODE.SEM_NOOPENFILEERRORBOX, &previous)) previous = 0;
        try { return ListVolumes(); }
        finally { PInvoke.SetThreadErrorMode(previous, null); }
    }

    private static PlatformResult<ImmutableArray<DesktopVolume>> ListVolumes()
    {
        // The BitLocker state is a shell property, which needs COM on this thread.
        int com = PInvoke.CoInitializeEx(null, global::Windows.Win32.System.Com.COINIT.COINIT_MULTITHREADED).Value;
        try { return ListVolumesCore(); }
        finally { if (com >= 0) PInvoke.CoUninitialize(); }
    }

    private static PlatformResult<ImmutableArray<DesktopVolume>> ListVolumesCore()
    {
        var drives = Drives();
        var volumes = new List<DesktopVolume>();
        Span<char> name = stackalloc char[VolumeNameLength];
        using (var find = PInvoke.FindFirstVolume(name))
        {
            if (find.IsInvalid) return Failure<ImmutableArray<DesktopVolume>>(Marshal.GetLastPInvokeError());
            do
            {
                if (Volume(Terminated(name), drives) is { } volume) volumes.Add(volume);
            }
            while (PInvoke.FindNextVolume((HANDLE)find.DangerousGetHandle(), name));
        }
        return new PlatformResult<ImmutableArray<DesktopVolume>>.Success(VolumeTracking.Sorted(volumes));
    }

    // A volume with a filesystem Windows recognizes, or one locked by BitLocker that has a drive letter to unlock it
    // through; one without media or unformatted (RAW) is left out because GetVolumeInformationW fails for it.
    private static DesktopVolume? Volume(string id, Dictionary<(uint Type, uint Number), Drive> drives)
    {
        Span<char> label = stackalloc char[MaxPath];
        Span<char> fileSystem = stackalloc char[MaxPath];
        if (!PInvoke.GetVolumeInformation(id, label, out _, out _, out uint flags, fileSystem))
            return LockedVolume(id, Marshal.GetLastPInvokeError(), drives);
        var paths = MountPaths(id);
        long? size = PInvoke.GetDiskFreeSpaceEx(id, out _, out ulong total, out _) && total > 0 ? (long)Math.Min(total, long.MaxValue) : null;
        uint type = PInvoke.GetDriveType(id);
        var (number, system) = Device(id);
        Drive? drive = number is { } key && drives.TryGetValue(key, out var found) ? found : null;
        string? systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        system |= systemRoot is not null && paths.Contains(systemRoot, StringComparer.OrdinalIgnoreCase);
        string text = Terminated(label);
        string name = text.Length > 0 ? text
            : drive?.Name is { } driveName ? size is { } known ? $"{driveName} ({VolumeTracking.Bytes(known)})" : driveName
            : paths.Length > 0 ? paths[0] : id;
        return new DesktopVolume(id, name)
        {
            MountPaths = paths,
            FileSystem = Terminated(fileSystem) is { Length: > 0 } fs ? fs : null,
            Size = size,
            DriveId = drive?.InstanceId,
            DriveName = drive?.Name,
            IsRemovable = type is DRIVE_REMOVABLE or DRIVE_CDROM || drive is { Ejectable: true },
            IsReadOnly = (flags & PInvoke.FILE_READ_ONLY_VOLUME) != 0,
            IsSystem = system,
            IsEncrypted = type is DRIVE_FIXED or DRIVE_REMOVABLE && paths.Length > 0 && IsEncrypted(BitLockerProtection(paths[0])),
            // An optical drive opens its tray (EjectMedia) where it is not a removable device of its own.
            CanEject = drive is { Ejectable: true } || type == DRIVE_CDROM,
        };
    }

    // A BitLocker volume that is locked: GetVolumeInformationW fails, and the shell reports it locked. Explorer's
    // unlock prompt needs its drive letter.
    private static DesktopVolume? LockedVolume(string id, int error, Dictionary<(uint Type, uint Number), Drive> drives)
    {
        var paths = MountPaths(id);
        if (paths.IsEmpty) return null;
        if (!IsLocked(error, BitLockerProtection(paths[0]))) return null;
        uint type = PInvoke.GetDriveType(id);
        var (number, system) = Device(id);
        Drive? drive = number is { } key && drives.TryGetValue(key, out var found) ? found : null;
        return Locked(id, paths[0], drive?.InstanceId, drive?.Name, type is DRIVE_REMOVABLE || drive is { Ejectable: true }, system, drive is { Ejectable: true });
    }

    // A locked volume lists no mount path: its drive letter serves no files until it is unlocked.
    internal static DesktopVolume Locked(string id, string root, string? driveId, string? driveName, bool removable, bool system, bool ejectable) =>
        new(id, driveName is { } name ? $"{name} ({root.TrimEnd('\\')})" : root)
        {
            DriveId = driveId,
            DriveName = driveName,
            IsRemovable = removable,
            IsSystem = system,
            IsLocked = true,
            IsEncrypted = true,
            UnlockMethod = DesktopVolumeUnlockMethod.SystemPrompt,
            CanEject = ejectable,
        };

    // GetVolumeInformationW's error for a locked volume, or the shell's protection state when the error differs.
    internal static bool IsLocked(int error, uint? protection) => error == LockedVolumeError || protection == ProtectionLocked;

    // On, encrypting, decrypting, suspended or locked: some of the volume is encrypted.
    internal static bool IsEncrypted(uint? protection) => protection is 1 or 3 or 4 or 5 or ProtectionLocked;

    // The shell's BitLocker state of a drive, for the native tests' record of what the runner reports.
    internal static uint? BitLockerProtectionForTest(string root)
    {
        int com = PInvoke.CoInitializeEx(null, global::Windows.Win32.System.Com.COINIT.COINIT_MULTITHREADED).Value;
        try { return BitLockerProtection(root); }
        finally { if (com >= 0) PInvoke.CoUninitialize(); }
    }

    private static PROPERTYKEY? s_protectionKey;

    // System.Volume.BitLockerProtection of a drive, which Explorer reads without an administrator; null when the shell
    // does not report it, such as for a volume BitLocker cannot protect.
    private static uint? BitLockerProtection(string root)
    {
        if (s_protectionKey is null)
        {
            PROPERTYKEY found;
            if (PInvoke.PSGetPropertyKeyFromName("System.Volume.BitLockerProtection", out found).Value < 0) return null;
            s_protectionKey = found;
        }
        PROPERTYKEY key = s_protectionKey.Value;
        Guid itemId = typeof(IShellItem2).GUID;
        void* created = null;
        fixed (char* path = root)
            if (PInvoke.SHCreateItemFromParsingName(path, null, &itemId, &created).Value < 0 || created is null) return null;
        var item = (IShellItem2*)created;
        try
        {
            uint value;
            return item->GetUInt32(&key, &value).Value >= 0 ? value : null;
        }
        finally { item->Release(); }
    }

    // Drive letters (E:\) and mounted folders (C:\Data\Disk\), in the order Windows reports them.
    private static ImmutableArray<string> MountPaths(string id)
    {
        char[] buffer = new char[MaxPath];
        while (true)
        {
            uint needed;
            fixed (char* names = buffer)
            {
                if (PInvoke.GetVolumePathNamesForVolumeName(id, new PZZWSTR(names), (uint)buffer.Length, out needed)) break;
            }
            if (Marshal.GetLastPInvokeError() != (int)WIN32_ERROR.ERROR_MORE_DATA || needed <= buffer.Length) return [];
            buffer = new char[needed];
        }
        return Strings(buffer);
    }

    // The disk (or optical drive) and whether the partition belongs to the system: the EFI system partition, the
    // Microsoft reserved or a recovery partition, or one the platform requires.
    private static ((uint Type, uint Number)? Number, bool System) Device(string id)
    {
        using var handle = Open(id.TrimEnd('\\'));
        if (handle.IsInvalid) return (null, false);
        (uint, uint)? number = null;
        STORAGE_DEVICE_NUMBER device;
        if (Control(handle, PInvoke.IOCTL_STORAGE_GET_DEVICE_NUMBER, &device, sizeof(STORAGE_DEVICE_NUMBER)))
            number = (device.DeviceType, device.DeviceNumber);
        else
        {
            // A dynamic or spanned volume has no single device number; its first extent names a disk.
            byte* extents = stackalloc byte[256];
            if (Control(handle, PInvoke.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, extents, 256) && ((VOLUME_DISK_EXTENTS*)extents)->NumberOfDiskExtents > 0)
                number = (FILE_DEVICE_DISK, ((VOLUME_DISK_EXTENTS*)extents)->Extents[0].DiskNumber);
        }
        bool system = false;
        PARTITION_INFORMATION_EX partition;
        if (Control(handle, PInvoke.IOCTL_DISK_GET_PARTITION_INFO_EX, &partition, sizeof(PARTITION_INFORMATION_EX)))
            system = partition.IsServicePartition || partition.PartitionStyle switch
            {
                PARTITION_STYLE.PARTITION_STYLE_GPT => partition.Gpt.PartitionType == EfiSystemPartition || partition.Gpt.PartitionType == MicrosoftReservedPartition
                    || partition.Gpt.PartitionType == RecoveryPartition || ((ulong)partition.Gpt.Attributes & 1) != 0, // GPT_ATTRIBUTE_PLATFORM_REQUIRED
                PARTITION_STYLE.PARTITION_STYLE_MBR => partition.Mbr.PartitionType is 0x27 or 0xEF, // recovery, EFI system
                _ => false,
            };
        return (number, system);
    }

    // Every present disk, optical and floppy drive, by the device number its volumes report.
    private static Dictionary<(uint Type, uint Number), Drive> Drives()
    {
        var drives = new Dictionary<(uint, uint), Drive>();
        foreach (var kind in new[] { PInvoke.GUID_DEVINTERFACE_DISK, PInvoke.GUID_DEVINTERFACE_CDROM, PInvoke.GUID_DEVINTERFACE_FLOPPY })
        {
            foreach (string path in Interfaces(kind))
            {
                using var handle = Open(path);
                if (handle.IsInvalid) continue;
                STORAGE_DEVICE_NUMBER device;
                if (!Control(handle, PInvoke.IOCTL_STORAGE_GET_DEVICE_NUMBER, &device, sizeof(STORAGE_DEVICE_NUMBER))) continue;
                if (InterfaceString(path, PInvoke.DEVPKEY_Device_InstanceId) is not { } instance || Locate(instance) is not { } node) continue;
                drives[(device.DeviceType, device.DeviceNumber)] = new Drive(instance, NodeString(node, PInvoke.DEVPKEY_Device_FriendlyName), EjectionTarget(node) is not null);
            }
        }
        return drives;
    }

    private static string[] Interfaces(Guid kind)
    {
        while (true)
        {
            if (PInvoke.CM_Get_Device_Interface_List_Size(out uint length, kind, default, CM_GET_DEVICE_INTERFACE_LIST_FLAGS.CM_GET_DEVICE_INTERFACE_LIST_PRESENT)
                != CONFIGRET.CR_SUCCESS || length <= 1) return [];
            char[] buffer = new char[length];
            CONFIGRET result;
            fixed (char* list = buffer)
                result = PInvoke.CM_Get_Device_Interface_List(kind, default, new PZZWSTR(list), length, CM_GET_DEVICE_INTERFACE_LIST_FLAGS.CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            // A device arrived between the two calls.
            if (result == CONFIGRET.CR_BUFFER_SMALL) continue;
            return result == CONFIGRET.CR_SUCCESS ? [.. Strings(buffer)] : [];
        }
    }

    // The device itself or the nearest ancestor that Windows can safely remove, such as the USB mass-storage device of
    // a USB stick's disk.
    private static uint? EjectionTarget(uint node)
    {
        for (int depth = 0; depth < MaxDepth; depth++)
        {
            uint capabilities = 0, length = sizeof(uint);
            if (PInvoke.CM_Get_DevNode_Registry_Property(node, PInvoke.CM_DRP_CAPABILITIES, null, &capabilities, &length, 0) == CONFIGRET.CR_SUCCESS
                && (capabilities & (uint)CM_DEVCAP.CM_DEVCAP_REMOVABLE) != 0) return node;
            if (PInvoke.CM_Get_Parent(out uint parent, node, 0) != CONFIGRET.CR_SUCCESS) return null;
            node = parent;
        }
        return null;
    }

    public PlatformResult<PlatformUnit> Eject(string volumeId, string? driveId)
    {
        uint? node = driveId is null ? null : Locate(driveId);
        if (node is { } device && EjectionTarget(device) is { } target) return RequestEject(target);
        // An internal optical drive is no removable device: open its tray instead.
        if (PInvoke.GetDriveType(volumeId) == DRIVE_CDROM) return EjectMedia(volumeId);
        return node is null ? new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.NotFound)
            : new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    }

    // Locks and dismounts the volume so that nothing writes to it, allows removal, then ejects the medium. The lock
    // fails while a program has a file open on the disc.
    private static PlatformResult<PlatformUnit> EjectMedia(string volumeId)
    {
        using var handle = PInvoke.CreateFile(volumeId.TrimEnd('\\'), 0x80000000 /* GENERIC_READ */,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, 0, null);
        if (handle.IsInvalid) return Failure<PlatformUnit>(Marshal.GetLastPInvokeError());
        if (!Control(handle, PInvoke.FSCTL_LOCK_VOLUME, null, 0))
        {
            int error = Marshal.GetLastPInvokeError();
            return error is (int)WIN32_ERROR.ERROR_ACCESS_DENIED or (int)WIN32_ERROR.ERROR_SHARING_VIOLATION or (int)WIN32_ERROR.ERROR_LOCK_VIOLATION
                ? new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.ResourceBusy, PlatformDiagnostic.FromWin32Error(error))
                : Failure<PlatformUnit>(error);
        }
        if (!Control(handle, PInvoke.FSCTL_DISMOUNT_VOLUME, null, 0)) return Failure<PlatformUnit>(Marshal.GetLastPInvokeError());
        var allow = new PREVENT_MEDIA_REMOVAL { PreventMediaRemoval = false };
        _ = ControlInput(handle, PInvoke.IOCTL_STORAGE_MEDIA_REMOVAL, &allow, sizeof(PREVENT_MEDIA_REMOVAL));
        if (!Control(handle, PInvoke.IOCTL_STORAGE_EJECT_MEDIA, null, 0)) return Failure<PlatformUnit>(Marshal.GetLastPInvokeError());
        return new PlatformResult<PlatformUnit>.Success(default);
    }

    // Opens Explorer's BitLocker prompt for the drive (its "unlock-bde" verb, bdeunlock.exe) and waits for it to close.
    public Task<PlatformResult<PlatformUnit>> PromptUnlockAsync(string volumeId, CancellationToken cancellationToken) =>
        MountPaths(volumeId) is [var root, ..] ? WindowsUnlockPrompt.ShowAsync(root, cancellationToken)
            : Task.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable));

    // The drive verb Explorer shows as "Unlock Drive..." on a locked BitLocker volume.
    internal const string UnlockVerb = "unlock-bde";

    private static PlatformResult<PlatformUnit> RequestEject(uint node)
    {
        // With a veto buffer Windows reports the veto instead of showing its own notification.
        Span<char> vetoName = stackalloc char[MaxPath];
        var result = PInvoke.CM_Request_Device_Eject(node, out PNP_VETO_TYPE veto, vetoName, 0);
        return EjectResult((uint)result, (int)veto);
    }

    // The request on the drive's own device node, removable or not: the native tests' virtual disk has no removable
    // device above it, and the Configuration Manager call is exercised on it this way.
    internal static PlatformResult<PlatformUnit> RequestEjectForTest(string driveId) =>
        Locate(driveId) is { } node ? RequestEject(node) : new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.NotFound);

    // CONFIGRET and PNP_VETO_TYPE to the contract's results. The veto names a device or process; it is not reported.
    internal static PlatformResult<PlatformUnit> EjectResult(uint result, int veto) => (result, veto) switch
    {
        (0, 0) => new PlatformResult<PlatformUnit>.Success(new PlatformUnit()),
        (_, 12) or (0x33, _) => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied, Veto(result, veto)), // InsufficientRights, CR_ACCESS_DENIED
        (_, 13) or (0x0D or 0x05, _) => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.NotFound, Veto(result, veto)), // AlreadyRemoved, no such devnode
        (_, 8 or 10 or 11) or (0x28, _) => new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable), // cannot be removed
        (_, >= 1 and <= 7) or (0x17, _) => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.ResourceBusy, Veto(result, veto)), // in use, CR_REMOVE_VETOED
        _ => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other, Veto(result, veto)),
    };

    private static PlatformDiagnostic Veto(uint result, int veto) => veto != 0 ? new("PNP_VETO_TYPE", veto) : new("CONFIGRET", result);

    public IDisposable? Subscribe(Action changed)
    {
        var context = GCHandle.Alloc(changed);
        var filter = new CM_NOTIFY_FILTER { cbSize = (uint)sizeof(CM_NOTIFY_FILTER), FilterType = CM_NOTIFY_FILTER_TYPE.CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE };
        filter.u.DeviceInterface.ClassGuid = PInvoke.GUID_DEVINTERFACE_VOLUME;
        HCMNOTIFICATION registration;
        if (PInvoke.CM_Register_Notification(&filter, (void*)GCHandle.ToIntPtr(context), &Notified, &registration) != CONFIGRET.CR_SUCCESS)
        {
            context.Free();
            return null;
        }
        return new Registration(registration, context);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Notified(HCMNOTIFICATION registration, void* context, CM_NOTIFY_ACTION action, CM_NOTIFY_EVENT_DATA* data, uint size)
    {
        try { ((Action)GCHandle.FromIntPtr((nint)context).Target!)(); }
        catch (Exception) { /* No exception may unwind into the Configuration Manager. */ }
        return 0;
    }

    private sealed class Registration(HCMNOTIFICATION registration, GCHandle context) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Waits for a callback in progress, so the context outlives every callback.
            PInvoke.CM_Unregister_Notification(registration);
            context.Free();
        }
    }

    private static SafeFileHandle Open(string path) =>
        // No access: device queries only, which neither mount the volume nor stand in the way of a lock or removal.
        PInvoke.CreateFile(path, 0, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE, null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING, 0, null);

    private static bool ControlInput(SafeFileHandle handle, uint code, void* input, int size)
    {
        bool added = false;
        handle.DangerousAddRef(ref added);
        try
        {
            uint returned;
            return PInvoke.DeviceIoControl((HANDLE)handle.DangerousGetHandle(), code, input, (uint)size, null, 0, &returned, null);
        }
        finally { if (added) handle.DangerousRelease(); }
    }

    private static bool Control(SafeFileHandle handle, uint code, void* output, int size)
    {
        bool added = false;
        handle.DangerousAddRef(ref added);
        try
        {
            uint returned;
            return PInvoke.DeviceIoControl((HANDLE)handle.DangerousGetHandle(), code, null, 0, output, (uint)size, &returned, null);
        }
        finally { if (added) handle.DangerousRelease(); }
    }

    private static uint? Locate(string instanceId)
    {
        uint node;
        CONFIGRET result;
        fixed (char* id = instanceId) result = PInvoke.CM_Locate_DevNode(&node, new PWSTR(id), CM_LOCATE_DEVNODE_FLAGS.CM_LOCATE_DEVNODE_NORMAL);
        return result == CONFIGRET.CR_SUCCESS ? node : null;
    }

    private static string? InterfaceString(string path, DEVPROPKEY key)
    {
        Span<byte> buffer = stackalloc byte[1024];
        uint size = (uint)buffer.Length;
        return PInvoke.CM_Get_Device_Interface_Property(path, key, out _, buffer, ref size, 0) == CONFIGRET.CR_SUCCESS ? Text(buffer[..(int)size]) : null;
    }

    private static string? NodeString(uint node, DEVPROPKEY key)
    {
        Span<byte> buffer = stackalloc byte[1024];
        uint size = (uint)buffer.Length;
        return PInvoke.CM_Get_DevNode_Property(node, key, out _, buffer, ref size, 0) == CONFIGRET.CR_SUCCESS ? Text(buffer[..(int)size]) : null;
    }

    private static string? Text(ReadOnlySpan<byte> bytes)
    {
        string text = Terminated(MemoryMarshal.Cast<byte, char>(bytes));
        return text.Length > 0 ? text : null;
    }

    private static string Terminated(ReadOnlySpan<char> text)
    {
        int end = text.IndexOf('\0');
        return new string(end < 0 ? text : text[..end]);
    }

    // A double-NUL-terminated list.
    internal static ImmutableArray<string> Strings(ReadOnlySpan<char> list)
    {
        var strings = ImmutableArray.CreateBuilder<string>();
        while (list.Length > 0 && list[0] != '\0')
        {
            int end = list.IndexOf('\0');
            if (end < 0) end = list.Length;
            strings.Add(new string(list[..end]));
            list = list[Math.Min(end + 1, list.Length)..];
        }
        return strings.ToImmutable();
    }

    private static PlatformResult<T> Failure<T>(int error) =>
        new PlatformResult<T>.Failed(error == (int)WIN32_ERROR.ERROR_ACCESS_DENIED ? PlatformFailureCode.PermissionDenied : PlatformFailureCode.Other,
            PlatformDiagnostic.FromWin32Error(error));
}

// The shell verb runs bdeunlock.exe through ShellExecuteEx; a verb that starts no process has finished when it returns.
[SupportedOSPlatform("windows8.0")]
internal static class WindowsUnlockPrompt
{
    internal static async Task<PlatformResult<PlatformUnit>> ShowAsync(string root, CancellationToken cancellationToken)
    {
        global::System.Diagnostics.Process? prompt;
        try { prompt = global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo(root) { UseShellExecute = true, Verb = WindowsVolumeSource.UnlockVerb }); }
        catch (global::System.ComponentModel.Win32Exception error)
        {
            return error.NativeErrorCode switch
            {
                1155 => new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable), // ERROR_NO_ASSOCIATION: no such verb
                1223 => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.UserDismissed), // ERROR_CANCELLED
                _ => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other, PlatformDiagnostic.FromWin32Error(error.NativeErrorCode)),
            };
        }
        if (prompt is not null)
            using (prompt) await prompt.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new PlatformResult<PlatformUnit>.Success(default);
    }
}
