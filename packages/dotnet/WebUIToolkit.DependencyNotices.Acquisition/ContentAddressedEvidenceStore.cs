using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Evidence;

namespace WebUIToolkit.DependencyNotices.Acquisition;

public sealed class ContentAddressedEvidenceStore
{
    private const int LinuxAtCurrentWorkingDirectory = -100;
    private const int LinuxAtSymlinkFollow = 0x400;
    private const int LinuxCreate = 0x40;
    private const int LinuxExclusive = 0x80;
    private const int LinuxCloseOnExec = 0x0008_0000;
    private const int LinuxDirectory = 0x0001_0000;
    private const int LinuxNoFollow = 0x0002_0000;
    private const int LinuxWriteOnly = 1;
    private const int LinuxLockShared = 1;
    private const int LinuxLockNonBlocking = 4;
    private const int LinuxWouldBlock = 11;
    private const long LinuxSystemCallOpenAt2 = 437;
    private const ulong LinuxResolveNoMagicLinks = 0x02;
    private const ulong LinuxResolveNoSymbolicLinks = 0x04;
    private const ulong LinuxResolveBeneath = 0x08;
    private const int MacGetPath = 50;
    private const int MacMaximumPathBytes = 1_024;
    private readonly string _sha256Directory;

    public ContentAddressedEvidenceStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        _sha256Directory = Path.Combine(RootDirectory, "sha256");
        EnsureDirectorySafe(RootDirectory);
        EnsureDirectorySafe(_sha256Directory);
    }

    public string RootDirectory { get; }

    public string GetPath(string sha256)
    {
        ValidateDigest(sha256);
        return Path.Combine(_sha256Directory, sha256);
    }

    public bool Contains(string sha256) => File.Exists(GetPath(sha256));

    public async ValueTask<CacheCommitResult> CommitAsync(
        Stream content,
        string expectedSha256,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateDigest(expectedSha256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        EnsureDirectorySafe(_sha256Directory);
        if (OperatingSystem.IsLinux())
        {
            return await CommitLinuxAnchoredAsync(
                content,
                expectedSha256,
                maximumBytes,
                cancellationToken).ConfigureAwait(false);
        }

        string destinationPath = GetPath(expectedSha256);
        if (File.Exists(destinationPath))
        {
            long existingLength = await VerifyExistingAsync(
                destinationPath,
                expectedSha256,
                maximumBytes,
                cancellationToken).ConfigureAwait(false);
            return new CacheCommitResult(destinationPath, existingLength, true);
        }

        string temporaryPath = Path.Combine(_sha256Directory, ".tmp-" + Guid.NewGuid().ToString("N"));
        long byteCount = 0;
        string actualSha256;
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (FileStream output = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                EnsureOpenedPathSafe(output.SafeFileHandle, temporaryPath);
                byte[] buffer = new byte[64 * 1024];
                while (true)
                {
                    int read = await content.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    byteCount = checked(byteCount + read);
                    if (byteCount > maximumBytes)
                    {
                        throw new AcquisitionException(
                            NoticeDiagnosticCodes.AcquisitionSizeLimit,
                            $"Acquired evidence exceeded the configured {maximumBytes} byte limit.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            actualSha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(expectedSha256, actualSha256, StringComparison.Ordinal))
            {
                throw new AcquisitionException(
                    NoticeDiagnosticCodes.AcquisitionDigestMismatch,
                    $"Acquired evidence digest mismatch. Expected '{expectedSha256}', actual '{actualSha256}'.");
            }

            bool published = false;
            try
            {
                published = TryCreateHardLink(temporaryPath, destinationPath);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                // A hard-link create is an atomic no-replace publication primitive.
                // Another store instance or process won this digest.
            }

            if (!published)
            {
                await VerifyExistingAsync(
                    destinationPath,
                    expectedSha256,
                    maximumBytes,
                    cancellationToken).ConfigureAwait(false);
                File.Delete(temporaryPath);
                return new CacheCommitResult(destinationPath, byteCount, true);
            }

            await VerifyExistingAsync(
                destinationPath,
                expectedSha256,
                maximumBytes,
                cancellationToken).ConfigureAwait(false);
            File.Delete(temporaryPath);
            return new CacheCommitResult(destinationPath, byteCount, false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async ValueTask<CacheCommitResult> CommitLinuxAnchoredAsync(
        Stream content,
        string expectedSha256,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using SafeFileHandle directoryHandle = OpenLinuxDirectoryAnchored(_sha256Directory);
        string destinationPath = GetPath(expectedSha256);
        SafeFileHandle? existingHandle = TryOpenLinuxAt(directoryHandle, expectedSha256, write: false);
        if (existingHandle is not null)
        {
            using (existingHandle)
            {
                long existingLength = await VerifyLinuxHandleAsync(
                    existingHandle,
                    expectedSha256,
                    maximumBytes,
                    cancellationToken).ConfigureAwait(false);
                return new CacheCommitResult(destinationPath, existingLength, true);
            }
        }

        // Preserve a real asynchronous race boundary before publication. Besides keeping
        // large and small inputs behaviorally consistent, this lets concurrent contenders
        // independently validate their supplied bytes before one wins the no-replace link.
        await Task.Yield();

        string temporaryName = ".tmp-" + Guid.NewGuid().ToString("N");
        SafeFileHandle temporaryHandle = OpenLinuxTemporaryAt(directoryHandle, temporaryName);
        long byteCount = 0;
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(
                temporaryHandle,
                FileAccess.Write,
                bufferSize: 64 * 1024,
                isAsync: false))
            {
                temporaryHandle = null!;
                byte[] buffer = new byte[64 * 1024];
                while (true)
                {
                    int read = await content.ReadAsync(buffer.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    byteCount = checked(byteCount + read);
                    if (byteCount > maximumBytes)
                    {
                        throw new AcquisitionException(
                            NoticeDiagnosticCodes.AcquisitionSizeLimit,
                            $"Acquired evidence exceeded the configured {maximumBytes} byte limit.");
                    }

                    hash.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }

                output.Flush(flushToDisk: true);
                string actualSha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!string.Equals(expectedSha256, actualSha256, StringComparison.Ordinal))
                {
                    throw new AcquisitionException(
                        NoticeDiagnosticCodes.AcquisitionDigestMismatch,
                        $"Acquired evidence digest mismatch. Expected '{expectedSha256}', actual '{actualSha256}'.");
                }

                bool published = TryPublishLinuxHandle(
                    output.SafeFileHandle,
                    directoryHandle,
                    expectedSha256);
                if (!published)
                {
                    using SafeFileHandle winner = TryOpenLinuxAt(
                        directoryHandle,
                        expectedSha256,
                        write: false)
                        ?? throw new IOException("The concurrently published evidence entry disappeared.");
                    await VerifyLinuxHandleAsync(
                        winner,
                        expectedSha256,
                        maximumBytes,
                        cancellationToken).ConfigureAwait(false);
                    return new CacheCommitResult(destinationPath, byteCount, true);
                }

                using SafeFileHandle publishedHandle = TryOpenLinuxAt(
                    directoryHandle,
                    expectedSha256,
                    write: false)
                    ?? throw new IOException("The published evidence entry disappeared.");
                await VerifyLinuxHandleAsync(
                    publishedHandle,
                    expectedSha256,
                    maximumBytes,
                    cancellationToken).ConfigureAwait(false);
                return new CacheCommitResult(destinationPath, byteCount, false);
            }
        }
        finally
        {
            temporaryHandle?.Dispose();
            UnlinkLinuxAt(directoryHandle, temporaryName);
        }
    }

    private static async ValueTask<long> VerifyLinuxHandleAsync(
        SafeFileHandle handle,
        string expectedSha256,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await AcquireLinuxSharedLockAsync(handle, cancellationToken).ConfigureAwait(false);
        await using var stream = new FileStream(
            handle,
            FileAccess.Read,
            bufferSize: 64 * 1024,
            isAsync: false);
        if (stream.Length > maximumBytes)
        {
            throw new AcquisitionException(
                NoticeDiagnosticCodes.AcquisitionSizeLimit,
                $"An existing evidence cache entry exceeded the configured {maximumBytes} byte limit.");
        }

        byte[] actual = await Task.Run(
            () => SHA256.HashData(stream),
            cancellationToken).ConfigureAwait(false);
        string actualSha256 = Convert.ToHexStringLower(actual);
        if (!string.Equals(expectedSha256, actualSha256, StringComparison.Ordinal))
        {
            throw new AcquisitionException(
                NoticeDiagnosticCodes.AcquisitionDigestMismatch,
                $"The existing cache entry for '{expectedSha256}' contains different bytes.");
        }

        return stream.Length;
    }

    private static async ValueTask AcquireLinuxSharedLockAsync(
        SafeFileHandle handle,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 8;
        for (int attempt = 0; ; attempt++)
        {
            if (LinuxFlock(
                handle.DangerousGetHandle().ToInt32(),
                LinuxLockShared | LinuxLockNonBlocking) == 0)
            {
                return;
            }

            int error = Marshal.GetLastPInvokeError();
            if (error != LinuxWouldBlock || attempt >= maximumAttempts - 1)
            {
                throw new IOException(
                    "An evidence cache entry could not be locked for verification.",
                    new System.ComponentModel.Win32Exception(error));
            }

            int delayMilliseconds = Math.Min(10 << attempt, 160);
            await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    private static SafeFileHandle OpenLinuxDirectoryAnchored(string directory)
    {
        int fileSystemRootDescriptor = LinuxOpenPath(
            "/",
            LinuxDirectory | LinuxNoFollow | LinuxCloseOnExec);
        if (fileSystemRootDescriptor < 0)
        {
            throw LinuxIOException("The filesystem root could not be anchored.");
        }

        using var fileSystemRoot =
            new SafeFileHandle((nint)fileSystemRootDescriptor, ownsHandle: true);
        string relativePath = Path.GetFullPath(directory)
            .TrimStart(Path.DirectorySeparatorChar);
        if (relativePath.Length == 0)
        {
            relativePath = ".";
        }

        var how = new LinuxOpenHow
        {
            Flags = (ulong)(LinuxDirectory | LinuxCloseOnExec),
            Resolve = LinuxResolveBeneath
                | LinuxResolveNoMagicLinks
                | LinuxResolveNoSymbolicLinks,
        };
        long descriptor = LinuxOpenAt2Path(
            fileSystemRoot.DangerousGetHandle().ToInt32(),
            relativePath,
            ref how);
        if (descriptor < 0)
        {
            throw LinuxIOException(
                "The evidence cache directory and all of its ancestors must not be links.");
        }

        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static SafeFileHandle? TryOpenLinuxAt(
        SafeFileHandle directory,
        string name,
        bool write)
    {
        var how = new LinuxOpenHow
        {
            Flags = (ulong)(LinuxCloseOnExec | LinuxNoFollow | (write ? LinuxWriteOnly : 0)),
            Resolve = LinuxResolveBeneath
                | LinuxResolveNoMagicLinks
                | LinuxResolveNoSymbolicLinks,
        };
        long descriptor = LinuxOpenAt2Path(
            directory.DangerousGetHandle().ToInt32(),
            name,
            ref how);
        if (descriptor >= 0)
        {
            return new SafeFileHandle((nint)descriptor, ownsHandle: true);
        }

        if (Marshal.GetLastPInvokeError() == 2)
        {
            return null;
        }

        throw LinuxIOException("An evidence cache entry could not be opened safely.");
    }

    private static SafeFileHandle OpenLinuxTemporaryAt(
        SafeFileHandle directory,
        string name)
    {
        nint namePointer = Marshal.StringToCoTaskMemUTF8(name);
        int descriptor;
        try
        {
            descriptor = LinuxOpenAt(
                directory.DangerousGetHandle().ToInt32(),
                namePointer,
                LinuxWriteOnly
                    | LinuxCreate
                    | LinuxExclusive
                    | LinuxNoFollow
                    | LinuxCloseOnExec,
                0x180);
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePointer);
        }

        if (descriptor < 0)
        {
            throw LinuxIOException("A temporary evidence cache entry could not be created safely.");
        }

        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static bool TryPublishLinuxHandle(
        SafeFileHandle file,
        SafeFileHandle directory,
        string destinationName)
    {
        string descriptorPath =
            "/proc/self/fd/" +
            file.DangerousGetHandle().ToInt64().ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        nint sourcePointer = Marshal.StringToCoTaskMemUTF8(descriptorPath);
        nint destinationPointer = Marshal.StringToCoTaskMemUTF8(destinationName);
        int result;
        try
        {
            result = LinuxLinkAt(
                LinuxAtCurrentWorkingDirectory,
                sourcePointer,
                directory.DangerousGetHandle().ToInt32(),
                destinationPointer,
                LinuxAtSymlinkFollow);
        }
        finally
        {
            Marshal.FreeCoTaskMem(destinationPointer);
            Marshal.FreeCoTaskMem(sourcePointer);
        }

        if (result == 0)
        {
            return true;
        }

        if (Marshal.GetLastPInvokeError() == 17)
        {
            return false;
        }

        throw LinuxIOException("The evidence cache entry could not be published atomically.");
    }

    private static void UnlinkLinuxAt(SafeFileHandle directory, string name)
    {
        nint namePointer = Marshal.StringToCoTaskMemUTF8(name);
        int result;
        try
        {
            result = LinuxUnlinkAt(
                directory.DangerousGetHandle().ToInt32(),
                namePointer,
                0);
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePointer);
        }

        if (result != 0 && Marshal.GetLastPInvokeError() != 2)
        {
            throw LinuxIOException("A temporary evidence cache entry could not be removed.");
        }
    }

    private static int LinuxOpenPath(string path, int flags)
    {
        nint pathPointer = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            return LinuxOpen(pathPointer, flags);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    private static long LinuxOpenAt2Path(
        int directoryDescriptor,
        string path,
        ref LinuxOpenHow how)
    {
        nint pathPointer = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            return LinuxOpenAt2(
                LinuxSystemCallOpenAt2,
                directoryDescriptor,
                pathPointer,
                ref how,
                (nuint)Marshal.SizeOf<LinuxOpenHow>());
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    private static IOException LinuxIOException(string message) =>
        new(message, new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));

    private static async ValueTask<long> VerifyExistingAsync(
        string path,
        string expectedSha256,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 8;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                EnsureOpenedPathSafe(stream.SafeFileHandle, path);
                if (stream.Length > maximumBytes)
                {
                    throw new AcquisitionException(
                        NoticeDiagnosticCodes.AcquisitionSizeLimit,
                        $"An existing evidence cache entry exceeded the configured {maximumBytes} byte limit.");
                }

                byte[] actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                string actualSha256 = Convert.ToHexStringLower(actual);
                if (!string.Equals(expectedSha256, actualSha256, StringComparison.Ordinal))
                {
                    throw new AcquisitionException(
                        NoticeDiagnosticCodes.AcquisitionDigestMismatch,
                        $"The existing cache entry for '{expectedSha256}' contains different bytes.");
                }

                return stream.Length;
            }
            catch (IOException) when (attempt < maximumAttempts - 1)
            {
                // A concurrent atomic move can make the destination visible before Windows
                // releases its final sharing handle. Preserve verification and retry briefly.
                int delayMilliseconds = Math.Min(10 << attempt, 160);
                await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void EnsureOpenedPathSafe(SafeFileHandle handle, string declaredPath)
    {
        string resolvedPath;
        if (OperatingSystem.IsLinux())
        {
            string descriptorPath =
                "/proc/self/fd/" + handle.DangerousGetHandle().ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            resolvedPath = new FileInfo(descriptorPath)
                .ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new IOException("An evidence cache handle could not be resolved.");
        }
        else if (OperatingSystem.IsWindows())
        {
            resolvedPath = NormalizeWindowsPath(GetWindowsFinalPath(handle));
        }
        else if (OperatingSystem.IsMacOS())
        {
            resolvedPath = GetMacFinalPath(handle);
        }
        else
        {
            throw new PlatformNotSupportedException(
                "This platform does not expose the file-handle path verification required by the evidence cache.");
        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(
            Path.GetFullPath(declaredPath),
            Path.GetFullPath(resolvedPath),
            comparison))
        {
            throw new AcquisitionException(
                NoticeDiagnosticCodes.AcquisitionOriginBlocked,
                "Evidence cache paths and their ancestors cannot be symbolic links or reparse points.");
        }
    }

    private static void ValidateDigest(string digest)
    {
        if (!EvidenceDigest.IsCanonicalSha256(digest))
        {
            throw new ArgumentException("A lowercase canonical SHA-256 digest is required.", nameof(digest));
        }
    }

    private static void EnsureDirectorySafe(string directory)
    {
        if (Directory.Exists(directory))
        {
            FileAttributes attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new AcquisitionException(
                    NoticeDiagnosticCodes.AcquisitionOriginBlocked,
                    "The evidence cache directory cannot be a symbolic link or reparse point.");
            }
        }
        else
        {
            Directory.CreateDirectory(directory);
        }

        string fullPath = Path.GetFullPath(directory);
        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new AcquisitionException(
                NoticeDiagnosticCodes.AcquisitionOriginBlocked,
                "The evidence cache directory must have an anchored root.");
        }

        string current = root;
        foreach (string component in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new AcquisitionException(
                    NoticeDiagnosticCodes.AcquisitionOriginBlocked,
                    "Evidence cache directories and their ancestors cannot be symbolic links or reparse points.");
            }
        }
    }

    private static string GetWindowsFinalPath(SafeFileHandle handle)
    {
        char[] buffer = new char[512];
        uint length = WindowsGetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0)
        {
            throw new IOException("An evidence cache handle could not be resolved.");
        }

        if (length >= buffer.Length)
        {
            buffer = new char[checked((int)length + 1)];
            length = WindowsGetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length)
            {
                throw new IOException("An evidence cache handle path exceeded platform limits.");
            }
        }

        return new string(buffer, 0, checked((int)length));
    }

    private static string GetMacFinalPath(SafeFileHandle handle)
    {
        nint buffer = Marshal.AllocHGlobal(MacMaximumPathBytes);
        try
        {
            if (MacFcntl(handle.DangerousGetHandle().ToInt32(), MacGetPath, buffer) != 0)
            {
                throw new IOException("An evidence cache handle could not be resolved.");
            }

            return Marshal.PtrToStringUTF8(buffer)
                ?? throw new IOException("An evidence cache handle returned an empty path.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string NormalizeWindowsPath(string path)
    {
        const string extendedPrefix = @"\\?\";
        const string uncPrefix = @"\\?\UNC\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
    }

    private static bool TryCreateHardLink(string existingPath, string linkPath)
    {
        bool succeeded;
        int error;
        if (OperatingSystem.IsWindows())
        {
            succeeded = WindowsCreateHardLink(linkPath, existingPath, 0);
            error = succeeded ? 0 : Marshal.GetLastPInvokeError();
        }
        else
        {
            nint existingPointer = Marshal.StringToCoTaskMemUTF8(existingPath);
            nint linkPointer = Marshal.StringToCoTaskMemUTF8(linkPath);
            try
            {
                succeeded = UnixCreateHardLink(existingPointer, linkPointer) == 0;
                error = succeeded ? 0 : Marshal.GetLastPInvokeError();
            }
            finally
            {
                Marshal.FreeCoTaskMem(linkPointer);
                Marshal.FreeCoTaskMem(existingPointer);
            }
        }

        if (succeeded)
        {
            return true;
        }

        if (File.Exists(linkPath))
        {
            return false;
        }

        throw new IOException(
            "The evidence cache entry could not be published atomically.",
            new System.ComponentModel.Win32Exception(error));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxOpenHow
    {
        internal ulong Flags;
        internal ulong Mode;
        internal ulong Resolve;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint WindowsGetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] path,
        uint pathLength,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowsCreateHardLink(
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        [MarshalAs(UnmanagedType.LPWStr)] string existingFileName,
        nint securityAttributes);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int MacFcntl(int descriptor, int command, nint buffer);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int UnixCreateHardLink(nint existingPath, nint linkPath);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int LinuxFlock(int descriptor, int operation);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int LinuxOpen(nint path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int LinuxOpenAt(
        int directoryDescriptor,
        nint path,
        int flags,
        uint mode);

    [DllImport("libc", EntryPoint = "linkat", SetLastError = true)]
    private static extern int LinuxLinkAt(
        int sourceDirectoryDescriptor,
        nint sourcePath,
        int destinationDirectoryDescriptor,
        nint destinationPath,
        int flags);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int LinuxUnlinkAt(
        int directoryDescriptor,
        nint path,
        int flags);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long LinuxOpenAt2(
        long number,
        int directoryDescriptor,
        nint path,
        ref LinuxOpenHow how,
        nuint size);
}

public readonly record struct CacheCommitResult(string Path, long ByteCount, bool WasAlreadyCached);
