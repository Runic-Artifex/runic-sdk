using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Runic.Application.Views;

// Identifies a directory beyond its path, so a watch on a directory that was deleted and created again is noticed:
// inotify and FSEvents keep a watch on the deleted directory and report nothing for its replacement. Reads a file's
// identity the same way (a watched file's signature, a given file).
internal static partial class DirectoryIdentity
{
    private static volatile bool _unsupported;

    // The device, inode and birth time on Unix (the change time on a Linux file system without a birth time), the volume
    // serial number and file index on Windows; null when unknown.
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
            // A C library with neither stat nor glibc's older __xstat: callers go without the identity check.
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
            : OperatingSystem.IsLinux() ? StatLinux(directory, buffer) : Stat(directory, buffer);
        if (result != 0) return null;
        var span = new ReadOnlySpan<byte>(buffer, 16);
        var device = OperatingSystem.IsMacOS()
            ? BinaryPrimitives.ReadUInt32LittleEndian(span) : BinaryPrimitives.ReadUInt64LittleEndian(span);
        var inode = BinaryPrimitives.ReadUInt64LittleEndian(span[8..]);
        // ext4 hands a freed inode to the next directory created, so a replacement can keep device and inode; the birth
        // time tells them apart. macOS reports it as the creation time. On Linux, .NET reads the creation time through
        // lstat, which has no birth time, so it is the change time there, which moves with every entry created, renamed
        // or deleted in the directory (#1360): statx reads the birth time instead. A Linux file system that records none
        // (or a kernel before 4.11, or a C library without statx) keeps the change time, which tells a replacement apart
        // too but restarts a watch after each change of an entry.
        if (OperatingSystem.IsLinux() && ReadBirthTime(directory) is { } birth) return $"{device}:{inode}:{birth}";
        return $"{device}:{inode}:{Directory.GetCreationTimeUtc(directory).Ticks}";
    }

    private static volatile bool _noStatx;

    // The birth time from statx (Linux 4.11, glibc 2.28, musl 1.2.5) as "b{seconds}.{nanoseconds}", or null when the
    // file system, the kernel or the C library has none. struct statx: stx_mask at 0 says which fields the file
    // system filled in, and stx_btime (tv_sec, an int64, then tv_nsec, a uint32) is at 80.
    private static unsafe string? ReadBirthTime(string path)
    {
        if (_noStatx) return null;
        const int CurrentDirectory = -100; // AT_FDCWD
        const uint BirthTime = 0x800; // STATX_BTIME
        const int NotImplemented = 38; // ENOSYS
        var buffer = stackalloc byte[256];
        try
        {
            if (Statx(CurrentDirectory, path, 0, BirthTime, buffer) != 0)
            {
                if (Marshal.GetLastPInvokeError() == NotImplemented) _noStatx = true;
                return null;
            }
        }
        catch (EntryPointNotFoundException)
        {
            _noStatx = true;
            return null;
        }
        var span = new ReadOnlySpan<byte>(buffer, 96);
        if ((BinaryPrimitives.ReadUInt32LittleEndian(span) & BirthTime) == 0) return null;
        var seconds = BinaryPrimitives.ReadInt64LittleEndian(span[80..]);
        var nanoseconds = BinaryPrimitives.ReadUInt32LittleEndian(span[88..]);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"b{seconds}.{nanoseconds:D9}");
    }

    private static volatile bool _xstat;

    // glibc before 2.33 (such as RHEL 8 and Ubuntu 20.04) exports stat only as __xstat, which takes the structure's
    // version: 1 on x64, 0 on arm64, with the same layout as stat's.
    private static unsafe int StatLinux(string path, byte* buffer)
    {
        if (!_xstat)
        {
            try { return Stat(path, buffer); }
            catch (EntryPointNotFoundException) { _xstat = true; }
        }
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => XStat(1, path, buffer),
            Architecture.Arm64 => XStat(0, path, buffer),
            _ => throw new EntryPointNotFoundException("stat"),
        };
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

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int Statx(int directory, string path, int flags, uint mask, byte* buffer);

    [LibraryImport("libc", EntryPoint = "__xstat", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int XStat(int version, string path, byte* buffer);

    [LibraryImport("libc", EntryPoint = "stat$INODE64", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int StatInode64(string path, byte* buffer);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetFileInformationByHandle(nint handle, byte* information);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);
}
