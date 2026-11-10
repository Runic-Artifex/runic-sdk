using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

// Whether another process holds, at the same value, a handle to the same kernel object as a handle of this process:
// what an inherited handle is. DuplicateHandle copies the value out of the other process, which fails when it names no
// handle there; CompareObjectHandles (Windows 10) tells whether the copy is the same object or another one that happens
// to have that value.
[SupportedOSPlatform("windows")]
internal static partial class InheritedHandles
{
    internal static bool Holds(int processId, SafeHandle handle)
    {
        using var process = OpenProcess(ProcessDuplicateHandle, false, processId);
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Process {processId} could not be opened.");
        var value = handle.DangerousGetHandle();
        if (!DuplicateHandle(process, value, GetCurrentProcess(), out var copy, 0, false, DuplicateSameAccess)) return false;
        try { return CompareObjectHandles(copy, value); }
        finally { _ = CloseHandle(copy); }
    }

    private const uint ProcessDuplicateHandle = 0x0040;
    private const uint DuplicateSameAccess = 0x2;

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [LibraryImport("kernel32")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(SafeProcessHandle sourceProcess, nint sourceHandle, nint targetProcess, out nint targetHandle,
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [LibraryImport("kernelbase")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CompareObjectHandles(nint first, nint second);

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
