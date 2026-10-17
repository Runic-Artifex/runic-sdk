using System.Runtime.InteropServices;

namespace Runic.Platform.Runtime;

// The two operations a file transfer needs from the operating system beyond System.IO: a rename that never replaces
// an entry and reports another file system instead of copying, and the type of an entry, so that a named pipe or a
// device is never opened.
internal static partial class FileTransferNative
{
    internal enum RenameStatus { Done, Exists, CrossDevice, Failed }

    internal readonly record struct RenameResult(RenameStatus Status, PlatformFailureCode Code = PlatformFailureCode.IoError, PlatformDiagnostic? Diagnostic = null);

    // Renames source to target, never replacing an existing target: Exists. Another file system or volume is
    // CrossDevice; nothing is copied.
    internal static RenameResult Rename(string source, string target)
    {
        if (OperatingSystem.IsWindows()) return RenameWindows(source, target);
        int error;
        try
        {
            error = OperatingSystem.IsMacOS()
                ? RenameExclusive(source, target, 0x4 /* RENAME_EXCL */) == 0 ? 0 : Marshal.GetLastPInvokeError()
                : RenameAt2(-100 /* AT_FDCWD */, source, -100, target, 1 /* RENAME_NOREPLACE */) == 0 ? 0 : Marshal.GetLastPInvokeError();
        }
        catch (EntryPointNotFoundException) { error = Unsupported; }
        // A file system or C library without an exclusive rename: check, then rename. An entry created in between,
        // which only another program could do, would be replaced if it is a file or an empty directory.
        if (error == Unsupported || error == EINVAL && !IsDirectoryIntoItself(source, target) || OperatingSystem.IsMacOS() && error == 45 /* ENOTSUP */
            || OperatingSystem.IsLinux() && error == 38 /* ENOSYS */)
        {
            if (Exists(target)) return new(RenameStatus.Exists);
            error = RenamePlain(source, target) == 0 ? 0 : Marshal.GetLastPInvokeError();
        }
        return Unix(error);
    }

    private const int Unsupported = -1, EINVAL = 22;

    private static bool IsDirectoryIntoItself(string source, string target) =>
        target.StartsWith(Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static RenameResult Unix(int error)
    {
        bool mac = OperatingSystem.IsMacOS();
        return error switch
        {
            0 => new(RenameStatus.Done),
            17 => new(RenameStatus.Exists), // EEXIST
            _ when error == (mac ? 66 : 39) => new(RenameStatus.Exists), // ENOTEMPTY: replacing a directory
            18 => new(RenameStatus.CrossDevice), // EXDEV
            _ => new(RenameStatus.Failed, CodeForErrno(error), PlatformDiagnostic.FromErrno(error)),
        };
    }

    internal static PlatformFailureCode CodeForErrno(int error) => error switch
    {
        2 or 20 => PlatformFailureCode.NotFound,                    // ENOENT, ENOTDIR
        1 or 13 or 30 => PlatformFailureCode.PermissionDenied,      // EPERM, EACCES, EROFS
        16 or 26 => PlatformFailureCode.ResourceBusy,               // EBUSY, ETXTBSY
        22 => PlatformFailureCode.InvalidData,                      // EINVAL: a directory into itself
        _ => PlatformFailureCode.IoError,
    };

    private static RenameResult RenameWindows(string source, string target)
    {
        // Neither MOVEFILE_REPLACE_EXISTING nor MOVEFILE_COPY_ALLOWED.
        if (MoveFileEx(source, target, 0)) return new(RenameStatus.Done);
        int error = Marshal.GetLastPInvokeError();
        return error switch
        {
            80 or 183 => new(RenameStatus.Exists),       // ERROR_FILE_EXISTS, ERROR_ALREADY_EXISTS
            17 => new(RenameStatus.CrossDevice),         // ERROR_NOT_SAME_DEVICE
            _ => new(RenameStatus.Failed, CodeForWin32(error), PlatformDiagnostic.FromWin32Error(error)),
        };
    }

    internal static PlatformFailureCode CodeForWin32(int error) => error switch
    {
        2 or 3 => PlatformFailureCode.NotFound,                     // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND
        5 or 19 => PlatformFailureCode.PermissionDenied,            // ERROR_ACCESS_DENIED, ERROR_WRITE_PROTECT
        32 or 33 => PlatformFailureCode.ResourceBusy,               // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
        _ => PlatformFailureCode.IoError,
    };

    // Whether an entry, a dangling link included, exists at the path.
    internal static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    // Whether a non-directory entry that is not a link is a regular file; false for named pipes, sockets and devices.
    // Windows paths reach no such entries. Unknown counts as regular: opening it reports the error.
    internal static unsafe bool IsRegularFile(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // statx has one layout on every architecture: stx_mode at 28.
                byte* buffer = stackalloc byte[256];
                if (StatX(-100, path, 0x100 /* AT_SYMLINK_NOFOLLOW */, 0x1 /* STATX_TYPE */, buffer) != 0) return true;
                return (*(ushort*)(buffer + 28) & 0xF000) == 0x8000;
            }
            if (OperatingSystem.IsMacOS())
            {
                // struct stat with 64-bit inodes, the only layout on arm64 and lstat$INODE64 on x64: st_mode at 4.
                byte* buffer = stackalloc byte[256];
                int status = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? LStat64(path, buffer) : LStat(path, buffer);
                if (status != 0) return true;
                return (*(ushort*)(buffer + 4) & 0xF000) == 0x8000;
            }
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException) { }
        return true;
    }

    [LibraryImport("libc", EntryPoint = "renameat2", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameAt2(int sourceDirectory, string source, int targetDirectory, string target, uint flags);
    [LibraryImport("libc", EntryPoint = "renamex_np", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameExclusive(string source, string target, uint flags);
    [LibraryImport("libc", EntryPoint = "rename", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenamePlain(string source, string target);
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int StatX(int directory, string path, int flags, uint mask, byte* buffer);
    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int LStat(string path, byte* buffer);
    [LibraryImport("libc", EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int LStat64(string path, byte* buffer);
    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string source, string target, uint flags);
}
