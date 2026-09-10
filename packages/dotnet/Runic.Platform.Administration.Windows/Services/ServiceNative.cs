using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.Services;

internal sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal ServiceHandle(nint pointer) : base(true) => SetHandle(pointer);
    protected override bool ReleaseHandle() => ServiceNative.CloseServiceHandle(handle) != 0;
}

internal static unsafe partial class ServiceNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Status
    {
        internal uint Type, State, Controls, Win32Exit, ServiceExit, CheckPoint, WaitHint, ProcessId, Flags;
        internal readonly ServiceStatus Snapshot() => new((ServiceState)State, ProcessId, Controls, Win32Exit, ServiceExit, CheckPoint, TimeSpan.FromMilliseconds(WaitHint));
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Config
    {
        internal uint Type, Start, Error;
        internal char* BinaryPath;
        internal char* LoadOrderGroup;
        internal uint Tag;
        internal char* Dependencies;
        internal char* Account;
        internal char* DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EnumRow
    {
        internal char* Name;
        internal char* DisplayName;
        internal Status Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FailureActions
    {
        internal uint Reset;
        internal char* RebootMessage;
        internal char* Command;
        internal uint Count;
        internal FailureAction* Actions;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FailureAction
    {
        internal uint Kind, Delay;
    }

    internal static string Text(char* value) => value == null ? "" : new(value);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint OpenSCManager(string? machine, string? database, uint access);
    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint OpenService(ServiceHandle manager, string name, uint access);
    [LibraryImport("advapi32.dll")]
    internal static partial int CloseServiceHandle(nint service);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int QueryServiceStatusEx(ServiceHandle service, int level, Status* buffer, uint length, out uint needed);
    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    internal static partial int QueryServiceConfig(ServiceHandle service, byte* buffer, uint length, out uint needed);
    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
    internal static partial int QueryServiceConfig2(ServiceHandle service, uint level, byte* buffer, uint length, out uint needed);
    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true)]
    internal static partial int Enumerate(ServiceHandle manager, int level, uint type, uint state, byte* buffer, uint length, out uint needed, out uint count, ref uint resume, char* group);
    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateService(ServiceHandle manager, string name, string displayName, uint access, uint type, uint start, uint error, string binaryPath, string? group, nint tag, char* dependencies, string? account, string? password);
    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial int ChangeServiceConfig(ServiceHandle service, uint type, uint start, uint error, string? binaryPath, string? group, nint tag, char* dependencies, string? account, string? password, string? displayName);
    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    internal static partial int ChangeServiceConfig2(ServiceHandle service, uint level, void* data);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int DeleteService(ServiceHandle service);
    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    internal static partial int StartService(ServiceHandle service, uint count, nint arguments);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int ControlService(ServiceHandle service, uint control, Status* status);
}
