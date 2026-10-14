using System.Runtime.InteropServices;
using System.Text;

namespace Runic.Platform.MacOS;

// The C XPC API (libxpc in libSystem) and libdispatch. XPC objects are retained C objects; every object this code
// creates or copies is released with Release. Event and reply handlers are blocks from MacDesktopBlock.
internal static unsafe partial class MacXpc
{
    private const string System = "/usr/lib/libSystem.B.dylib";
    internal const ulong MachServiceListener = 1 << 0, MachServicePrivileged = 1 << 1;
    private static readonly nuint ArrayAppend = nuint.MaxValue;

    private static readonly Lazy<nint> Library = new(() => NativeLibrary.Load(System));
    private static nint Export(string name) => NativeLibrary.TryGetExport(Library.Value, name, out var address) ? address : 0;
    internal static nint TypeDictionary => Export("_xpc_type_dictionary");
    internal static nint TypeError => Export("_xpc_type_error");
    internal static nint TypeConnection => Export("_xpc_type_connection");
    internal static nint TypeArray => Export("_xpc_type_array");
    internal static nint ErrorConnectionInvalid => Export("_xpc_error_connection_invalid");
    internal static nint ErrorConnectionInterrupted => Export("_xpc_error_connection_interrupted");
    // macOS 12 and later: the peer did not satisfy xpc_connection_set_peer_code_signing_requirement.
    internal static nint ErrorPeerCodeSigningRequirement => Export("_xpc_error_peer_code_signing_requirement");

    [LibraryImport(System, EntryPoint = "xpc_connection_create_mach_service", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint CreateMachService(string name, nint queue, ulong flags);
    [LibraryImport(System, EntryPoint = "xpc_connection_create")] internal static partial nint CreateConnection(nint name, nint queue);
    [LibraryImport(System, EntryPoint = "xpc_endpoint_create")] internal static partial nint CreateEndpoint(nint connection);
    [LibraryImport(System, EntryPoint = "xpc_connection_create_from_endpoint")] internal static partial nint CreateFromEndpoint(nint endpoint);
    [LibraryImport(System, EntryPoint = "xpc_connection_set_event_handler")] internal static partial void SetEventHandler(nint connection, nint handler);
    [LibraryImport(System, EntryPoint = "xpc_connection_set_target_queue")] internal static partial void SetTargetQueue(nint connection, nint queue);
    [LibraryImport(System, EntryPoint = "xpc_connection_resume")] internal static partial void Resume(nint connection);
    [LibraryImport(System, EntryPoint = "xpc_connection_cancel")] internal static partial void Cancel(nint connection);
    [LibraryImport(System, EntryPoint = "xpc_connection_send_message")] internal static partial void Send(nint connection, nint message);
    [LibraryImport(System, EntryPoint = "xpc_connection_send_message_with_reply")] internal static partial void SendWithReply(nint connection, nint message, nint queue, nint handler);
    [LibraryImport(System, EntryPoint = "xpc_connection_send_message_with_reply_sync")] internal static partial nint SendWithReplySync(nint connection, nint message);
    [LibraryImport(System, EntryPoint = "xpc_connection_set_peer_code_signing_requirement", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int SetPeerCodeSigningRequirement(nint connection, string requirement);
    [LibraryImport(System, EntryPoint = "xpc_connection_get_euid")] internal static partial uint GetEffectiveUserId(nint connection);
    [LibraryImport(System, EntryPoint = "xpc_connection_get_pid")] internal static partial int GetProcessId(nint connection);
    [LibraryImport(System, EntryPoint = "xpc_dictionary_create")] internal static partial nint CreateDictionary(nint keys, nint values, nuint count);
    [LibraryImport(System, EntryPoint = "xpc_dictionary_create_reply")] internal static partial nint CreateReply(nint original);
    [LibraryImport(System, EntryPoint = "xpc_dictionary_set_string", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void SetString(nint dictionary, string key, string value);
    [LibraryImport(System, EntryPoint = "xpc_dictionary_get_string", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetStringPointer(nint dictionary, string key);
    [LibraryImport(System, EntryPoint = "xpc_dictionary_set_int64", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void SetInt64(nint dictionary, string key, long value);
    [LibraryImport(System, EntryPoint = "xpc_dictionary_get_int64", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long GetInt64(nint dictionary, string key);
    [LibraryImport(System, EntryPoint = "xpc_dictionary_set_data", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void SetDataCore(nint dictionary, string key, byte* bytes, nuint length);
    [LibraryImport(System, EntryPoint = "xpc_dictionary_get_data", StringMarshalling = StringMarshalling.Utf8)]
    private static partial byte* GetDataCore(nint dictionary, string key, out nuint length);
    [LibraryImport(System, EntryPoint = "xpc_dictionary_set_value", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void SetValue(nint dictionary, string key, nint value);
    [LibraryImport(System, EntryPoint = "xpc_dictionary_get_value", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetValue(nint dictionary, string key);
    [LibraryImport(System, EntryPoint = "xpc_array_create")] internal static partial nint CreateArray(nint values, nuint count);
    [LibraryImport(System, EntryPoint = "xpc_array_set_string", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void ArraySetString(nint array, nuint index, string value);
    [LibraryImport(System, EntryPoint = "xpc_array_get_count")] internal static partial nuint ArrayCount(nint array);
    [LibraryImport(System, EntryPoint = "xpc_array_get_string")] private static partial nint ArrayGetStringPointer(nint array, nuint index);
    [LibraryImport(System, EntryPoint = "xpc_get_type")] internal static partial nint GetType(nint value);
    [LibraryImport(System, EntryPoint = "xpc_retain")] internal static partial nint Retain(nint value);
    [LibraryImport(System, EntryPoint = "xpc_release")] internal static partial void Release(nint value);
    [LibraryImport(System, EntryPoint = "dispatch_queue_create", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint CreateQueue(string label, nint attributes);
    [LibraryImport(System, EntryPoint = "dispatch_release")] internal static partial void ReleaseQueue(nint queue);

    internal static bool IsDictionary(nint value) => value != 0 && GetType(value) == TypeDictionary;
    internal static bool IsError(nint value) => value != 0 && GetType(value) == TypeError;

    internal static string? GetString(nint dictionary, string key) =>
        GetStringPointer(dictionary, key) is var pointer and not 0 ? Marshal.PtrToStringUTF8(pointer) : null;

    internal static void SetData(nint dictionary, string key, ReadOnlySpan<byte> data)
    {
        fixed (byte* bytes = data) SetDataCore(dictionary, key, bytes, (nuint)data.Length);
    }

    internal static byte[]? GetData(nint dictionary, string key)
    {
        var bytes = GetDataCore(dictionary, key, out var length);
        return bytes is null ? null : new ReadOnlySpan<byte>(bytes, checked((int)length)).ToArray();
    }

    internal static nint CreateStringArray(IEnumerable<string> values)
    {
        var array = CreateArray(0, 0);
        foreach (var value in values) ArraySetString(array, ArrayAppend, value);
        return array;
    }

    /// <summary>The strings of an XPC array, or null when the value is not an array of at most <paramref name="limit"/> strings.</summary>
    internal static List<string>? GetStringArray(nint value, int limit)
    {
        if (value == 0 || GetType(value) != TypeArray) return null;
        var count = ArrayCount(value);
        if (count > (nuint)limit) return null;
        var result = new List<string>((int)count);
        for (nuint i = 0; i < count; i++)
        {
            if (ArrayGetStringPointer(value, i) is var pointer and not 0 && Marshal.PtrToStringUTF8(pointer) is { } text) result.Add(text);
            else return null;
        }
        return result;
    }
}

// Security.framework: Authorization Services and code signing. CoreFoundation for the dictionaries they use.
internal static unsafe partial class MacSecurity
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8Encoding = 0x08000100;
    private const nint NumberSInt64 = 4;

    // AuthorizationFlags.
    internal const int FlagInteractionAllowed = 1 << 0, FlagExtendRights = 1 << 1, FlagDestroyRights = 1 << 3;
    internal const int ExternalFormLength = 32;
    // SecCSFlags.
    internal const uint CheckAllArchitectures = 1 << 0, CheckNestedCode = 1 << 3, StrictValidate = 1 << 4;

    [StructLayout(LayoutKind.Sequential)] internal struct AuthorizationItem { internal nint Name; internal nuint ValueLength; internal nint Value; internal uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct AuthorizationItemSet { internal uint Count; internal AuthorizationItem* Items; }

    [LibraryImport(Security, EntryPoint = "AuthorizationCreate")]
    internal static partial int AuthorizationCreate(AuthorizationItemSet* rights, AuthorizationItemSet* environment, int flags, out nint authorization);
    [LibraryImport(Security, EntryPoint = "AuthorizationCopyRights")]
    internal static partial int AuthorizationCopyRights(nint authorization, AuthorizationItemSet* rights, AuthorizationItemSet* environment, int flags, nint authorizedRights);
    [LibraryImport(Security, EntryPoint = "AuthorizationMakeExternalForm")]
    internal static partial int AuthorizationMakeExternalForm(nint authorization, byte* externalForm);
    [LibraryImport(Security, EntryPoint = "AuthorizationCreateFromExternalForm")]
    internal static partial int AuthorizationCreateFromExternalForm(byte* externalForm, out nint authorization);
    [LibraryImport(Security, EntryPoint = "AuthorizationFree")] internal static partial int AuthorizationFree(nint authorization, int flags);
    [LibraryImport(Security, EntryPoint = "AuthorizationRightGet", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int AuthorizationRightGet(string name, out nint definition);
    [LibraryImport(Security, EntryPoint = "AuthorizationRightSet", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int AuthorizationRightSet(nint authorization, string name, nint definition, nint descriptionKey, nint bundle, nint table);
    [LibraryImport(Security, EntryPoint = "AuthorizationRightRemove", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int AuthorizationRightRemove(nint authorization, string name);

    [LibraryImport(Security, EntryPoint = "SecCodeCopySelf")] internal static partial int CodeCopySelf(uint flags, out nint code);
    [LibraryImport(Security, EntryPoint = "SecRequirementCreateWithString")] internal static partial int RequirementCreate(nint text, uint flags, out nint requirement);
    [LibraryImport(Security, EntryPoint = "SecCodeCheckValidity")] internal static partial int CodeCheckValidity(nint code, uint flags, nint requirement);
    [LibraryImport(Security, EntryPoint = "SecStaticCodeCreateWithPath")] internal static partial int StaticCodeCreate(nint url, uint flags, out nint code);
    [LibraryImport(Security, EntryPoint = "SecStaticCodeCheckValidity")] internal static partial int StaticCodeCheckValidity(nint code, uint flags, nint requirement);
    [LibraryImport(Security, EntryPoint = "SecCodeCopyGuestWithAttributes")] internal static partial int CodeCopyGuest(nint host, nint attributes, uint flags, out nint guest);
    [LibraryImport(Security, EntryPoint = "SecCodeCreateWithXPCMessage")] internal static partial int CodeCreateWithXpcMessage(nint message, uint flags, out nint code);
    [LibraryImport(Security, EntryPoint = "SecCodeCopyDesignatedRequirement")] internal static partial int CodeCopyDesignatedRequirement(nint code, uint flags, out nint requirement);
    [LibraryImport(Security, EntryPoint = "SecRequirementCopyString")] internal static partial int RequirementCopyString(nint requirement, uint flags, out nint text);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRelease")] internal static partial void Release(nint value);
    [LibraryImport(CoreFoundation, EntryPoint = "CFRetain")] internal static partial nint Retain(nint value);
    [LibraryImport(CoreFoundation, EntryPoint = "CFStringCreateWithCString", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CreateStringCore(nint allocator, string text, uint encoding);
    [LibraryImport(CoreFoundation, EntryPoint = "CFStringGetLength")] private static partial nint StringLength(nint text);
    [LibraryImport(CoreFoundation, EntryPoint = "CFStringGetCString")] private static partial byte StringGetCString(nint text, byte* buffer, nint size, uint encoding);
    [LibraryImport(CoreFoundation, EntryPoint = "CFDictionaryCreateMutable")] private static partial nint CreateMutableDictionary(nint allocator, nint capacity, nint keyCallbacks, nint valueCallbacks);
    [LibraryImport(CoreFoundation, EntryPoint = "CFDictionarySetValue")] internal static partial void DictionarySet(nint dictionary, nint key, nint value);
    [LibraryImport(CoreFoundation, EntryPoint = "CFDictionaryGetValue")] internal static partial nint DictionaryGet(nint dictionary, nint key);
    [LibraryImport(CoreFoundation, EntryPoint = "CFNumberCreate")] private static partial nint NumberCreate(nint allocator, nint type, long* value);
    [LibraryImport(CoreFoundation, EntryPoint = "CFNumberGetValue")] private static partial byte NumberGetValue(nint number, nint type, long* value);
    [LibraryImport(CoreFoundation, EntryPoint = "CFGetTypeID")] private static partial nuint GetTypeId(nint value);
    [LibraryImport(CoreFoundation, EntryPoint = "CFStringGetTypeID")] private static partial nuint StringTypeId();
    [LibraryImport(CoreFoundation, EntryPoint = "CFNumberGetTypeID")] private static partial nuint NumberTypeId();
    [LibraryImport(CoreFoundation, EntryPoint = "CFBooleanGetTypeID")] private static partial nuint BooleanTypeId();
    [LibraryImport(CoreFoundation, EntryPoint = "CFDictionaryGetTypeID")] private static partial nuint DictionaryTypeId();
    [LibraryImport(CoreFoundation, EntryPoint = "CFURLCreateFromFileSystemRepresentation")]
    private static partial nint CreateUrl(nint allocator, byte* path, nint length, byte isDirectory);

    private static readonly Lazy<nint> CoreFoundationLibrary = new(() => NativeLibrary.Load(CoreFoundation));
    private static readonly Lazy<nint> SecurityLibrary = new(() => NativeLibrary.Load(Security));
    // Exported constants: the address of a variable that holds the object pointer, or of a callback structure.
    private static nint Constant(Lazy<nint> library, string name) => *(nint*)NativeLibrary.GetExport(library.Value, name);
    internal static nint True => Constant(CoreFoundationLibrary, "kCFBooleanTrue");
    internal static nint False => Constant(CoreFoundationLibrary, "kCFBooleanFalse");
    internal static nint GuestAttributePid => Constant(SecurityLibrary, "kSecGuestAttributePid");

    internal static bool IsAvailable => OperatingSystem.IsMacOS() && NativeLibrary.TryLoad(Security, out _);

    internal static nint CreateString(string text) => CreateStringCore(0, text, Utf8Encoding);

    internal static nint CreateDictionary() => CreateMutableDictionary(0, 0,
        NativeLibrary.GetExport(CoreFoundationLibrary.Value, "kCFTypeDictionaryKeyCallBacks"),
        NativeLibrary.GetExport(CoreFoundationLibrary.Value, "kCFTypeDictionaryValueCallBacks"));

    internal static nint CreateNumber(long value) => NumberCreate(0, NumberSInt64, &value);

    internal static nint CreateFileUrl(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        fixed (byte* pointer = bytes) return CreateUrl(0, pointer, bytes.Length, 0);
    }

    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/>, releasing both references this code created.</summary>
    internal static void Set(nint dictionary, string key, nint value)
    {
        var cfKey = CreateString(key);
        DictionarySet(dictionary, cfKey, value);
        Release(cfKey);
        Release(value);
    }

    internal static nint Get(nint dictionary, string key)
    {
        var cfKey = CreateString(key);
        try { return DictionaryGet(dictionary, cfKey); }
        finally { Release(cfKey); }
    }

    internal static string? ToText(nint value)
    {
        if (value == 0 || GetTypeId(value) != StringTypeId()) return null;
        var size = StringLength(value) * 4 + 1;
        var buffer = new byte[size];
        fixed (byte* pointer = buffer)
            return StringGetCString(value, pointer, size, Utf8Encoding) != 0 ? Marshal.PtrToStringUTF8((nint)pointer) : null;
    }

    internal static long? ToNumber(nint value)
    {
        if (value == 0 || GetTypeId(value) != NumberTypeId()) return null;
        long result;
        return NumberGetValue(value, NumberSInt64, &result) != 0 ? result : null;
    }

    internal static bool? ToBoolean(nint value) =>
        value == 0 || GetTypeId(value) != BooleanTypeId() ? null : value == True;

    internal static bool IsDictionary(nint value) => value != 0 && GetTypeId(value) == DictionaryTypeId();

    /// <summary>Creates a requirement from its text, or returns 0.</summary>
    internal static nint CreateRequirement(string text)
    {
        var cfText = CreateString(text);
        try { return RequirementCreate(cfText, 0, out var requirement) == 0 ? requirement : 0; }
        finally { Release(cfText); }
    }

    /// <summary>The designated requirement of this process's code, as text; null when it has none (unsigned).</summary>
    internal static string? CopySelfDesignatedRequirement()
    {
        if (CodeCopySelf(0, out var code) != 0) return null;
        try
        {
            if (CodeCopyDesignatedRequirement(code, 0, out var requirement) != 0) return null;
            try
            {
                if (RequirementCopyString(requirement, 0, out var text) != 0) return null;
                try { return ToText(text); }
                finally { Release(text); }
            }
            finally { Release(requirement); }
        }
        finally { Release(code); }
    }

    /// <summary>Whether this process's running code satisfies <paramref name="requirementText"/>.</summary>
    internal static bool SelfSatisfies(string requirementText)
    {
        var requirement = CreateRequirement(requirementText);
        if (requirement == 0) return false;
        try
        {
            if (CodeCopySelf(0, out var code) != 0) return false;
            try { return CodeCheckValidity(code, 0, requirement) == 0; }
            finally { Release(code); }
        }
        finally { Release(requirement); }
    }

    /// <summary>Whether the sender of an XPC message, identified by the message's audit token, satisfies the requirement.</summary>
    internal static bool SenderSatisfies(nint message, string requirementText)
    {
        var requirement = CreateRequirement(requirementText);
        if (requirement == 0) return false;
        try
        {
            if (CodeCreateWithXpcMessage(message, 0, out var code) != 0) return false;
            try { return CodeCheckValidity(code, 0, requirement) == 0; }
            finally { Release(code); }
        }
        finally { Release(requirement); }
    }

    /// <summary>The OSStatus of checking the file at <paramref name="path"/> against the requirement on disk.</summary>
    internal static int CheckFile(string path, string requirementText)
    {
        var requirement = CreateRequirement(requirementText);
        if (requirement == 0) return -67052; // errSecCSReqInvalid
        var url = CreateFileUrl(path);
        try
        {
            var status = StaticCodeCreate(url, 0, out var code);
            if (status != 0) return status;
            try { return StaticCodeCheckValidity(code, CheckAllArchitectures | StrictValidate | CheckNestedCode, requirement); }
            finally { Release(code); }
        }
        finally
        {
            Release(url);
            Release(requirement);
        }
    }

    /// <summary>The OSStatus of checking the running process <paramref name="pid"/>, a child that has not been reaped.</summary>
    internal static int CheckProcess(int pid, string requirementText)
    {
        var requirement = CreateRequirement(requirementText);
        if (requirement == 0) return -67052;
        var attributes = CreateDictionary();
        try
        {
            var number = CreateNumber(pid);
            DictionarySet(attributes, GuestAttributePid, number);
            Release(number);
            var status = CodeCopyGuest(0, attributes, 0, out var code);
            if (status != 0) return status;
            try { return CodeCheckValidity(code, 0, requirement); }
            finally { Release(code); }
        }
        finally
        {
            Release(attributes);
            Release(requirement);
        }
    }

    /// <summary>A copy of a right's definition from the authorization database, or 0 when it has none.</summary>
    internal static nint CopyRight(string name) => AuthorizationRightGet(name, out var definition) == 0 ? definition : 0;
}

// posix_spawn and the process calls the daemon needs. The opaque spawn types are pointers on Darwin.
internal static unsafe partial class MacProcess
{
    private const string System = "/usr/lib/libSystem.B.dylib";
    internal const short SpawnSetSignalDefault = 0x04, SpawnSetSignalMask = 0x08, SpawnStartSuspended = 0x80, SpawnCloseOnExecDefault = 0x4000;
    internal const int SigKill = 9, SigContinue = 19, Interrupted = 4;

    [LibraryImport(System, EntryPoint = "posix_spawn", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Spawn(out int pid, string path, nint* fileActions, nint* attributes, nint* argv, nint* environment);
    [LibraryImport(System, EntryPoint = "posix_spawn_file_actions_init")] internal static partial int FileActionsInit(nint* actions);
    [LibraryImport(System, EntryPoint = "posix_spawn_file_actions_destroy")] internal static partial int FileActionsDestroy(nint* actions);
    [LibraryImport(System, EntryPoint = "posix_spawn_file_actions_adddup2")] internal static partial int FileActionsDup2(nint* actions, int descriptor, int target);
    [LibraryImport(System, EntryPoint = "posix_spawn_file_actions_addchdir_np", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int FileActionsChdir(nint* actions, string path);
    [LibraryImport(System, EntryPoint = "posix_spawnattr_init")] internal static partial int AttributesInit(nint* attributes);
    [LibraryImport(System, EntryPoint = "posix_spawnattr_destroy")] internal static partial int AttributesDestroy(nint* attributes);
    [LibraryImport(System, EntryPoint = "posix_spawnattr_setflags")] internal static partial int AttributesSetFlags(nint* attributes, short flags);
    [LibraryImport(System, EntryPoint = "posix_spawnattr_setsigmask")] internal static partial int AttributesSetSignalMask(nint* attributes, uint* mask);
    [LibraryImport(System, EntryPoint = "posix_spawnattr_setsigdefault")] internal static partial int AttributesSetSignalDefault(nint* attributes, uint* signals);
    [LibraryImport(System, EntryPoint = "pipe", SetLastError = true)] internal static partial int Pipe(int* descriptors);
    [LibraryImport(System, EntryPoint = "close")] internal static partial int Close(int descriptor);
    [LibraryImport(System, EntryPoint = "fcntl")] internal static partial int Fcntl(int descriptor, int command, int argument);
    [LibraryImport(System, EntryPoint = "waitpid", SetLastError = true)] internal static partial int WaitPid(int pid, int* status, int options);
    [LibraryImport(System, EntryPoint = "kill")] internal static partial int Kill(int pid, int signal);
    [LibraryImport(System, EntryPoint = "geteuid")] internal static partial uint GetEffectiveUserId();
    [LibraryImport(System, EntryPoint = "getppid")] internal static partial int GetParentProcessId();
    [LibraryImport(System, EntryPoint = "umask")] internal static partial ushort Umask(ushort mask);
    // struct stat with 64-bit inodes; arm64 exports it as lstat, x86_64 as lstat$INODE64.
    [LibraryImport(System, EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LstatArm64(string path, byte* buffer);
    [LibraryImport(System, EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LstatX64(string path, byte* buffer);

    internal readonly record struct FileStatus(ushort Mode, uint Owner, uint Group);

    /// <summary>The status of <paramref name="path"/> without following a final symbolic link; null with the errno when it fails.</summary>
    internal static FileStatus? Lstat(string path, out int error)
    {
        var buffer = stackalloc byte[256]; // struct stat is 144 bytes
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? LstatX64(path, buffer) : LstatArm64(path, buffer);
        error = result == 0 ? 0 : Marshal.GetLastPInvokeError();
        // st_dev (4), st_mode (2), st_nlink (2), st_ino (8), st_uid (4), st_gid (4).
        return result == 0 ? new FileStatus(*(ushort*)(buffer + 4), *(uint*)(buffer + 16), *(uint*)(buffer + 20)) : null;
    }
}
