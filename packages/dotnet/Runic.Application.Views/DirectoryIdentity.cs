using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Runic.Application.Views;

// Identifies a directory beyond its path, so a watch on a directory that was deleted and created again is noticed:
// inotify and FSEvents keep a watch on the deleted directory and report nothing for its replacement.
internal static partial class DirectoryIdentity
{
    private static volatile bool _unsupported;

    // The device and inode on Unix, the volume serial number and file index on Windows; null when unknown.
    public static string? Read(string directory)
    {
        if (_unsupported) return null;
        try
        {
            if (OperatingSystem.IsWindows()) return ReadWindows(directory);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return ReadUnix(directory);
            return null;
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            // glibc before 2.33 exports only __xstat; such a system keeps the watch without the identity check.
            _unsupported = true;
            return null;
        }
    }

    // struct stat starts with st_dev at offset 0 (8 bytes on Linux, 4 on macOS) and st_ino (8 bytes) at offset 8 on
    // Linux x64 and arm64 and on macOS with 64-bit inodes, the only layout of arm64 macOS.
    private static unsafe string? ReadUnix(string directory)
    {
        var buffer = stackalloc byte[256];
        var result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? StatInode64(directory, buffer)
            : Stat(directory, buffer);
        if (result != 0) return null;
        var span = new ReadOnlySpan<byte>(buffer, 16);
        var device = OperatingSystem.IsMacOS()
            ? BinaryPrimitives.ReadUInt32LittleEndian(span) : BinaryPrimitives.ReadUInt64LittleEndian(span);
        var inode = BinaryPrimitives.ReadUInt64LittleEndian(span[8..]);
        return $"{device}:{inode}";
    }

    private static unsafe string? ReadWindows(string directory)
    {
        const uint FileReadAttributes = 0x80;
        const uint ShareAll = 0x7;
        const uint OpenExisting = 3;
        const uint BackupSemantics = 0x02000000;
        var handle = CreateFile(directory, FileReadAttributes, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        if (handle == -1) return null;
        try
        {
            // BY_HANDLE_FILE_INFORMATION: dwVolumeSerialNumber at 28, nFileIndexHigh at 44, nFileIndexLow at 48.
            var buffer = stackalloc byte[52];
            if (GetFileInformationByHandle(handle, buffer) == 0) return null;
            var span = new ReadOnlySpan<byte>(buffer, 52);
            return $"{BinaryPrimitives.ReadUInt32LittleEndian(span[28..])}:{BinaryPrimitives.ReadUInt32LittleEndian(span[44..])}:{BinaryPrimitives.ReadUInt32LittleEndian(span[48..])}";
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int Stat(string path, byte* buffer);

    [LibraryImport("libc", EntryPoint = "stat$INODE64", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int StatInode64(string path, byte* buffer);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetFileInformationByHandle(nint handle, byte* information);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);
}
