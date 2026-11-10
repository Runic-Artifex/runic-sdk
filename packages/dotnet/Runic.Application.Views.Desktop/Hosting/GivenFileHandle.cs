using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Runic.Application.Views.Desktop.Hosting;

// window-services/1, launch (host-protocol.md, Opening files and links; T16): a file the user gave the guest, opened
// for its launch, so the host checks and launches one file rather than a path twice. The identity read through the
// handle is the start of DirectoryIdentity's, the given file's: device and inode on Linux (the birth time, which tells a
// reused inode apart, is checked by path right after), volume and file index on Windows.
//   - Linux: a descriptor, which the portal's OpenURI.OpenFile receives instead of the path.
//   - Windows: a handle that shares neither delete nor, unless another process writes the file, write access, held
//     across ShellExecute: while the host launches, the path cannot be renamed, replaced or deleted.
//   - macOS: none. LaunchServices takes a URL, not a descriptor, so the host checks the identity by path right before
//     the launch (Open returns null without failing).
internal sealed partial class GivenFileHandle : IDisposable
{
    private GivenFileHandle(SafeFileHandle handle, string? identity)
    {
        Handle = handle;
        Identity = identity;
    }

    internal SafeFileHandle Handle { get; }

    // The opened file's identity; null where it is unknown.
    internal string? Identity { get; }

    // Whether the opened file is the given one, whose identity is given (DirectoryIdentity). Where the handle's identity
    // is unknown, as on a C library without fstat, the check by path alone decides.
    internal bool IsFile(string? given) => Identity is null || given is not null &&
        (given == Identity || given.StartsWith(Identity + ":", StringComparison.Ordinal));

    public void Dispose() => Handle.Dispose();

    // Whether the platform launches through the handle or holds it across the launch.
    internal static bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsWindows();

    // The opened file, or null with the reason: inUse when Windows refuses it because another process may delete or
    // rename it now.
    internal static GivenFileHandle? Open(string path, out bool inUse)
    {
        inUse = false;
        if (OperatingSystem.IsLinux()) return OpenLinux(path);
        if (OperatingSystem.IsWindows()) return OpenWindows(path, out inUse);
        return null;
    }

    private static GivenFileHandle? OpenLinux(string path)
    {
        // O_RDONLY | O_CLOEXEC | O_NONBLOCK, as the portal launcher opens it: a special file must not block the host.
        var handle = new SafeFileHandle(OpenUnix(path, 0x80000 | 0x800), ownsHandle: true);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return new GivenFileHandle(handle, ReadUnixIdentity(handle));
    }

    // struct stat starts with st_dev (8 bytes) and st_ino (8 bytes) on Linux x64 and arm64, as DirectoryIdentity reads it.
    private static unsafe string? ReadUnixIdentity(SafeFileHandle handle)
    {
        var buffer = stackalloc byte[256];
        try
        {
            if (FileStatus((int)handle.DangerousGetHandle(), buffer) != 0) return null;
        }
        catch (EntryPointNotFoundException) { return null; }
        var span = new ReadOnlySpan<byte>(buffer, 16);
        return $"{BinaryPrimitives.ReadUInt64LittleEndian(span)}:{BinaryPrimitives.ReadUInt64LittleEndian(span[8..])}";
    }

    private const uint FileReadData = 0x1, FileReadAttributes = 0x80, Synchronize = 0x100000;
    private const uint FileShareRead = 0x1, FileShareWrite = 0x2;
    private const uint OpenExisting = 3, BackupSemantics = 0x02000000;
    private const int SharingViolation = 32;

    private static unsafe GivenFileHandle? OpenWindows(string path, out bool inUse)
    {
        inUse = false;
        // Data access, so the handle takes part in the sharing checks: an open for delete, rename or replace fails while
        // it is held. A file another process writes is shared for write, which still keeps its path.
        var handle = CreateFile(path, FileReadData | FileReadAttributes | Synchronize, FileShareRead, 0, OpenExisting, BackupSemantics, 0);
        if (handle.IsInvalid && Marshal.GetLastPInvokeError() == SharingViolation)
        {
            handle.Dispose();
            handle = CreateFile(path, FileReadData | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite, 0, OpenExisting, BackupSemantics, 0);
        }
        if (handle.IsInvalid)
        {
            inUse = Marshal.GetLastPInvokeError() == SharingViolation;
            handle.Dispose();
            return null;
        }
        // BY_HANDLE_FILE_INFORMATION: dwVolumeSerialNumber at 28, nFileIndexHigh at 44, nFileIndexLow at 48.
        var buffer = stackalloc byte[52];
        string? identity = null;
        if (GetFileInformationByHandle(handle, buffer) != 0)
        {
            var span = new ReadOnlySpan<byte>(buffer, 52);
            identity = $"{BinaryPrimitives.ReadUInt32LittleEndian(span[28..])}:{BinaryPrimitives.ReadUInt32LittleEndian(span[44..])}:{BinaryPrimitives.ReadUInt32LittleEndian(span[48..])}";
        }
        return new GivenFileHandle(handle, identity);
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenUnix(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fstat")]
    private static unsafe partial int FileStatus(int descriptor, byte* buffer);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetFileInformationByHandle(SafeFileHandle handle, byte* information);
}
