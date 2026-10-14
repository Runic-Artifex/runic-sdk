using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Runic.Platform.Windows.Elevation;

/// <summary>The Win32 functions of both sides of the elevation.</summary>
internal static unsafe partial class ElevationNative
{
    public const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    public const uint OpenExisting = 3;
    public const uint FileFlagOverlapped = 0x40000000, FileFlagFirstPipeInstance = 0x00080000;
    public const uint PipeAccessDuplex = 0x3, PipeRejectRemoteClients = 0x8;
    // SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION (SecurityIdentification << 16): the pipe's server may learn the
    // client's identity but never act as the elevated client.
    public const uint SecuritySqosPresent = 0x00100000, SecurityIdentification = 0x00010000;
    public const int ErrorPipeBusy = 231;
    public const uint InvalidFileAttributes = 0xFFFFFFFF, FileAttributeDirectory = 0x10, FileAttributeReparsePoint = 0x400;
    public const uint DriveFixed = 3;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint TokenQuery = 0x8;
    public const int TokenUser = 1, TokenElevation = 20;
    public const int StdInput = -10, StdOutput = -11, StdError = -12;
    public const uint HandleFlagInherit = 0x1;
    public const uint LoadLibrarySearchDefaultDirs = 0x1000;
    public const int SeFileObject = 1;
    public const uint OwnerSecurityInformation = 0x1, DaclSecurityInformation = 0x4;
    public const uint SeeMaskNoCloseProcess = 0x40, SeeMaskNoAsync = 0x100, SeeMaskFlagNoUi = 0x400;
    public const int SwHide = 0;
    public const uint CoinitApartmentThreaded = 0x2, CoinitDisableOle1Dde = 0x4;
    public static readonly nint CurrentProcess = -1;

    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    /// <summary><c>SHELLEXECUTEINFOW</c>; natural alignment gives the Win32 layout on every architecture.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ShellExecuteInfo
    {
        public uint Size;
        public uint Mask;
        public nint Window;
        public char* Verb;
        public char* File;
        public char* Parameters;
        public char* Directory;
        public int Show;
        public nint InstanceApplication;
        public nint IdList;
        public char* Class;
        public nint ClassKey;
        public uint HotKey;
        public nint IconOrMonitor;
        public nint Process;
    }

    /// <summary><c>PROCESS_BASIC_INFORMATION</c>: every field is pointer-sized or padded to it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, SecurityAttributes* attributes);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateFile(string name, uint access, uint share, SecurityAttributes* attributes, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WaitNamedPipe(string name, uint timeout);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeClientProcessId(SafeHandle pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeServerSessionId(SafeHandle pipe, out uint sessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentProcessId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetProcessId(SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial uint GetFileAttributes(string path);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetDriveType(string root);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial uint QueryDosDevice(string device, char* target, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemWindowsDirectoryW", SetLastError = true)]
    public static partial uint GetSystemWindowsDirectory(char* buffer, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemDirectoryW", SetLastError = true)]
    public static partial uint GetSystemDirectory(char* buffer, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetDefaultDllDirectories(uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetStdHandle(int which, nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreatePipe(out nint read, out nint write, SecurityAttributes* attributes, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateDirectory(string path, SecurityAttributes* attributes);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(nint token, int informationClass, void* information, uint length, out uint returnLength);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertSidToStringSid(nint sid, out nint text);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision, out nint securityDescriptor, nint size);

    [LibraryImport("advapi32.dll", EntryPoint = "GetNamedSecurityInfoW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetNamedSecurityInfo(string name, int objectType, uint information, out nint owner, out nint group, out nint dacl, out nint sacl, out nint securityDescriptor);

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShellExecuteEx(ShellExecuteInfo* info);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint flags);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationProcess(nint process, int informationClass, void* information, uint length, out uint returnLength);

    /// <summary>The string form of a SID, such as <c>S-1-5-32-544</c>; null if it cannot be converted.</summary>
    public static string? SidToString(nint sid)
    {
        if (sid == 0 || !ConvertSidToStringSid(sid, out var text)) return null;
        try { return Marshal.PtrToStringUni(text); }
        finally { LocalFree(text); }
    }

    /// <summary>The user SID of this process's token.</summary>
    public static string? CurrentUserSid()
    {
        if (!OpenProcessToken(CurrentProcess, TokenQuery, out var token)) return null;
        try
        {
            var buffer = stackalloc byte[256];
            // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field is the SID pointer.
            return GetTokenInformation(token, TokenUser, buffer, 256, out _) ? SidToString(*(nint*)buffer) : null;
        }
        finally { CloseHandle(token); }
    }

    /// <summary>Whether this process's token is elevated; null if it cannot be read.</summary>
    public static bool? IsElevated()
    {
        if (!OpenProcessToken(CurrentProcess, TokenQuery, out var token)) return null;
        try
        {
            uint elevation;
            return GetTokenInformation(token, TokenElevation, &elevation, sizeof(uint), out _) ? elevation != 0 : null;
        }
        finally { CloseHandle(token); }
    }

    /// <summary>The ID of the process that created this one; null if it cannot be read.</summary>
    public static uint? ParentProcessId()
    {
        ProcessBasicInformation information;
        return NtQueryInformationProcess(CurrentProcess, 0, &information, (uint)sizeof(ProcessBasicInformation), out _) == 0
            ? (uint)information.InheritedFromUniqueProcessId
            : null;
    }

    /// <summary>The image path of process <paramref name="processId"/>; null if it cannot be opened.</summary>
    public static string? ProcessImagePath(uint processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == 0) return null;
        using var process = new SafeProcessHandle(handle, ownsHandle: true);
        var buffer = stackalloc char[32768];
        uint size = 32768;
        return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    public static string? WindowsDirectory()
    {
        var buffer = stackalloc char[1024];
        var length = GetSystemWindowsDirectory(buffer, 1024);
        return length is > 0 and < 1024 ? new string(buffer, 0, (int)length) : null;
    }

    public static string? SystemDirectory()
    {
        var buffer = stackalloc char[1024];
        var length = GetSystemDirectory(buffer, 1024);
        return length is > 0 and < 1024 ? new string(buffer, 0, (int)length) : null;
    }
}
