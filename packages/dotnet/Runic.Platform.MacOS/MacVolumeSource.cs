using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Runic.Platform.MacOS;

// Disk Arbitration (DiskArbitration.framework) on a serial dispatch queue of its own: the appeared, disappeared and
// description-changed callbacks keep a copy of every disk's description, and the idle callback says when the first
// round of appearances is complete. Mount, unmount and eject complete on the same queue with an optional dissenter.
// Only C functions with function-pointer callbacks: no blocks, AppKit or Objective-C messages, so it is NativeAOT-safe.
[SupportedOSPlatform("macos")]
internal sealed unsafe partial class MacVolumeSource : IMacVolumeSource, IDisposable
{
    private const string DiskArbitration = "/System/Library/Frameworks/DiskArbitration.framework/DiskArbitration";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string System = "/usr/lib/libSystem.B.dylib";
    private static readonly TimeSpan FirstRound = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, MacDisk> _disks = new(StringComparer.Ordinal);
    private readonly List<Action> _subscribers = [];
    private readonly HashSet<nint> _operations = [];
    private readonly TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GCHandle _self;
    private nint _session, _queue;
    private bool _started, _disposed;

    public PlatformResult<ImmutableArray<MacDisk>> Disks()
    {
        if (!Start()) return new PlatformResult<ImmutableArray<MacDisk>>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        lock (_lock) return new PlatformResult<ImmutableArray<MacDisk>>.Success([.. _disks.Values]);
    }

    public IDisposable? Subscribe(Action changed)
    {
        if (!Start()) return null;
        lock (_lock) _subscribers.Add(changed);
        return new Subscription(this, changed);
    }

    private sealed class Subscription(MacVolumeSource source, Action changed) : IDisposable
    {
        public void Dispose() { lock (source._lock) source._subscribers.Remove(changed); }
    }

    // Creates the session on first use and waits for Disk Arbitration's first round of appearances.
    private bool Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started)
            {
                _started = true;
                _session = DASessionCreate(0);
                if (_session != 0)
                {
                    _self = GCHandle.Alloc(this);
                    nint context = GCHandle.ToIntPtr(_self);
                    DARegisterDiskAppearedCallback(_session, 0, &Appeared, context);
                    DARegisterDiskDisappearedCallback(_session, 0, &Disappeared, context);
                    DARegisterDiskDescriptionChangedCallback(_session, 0, 0, &DescriptionChanged, context);
                    DARegisterIdleCallback(_session, &Idle, context);
                    _queue = dispatch_queue_create("org.runic.volumes", 0);
                    DASessionSetDispatchQueue(_session, _queue);
                }
            }
            if (_session == 0) return false;
        }
        // A disk that never settles must not hold the listing forever; what has appeared so far is listed.
        _idle.Task.Wait(FirstRound);
        return true;
    }

    public Task<PlatformResult<string?>> MountAsync(string bsdName) =>
        Operate<string?>(bsdName, static (disk, callback, context) => DADiskMount(disk, 0, 0, callback, context), mount: true);

    public Task<PlatformResult<PlatformUnit>> UnmountAsync(string bsdName) =>
        Operate<PlatformUnit>(bsdName, static (disk, callback, context) => DADiskUnmount(disk, 0, callback, context), mount: false);

    public Task<PlatformResult<PlatformUnit>> EjectAsync(string bsdName) =>
        Operate<PlatformUnit>(bsdName, static (disk, callback, context) => DADiskEject(disk, 0, callback, context), mount: false);

    private sealed class Operation(MacVolumeSource source, Action<nint, int> complete)
    {
        internal readonly MacVolumeSource Source = source;
        internal readonly Action<nint, int> Complete = complete;
    }

    private Task<PlatformResult<T>> Operate<T>(string bsdName, Action<nint, nint, nint> start, bool mount)
    {
        var completion = new TaskCompletionSource<PlatformResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Start()) { completion.SetResult(new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable)); return completion.Task; }
        nint disk = DADiskCreateFromBSDName(0, _session, bsdName);
        if (disk == 0) { completion.SetResult(new PlatformResult<T>.Failed(PlatformFailureCode.NotFound)); return completion.Task; }
        GCHandle handle = default;
        handle = GCHandle.Alloc(new Operation(this, (completed, status) =>
        {
            lock (_lock) _operations.Remove(GCHandle.ToIntPtr(handle));
            if (status == 0)
            {
                object? value = mount ? MountPath(completed) : new PlatformUnit();
                completion.TrySetResult(new PlatformResult<T>.Success((T)value!));
            }
            else completion.TrySetResult(Result<T>(status, mount));
            handle.Free();
        }));
        nint context = GCHandle.ToIntPtr(handle);
        lock (_lock) _operations.Add(context);
        try { start(disk, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&Completed, context); }
        finally { CFRelease(disk); }
        return completion.Task;
    }

    // DAReturn values (DADissenter.h) and the errno values Disk Arbitration passes through unix_err().
    internal static PlatformResult<T> Result<T>(int status, bool mount)
    {
        var diagnostic = new PlatformDiagnostic("DAReturn", unchecked((uint)status));
        int errno = (status & ~0x3FFF) == 0xC000 ? status & 0x3FFF : 0;
        return (unchecked((uint)status), errno) switch
        {
            (0xF8DA0002 or 0xF8DA0004, _) or (_, 16) => new PlatformResult<T>.Failed(PlatformFailureCode.ResourceBusy, diagnostic), // Busy, ExclusiveAccess, EBUSY
            (0xF8DA0008 or 0xF8DA0009, _) or (_, 1 or 13) => new PlatformResult<T>.Failed(PlatformFailureCode.PermissionDenied, diagnostic), // NotPermitted, NotPrivileged, EPERM, EACCES
            (0xF8DA0006, _) or (_, 2 or 19) => new PlatformResult<T>.Failed(PlatformFailureCode.NotFound, diagnostic), // NotFound, ENOENT, ENODEV
            (0xF8DA000C, _) or (_, 45) => new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable), // Unsupported, ENOTSUP
            (0xF8DA0007, _) when mount => new PlatformResult<T>.Failed(PlatformFailureCode.Conflict, diagnostic), // NotMounted
            (0xF8DA0007, _) => new PlatformResult<T>.Success(default!), // NotMounted: an unmount or eject has nothing to do
            _ => new PlatformResult<T>.Failed(PlatformFailureCode.IoError, diagnostic),
        };
    }

    private static string? MountPath(nint disk)
    {
        nint description = DADiskCopyDescription(disk);
        if (description == 0) return null;
        try { return Url(CFDictionaryGetValue(description, Key("kDADiskDescriptionVolumePathKey"))); }
        finally { CFRelease(description); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Completed(nint disk, nint dissenter, nint context)
    {
        try
        {
            var operation = (Operation)GCHandle.FromIntPtr(context).Target!;
            operation.Complete(disk, dissenter == 0 ? 0 : DADissenterGetStatus(dissenter));
        }
        catch (Exception) { /* No exception may unwind into Disk Arbitration. */ }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Appeared(nint disk, nint context) => Update(disk, context, removed: false);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Disappeared(nint disk, nint context) => Update(disk, context, removed: true);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DescriptionChanged(nint disk, nint keys, nint context) => Update(disk, context, removed: false);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Idle(nint context)
    {
        try { ((MacVolumeSource)GCHandle.FromIntPtr(context).Target!)._idle.TrySetResult(); }
        catch (Exception) { /* No exception may unwind into Disk Arbitration. */ }
    }

    private static void Update(nint disk, nint context, bool removed)
    {
        try
        {
            var source = (MacVolumeSource)GCHandle.FromIntPtr(context).Target!;
            if (Marshal.PtrToStringUTF8(DADiskGetBSDName(disk)) is not { Length: > 0 } name) return;
            var described = removed ? null : Describe(disk, name);
            Action[] subscribers;
            lock (source._lock)
            {
                if (described is null) source._disks.Remove(name);
                else source._disks[name] = described;
                subscribers = [.. source._subscribers];
            }
            foreach (var subscriber in subscribers) subscriber();
        }
        catch (Exception) { /* No exception may unwind into Disk Arbitration. */ }
    }

    private static MacDisk? Describe(nint disk, string name)
    {
        nint description = DADiskCopyDescription(disk);
        if (description == 0) return null;
        try
        {
            nint Value(string key) => CFDictionaryGetValue(description, Key(key));
            string? path = Url(Value("kDADiskDescriptionVolumePathKey"));
            return new MacDisk(name)
            {
                VolumeKind = Text(Value("kDADiskDescriptionVolumeKindKey")),
                Mountable = Boolean(Value("kDADiskDescriptionVolumeMountableKey")) ?? false,
                VolumeName = Text(Value("kDADiskDescriptionVolumeNameKey")),
                VolumePath = path,
                Hidden = path is not null && Hidden(path),
                Content = Text(Value("kDADiskDescriptionMediaContentKey")),
                Size = Number(Value("kDADiskDescriptionMediaSizeKey")) is > 0 and var size ? size : null,
                Whole = Boolean(Value("kDADiskDescriptionMediaWholeKey")) ?? false,
                Removable = Boolean(Value("kDADiskDescriptionMediaRemovableKey")) ?? false,
                Ejectable = Boolean(Value("kDADiskDescriptionMediaEjectableKey")) ?? false,
                Writable = Boolean(Value("kDADiskDescriptionMediaWritableKey")) ?? true,
                Internal = Boolean(Value("kDADiskDescriptionDeviceInternalKey")) ?? false,
                Vendor = Text(Value("kDADiskDescriptionDeviceVendorKey"))?.Trim(),
                Model = Text(Value("kDADiskDescriptionDeviceModelKey"))?.Trim(),
                PhysicalDisk = PhysicalDisk(disk),
            };
        }
        finally { CFRelease(description); }
    }

    // The topmost whole IOMedia above the disk in the I/O Registry's service plane: for an APFS volume, the drive's
    // whole disk rather than the synthesized container.
    private static string? PhysicalDisk(nint disk)
    {
        uint entry = DADiskCopyIOMedia(disk);
        if (entry == 0) return null;
        string? found = null;
        for (int depth = 0; depth < 32 && entry != 0; depth++)
        {
            if (IOObjectConformsTo(entry, "IOMedia") != 0 && WholeMedia(entry) && BsdName(entry) is { } name) found = name;
            uint parent = 0;
            int result = IORegistryEntryGetParentEntry(entry, "IOService", &parent);
            _ = IOObjectRelease(entry);
            entry = result == 0 ? parent : 0;
        }
        if (entry != 0) _ = IOObjectRelease(entry);
        return found;
    }

    private static bool WholeMedia(uint entry)
    {
        nint value = Property(entry, "Whole");
        try { return Boolean(value) == true; }
        finally { if (value != 0) CFRelease(value); }
    }

    private static string? BsdName(uint entry)
    {
        nint value = Property(entry, "BSD Name");
        try { return Text(value); }
        finally { if (value != 0) CFRelease(value); }
    }

    // A new reference to the registry entry's property, or 0.
    private static nint Property(uint entry, string key)
    {
        nint name = CFStringCreateWithCString(0, key, 0x08000100 /* kCFStringEncodingUTF8 */);
        if (name == 0) return 0;
        try { return IORegistryEntryCreateCFProperty(entry, name, 0, 0); }
        finally { CFRelease(name); }
    }

    // statfs(2)'s f_flags has MNT_DONTBROWSE for a nobrowse mount. struct statfs (64-bit inodes): f_flags is at
    // offset 64 of 2168 bytes; Intel Macs export the 64-bit-inode variant as statfs$INODE64.
    private const uint MNT_DONTBROWSE = 0x00100000;
    private static readonly Lazy<nint> s_statfs = new(() => NativeLibrary.GetExport(NativeLibrary.Load(System),
        RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "statfs$INODE64" : "statfs"));

    private static bool Hidden(string path)
    {
        byte* buffer = stackalloc byte[2168];
        byte[] name = global::System.Text.Encoding.UTF8.GetBytes(path + "\0");
        fixed (byte* file = name)
            return ((delegate* unmanaged[Cdecl]<byte*, byte*, int>)s_statfs.Value)(file, buffer) == 0 && (*(uint*)(buffer + 64) & MNT_DONTBROWSE) != 0;
    }

    private static nint Key(string name) => *(nint*)NativeLibrary.GetExport(s_library.Value, name);
    private static readonly Lazy<nint> s_library = new(() => NativeLibrary.Load(DiskArbitration));

    private static string? Text(nint value)
    {
        if (value == 0 || CFGetTypeID(value) != CFStringGetTypeID()) return null;
        nint length = CFStringGetLength(value);
        if (length <= 0) return null;
        var buffer = new char[length];
        fixed (char* characters = buffer) CFStringGetCharacters(value, new CFRange(0, length), characters);
        return new string(buffer);
    }

    private static bool? Boolean(nint value) => value != 0 && CFGetTypeID(value) == CFBooleanGetTypeID() ? CFBooleanGetValue(value) != 0 : null;

    private static long? Number(nint value)
    {
        if (value == 0 || CFGetTypeID(value) != CFNumberGetTypeID()) return null;
        long number;
        return CFNumberGetValue(value, 4 /* kCFNumberSInt64Type */, &number) != 0 ? number : null;
    }

    private static string? Url(nint value)
    {
        if (value == 0 || CFGetTypeID(value) != CFURLGetTypeID()) return null;
        nint path = CFURLCopyFileSystemPath(value, 0 /* kCFURLPOSIXPathStyle */);
        if (path == 0) return null;
        try { return Text(path); }
        finally { CFRelease(path); }
    }

    public void Dispose()
    {
        nint[] pending;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            pending = [.. _operations];
            _operations.Clear();
        }
        if (_session != 0)
        {
            nint context = GCHandle.ToIntPtr(_self);
            DAUnregisterCallback(_session, (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Appeared, context);
            DAUnregisterCallback(_session, (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Disappeared, context);
            DAUnregisterCallback(_session, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&DescriptionChanged, context);
            DAUnregisterCallback(_session, (nint)(delegate* unmanaged[Cdecl]<nint, void>)&Idle, context);
            DASessionSetDispatchQueue(_session, 0);
            // A callback already running on the queue finishes before the session and this object's handle go.
            dispatch_sync_f(_queue, 0, &Drained);
            CFRelease(_session);
            dispatch_release(_queue);
            _self.Free();
        }
        // An operation Disk Arbitration will no longer complete answers that the service is gone.
        foreach (nint context in pending)
            ((Operation)GCHandle.FromIntPtr(context).Target!).Complete(0, unchecked((int)0xF8DA000C));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Drained(nint context) { }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CFRange(nint location, nint length)
    {
        private readonly nint _location = location, _length = length;
    }

    [LibraryImport(DiskArbitration)] private static partial nint DASessionCreate(nint allocator);
    [LibraryImport(DiskArbitration)] private static partial void DASessionSetDispatchQueue(nint session, nint queue);
    [LibraryImport(DiskArbitration)] private static partial void DARegisterDiskAppearedCallback(nint session, nint match, delegate* unmanaged[Cdecl]<nint, nint, void> callback, nint context);
    [LibraryImport(DiskArbitration)] private static partial void DARegisterDiskDisappearedCallback(nint session, nint match, delegate* unmanaged[Cdecl]<nint, nint, void> callback, nint context);
    [LibraryImport(DiskArbitration)] private static partial void DARegisterDiskDescriptionChangedCallback(nint session, nint match, nint watch, delegate* unmanaged[Cdecl]<nint, nint, nint, void> callback, nint context);
    [LibraryImport(DiskArbitration)] private static partial void DARegisterIdleCallback(nint session, delegate* unmanaged[Cdecl]<nint, void> callback, nint context);
    [LibraryImport(DiskArbitration)] private static partial void DAUnregisterCallback(nint session, nint callback, nint context);
    [LibraryImport(DiskArbitration, StringMarshalling = StringMarshalling.Utf8)] private static partial nint DADiskCreateFromBSDName(nint allocator, nint session, string name);
    [LibraryImport(DiskArbitration)] private static partial nint DADiskGetBSDName(nint disk);
    [LibraryImport(DiskArbitration)] private static partial nint DADiskCopyDescription(nint disk);
    [LibraryImport(DiskArbitration)] private static partial uint DADiskCopyIOMedia(nint disk);
    [LibraryImport(DiskArbitration)] private static partial void DADiskMount(nint disk, nint path, uint options, nint callback, nint context);
    [LibraryImport(DiskArbitration)] private static partial void DADiskUnmount(nint disk, uint options, nint callback, nint context);
    [LibraryImport(DiskArbitration)] private static partial void DADiskEject(nint disk, uint options, nint callback, nint context);
    [LibraryImport(DiskArbitration)] private static partial int DADissenterGetStatus(nint dissenter);
    [LibraryImport(CoreFoundation)] private static partial void CFRelease(nint value);
    [LibraryImport(CoreFoundation)] private static partial nuint CFGetTypeID(nint value);
    [LibraryImport(CoreFoundation)] private static partial nuint CFStringGetTypeID();
    [LibraryImport(CoreFoundation)] private static partial nuint CFBooleanGetTypeID();
    [LibraryImport(CoreFoundation)] private static partial nuint CFNumberGetTypeID();
    [LibraryImport(CoreFoundation)] private static partial nuint CFURLGetTypeID();
    [LibraryImport(CoreFoundation)] private static partial nint CFDictionaryGetValue(nint dictionary, nint key);
    [LibraryImport(CoreFoundation)] private static partial nint CFStringGetLength(nint value);
    [LibraryImport(CoreFoundation)] private static partial void CFStringGetCharacters(nint value, CFRange range, char* buffer);
    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)] private static partial nint CFStringCreateWithCString(nint allocator, string value, uint encoding);
    [LibraryImport(CoreFoundation)] private static partial byte CFBooleanGetValue(nint value);
    [LibraryImport(CoreFoundation)] private static partial byte CFNumberGetValue(nint value, nint type, void* result);
    [LibraryImport(CoreFoundation)] private static partial nint CFURLCopyFileSystemPath(nint url, nint style);
    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)] private static partial int IOObjectConformsTo(uint entry, string className);
    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)] private static partial int IORegistryEntryGetParentEntry(uint entry, string plane, uint* parent);
    [LibraryImport(IOKit)] private static partial nint IORegistryEntryCreateCFProperty(uint entry, nint key, nint allocator, uint options);
    [LibraryImport(IOKit)] private static partial int IOObjectRelease(uint entry);
    [LibraryImport(System, StringMarshalling = StringMarshalling.Utf8)] private static partial nint dispatch_queue_create(string label, nint attributes);
    [LibraryImport(System)] private static partial void dispatch_sync_f(nint queue, nint context, delegate* unmanaged[Cdecl]<nint, void> work);
    [LibraryImport(System)] private static partial void dispatch_release(nint queue);
}
