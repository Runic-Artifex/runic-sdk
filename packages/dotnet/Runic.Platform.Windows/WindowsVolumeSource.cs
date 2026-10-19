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
    private const uint DRIVE_REMOVABLE = 2, DRIVE_CDROM = 5;
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

    // A volume with a filesystem Windows recognizes; one without media, unformatted (RAW) or locked by BitLocker is
    // left out because GetVolumeInformationW fails for it.
    private static DesktopVolume? Volume(string id, Dictionary<(uint Type, uint Number), Drive> drives)
    {
        Span<char> label = stackalloc char[MaxPath];
        Span<char> fileSystem = stackalloc char[MaxPath];
        if (!PInvoke.GetVolumeInformation(id, label, out _, out _, out uint flags, fileSystem)) return null;
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
            CanEject = drive is { Ejectable: true },
        };
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

    public PlatformResult<PlatformUnit> Eject(string driveId)
    {
        if (Locate(driveId) is not { } node) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.NotFound);
        if (EjectionTarget(node) is not { } target) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        return RequestEject(target);
    }

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
        _ => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IoError, Veto(result, veto)),
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
        new PlatformResult<T>.Failed(error == (int)WIN32_ERROR.ERROR_ACCESS_DENIED ? PlatformFailureCode.PermissionDenied : PlatformFailureCode.IoError,
            PlatformDiagnostic.FromWin32Error(error));
}
